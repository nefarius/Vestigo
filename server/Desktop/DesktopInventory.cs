using System.Diagnostics;
using System.Text.RegularExpressions;

using Vestigo.Configuration;
using Vestigo.Files;
using Vestigo.Processes;

namespace Vestigo.Desktop;

/// <summary>
///     Lists windows and displays on the service desktop or, through the session helper, on the console user's
///     desktop. Screenshots always run in the interactive session.
/// </summary>
public sealed class DesktopInventory(
    IProcessLauncher launcher,
    SessionHelperClient helper,
    FileDeployer deployer,
    VestigoOptions options,
    ILogger<DesktopInventory>? logger = null)
{
    internal const long HelperMaxOutputBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(30);

    public async Task<OperationResult<WindowListResult>> ListWindowsAsync(
        WindowSelector selector,
        LaunchContext context,
        bool elevated,
        CancellationToken cancellationToken,
        DesktopHelperTarget? desktop = null)
    {
        try
        {
            _ = WindowSelector.CompileTitle(selector.TitlePattern);
        }
        catch (RegexParseException ex)
        {
            return OperationResult<WindowListResult>.Fail("invalid_request", "titleRegex is not a valid regular expression: " + ex.Message);
        }

        if (context == LaunchContext.Service)
        {
            if (!OperatingSystem.IsWindows())
            {
                return OperationResult<WindowListResult>.Fail("windows_required", "Window enumeration requires Windows.");
            }

            try
            {
                WindowListResult listed = await Task.Run(() => WindowEnumerator.List(selector), cancellationToken).ConfigureAwait(false);
                return OperationResult<WindowListResult>.Ok(Stamp(listed, desktop));
            }
            catch (RegexMatchTimeoutException ex)
            {
                return OperationResult<WindowListResult>.Fail("invalid_request", "titleRegex timed out: " + ex.Message);
            }
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return available.FailAs<WindowListResult>();
        }

        OperationResult<WindowListResult> listedWindows = await helper.RunAsync<WindowListResult>(
            SessionHelper.BuildListWindowsArguments(selector),
            elevated,
            HelperTimeout,
            HelperMaxOutputBytes,
            cancellationToken,
            desktop?.DesktopPath).ConfigureAwait(false);
        return listedWindows.Success
            ? OperationResult<WindowListResult>.Ok(Stamp(listedWindows.Value!, desktop))
            : listedWindows;
    }

    public async Task<OperationResult<WindowInspectionReport>> InspectWindowAsync(
        string handle,
        int? expectedPid,
        DateTimeOffset? expectedStart,
        LaunchContext context,
        bool elevated,
        CancellationToken cancellationToken,
        DesktopHelperTarget? desktop = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OperationResult<WindowInspectionReport>.Fail("windows_required", "Window inspection requires Windows.");
        }

        if (context == LaunchContext.Service)
        {
            return await Task.Run(
                () => new WindowInspector(new WindowsWindowInspectionSource()).Inspect(handle, expectedPid, expectedStart),
                cancellationToken).ConfigureAwait(false);
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return available.FailAs<WindowInspectionReport>();
        }

        OperationResult<WindowInspectionReport> inspected = await helper.RunAsync<WindowInspectionReport>(
            SessionHelper.BuildInspectWindowArguments(handle, expectedPid, expectedStart),
            elevated,
            HelperTimeout,
            HelperMaxOutputBytes,
            cancellationToken,
            desktop?.DesktopPath).ConfigureAwait(false);
        return inspected.Success
            ? OperationResult<WindowInspectionReport>.Ok(inspected.Value! with { DesktopName = desktop?.DesktopName, InputDesktop = desktop?.IsInputDesktop })
            : inspected;
    }

    public async Task<OperationResult<DisplayListResult>> ListDisplaysAsync(
        LaunchContext context,
        bool elevated,
        CancellationToken cancellationToken,
        bool includeModes = false,
        DesktopHelperTarget? desktop = null)
    {
        if (context == LaunchContext.Service)
        {
            if (!OperatingSystem.IsWindows())
            {
                return OperationResult<DisplayListResult>.Fail("windows_required", "Display enumeration requires Windows.");
            }

            DisplayListResult serviceDisplays = await Task.Run(() => DisplayTopology.List(includeModes), cancellationToken).ConfigureAwait(false);
            return OperationResult<DisplayListResult>.Ok(Stamp(serviceDisplays, desktop));
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return available.FailAs<DisplayListResult>();
        }

        OperationResult<DisplayListResult> listedDisplays = await helper.RunAsync<DisplayListResult>(
            SessionHelper.BuildListDisplaysArguments(includeModes),
            elevated,
            HelperTimeout,
            HelperMaxOutputBytes,
            cancellationToken,
            desktop?.DesktopPath).ConfigureAwait(false);
        return listedDisplays.Success
            ? OperationResult<DisplayListResult>.Ok(Stamp(listedDisplays.Value!, desktop))
            : listedDisplays;
    }

    public async Task<OperationResult<CapturedScreenshot>> CaptureAsync(
        ScreenCaptureRequest request,
        string? savePath,
        bool overwrite,
        bool elevated,
        CancellationToken cancellationToken,
        string? desktop = null)
    {
        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return available.FailAs<CapturedScreenshot>();
        }

        long maxBytes = options.Desktop.MaxScreenshotBytes;
        ScreenCaptureRequest helperRequest = request with { MaxBytes = maxBytes, IncludeFullImage = savePath is not null };
        Stopwatch clock = Stopwatch.StartNew();
        OperationResult<ScreenCaptureResult> helperResult = await helper.RunAsync<ScreenCaptureResult>(
            SessionHelper.BuildCaptureArguments(helperRequest),
            elevated,
            HelperTimeout,
            maxBytes,
            cancellationToken,
            desktop).ConfigureAwait(false);
        if (!helperResult.Success)
        {
            return helperResult.FailAs<CapturedScreenshot>();
        }

        ScreenCaptureResult value = helperResult.Value!;
        byte[]? preview = DecodePng(value.PreviewPngBase64, out string? previewError);
        if (previewError is not null)
        {
            return OperationResult<CapturedScreenshot>.Fail("helper_failed", previewError);
        }

        string? savedPath = null;
        long? savedSize = null;
        string? savedSha256 = null;
        bool? replaced = null;
        if (savePath is not null)
        {
            byte[]? full = DecodePng(value.FullPngBase64, out string? fullError);
            if (full is null)
            {
                return OperationResult<CapturedScreenshot>.Fail(
                    "helper_failed",
                    fullError ?? "The session helper did not return the full image.");
            }

            OperationResult<DeployResult> deployed = await deployer.DeployAsync(
                new MemoryStream(full),
                new DeployRequest(savePath, overwrite, false, options.Files.MaxUploadBytes),
                cancellationToken).ConfigureAwait(false);
            if (!deployed.Success)
            {
                return OperationResult<CapturedScreenshot>.Fail(deployed.ErrorCode!, deployed.Error!);
            }

            savedPath = deployed.Value!.FullPath;
            savedSize = deployed.Value.Size;
            savedSha256 = deployed.Value.Sha256;
            replaced = deployed.Value.ReplacedExisting;
        }

        logger?.LogInformation(
            "Screenshot target={Target} size={Width}x{Height} preview={Preview} saved={Saved} durationMs={Ms}",
            value.Target,
            value.Size.Width,
            value.Size.Height,
            preview?.Length ?? 0,
            savedPath is not null,
            clock.ElapsedMilliseconds);

        return OperationResult<CapturedScreenshot>.Ok(new CapturedScreenshot(
            new ScreenCaptureInfo(
                value.Target,
                value.Bounds,
                value.Size,
                value.PreviewSize,
                value.Dpi,
                value.Displays,
                value.WindowHandle,
                value.WindowTitle,
                savedPath,
                savedSize,
                savedSha256,
                replaced,
                value.SessionId,
                value.CaptureFidelity),
            preview));
    }

    public Task<OperationResult<InputStateReport>> InspectInputStateAsync(
        bool elevated,
        CancellationToken cancellationToken,
        string? desktop = null) =>
        helper.RunAsync<InputStateReport>(
            [SessionHelper.Flag, SessionHelper.InspectInputStateCommand],
            elevated,
            TimeSpan.FromSeconds(15),
            HelperMaxOutputBytes,
            cancellationToken,
            desktop);

    private static WindowListResult Stamp(WindowListResult result, DesktopHelperTarget? desktop) =>
        desktop is null ? result : result with { DesktopName = desktop.DesktopName, InputDesktop = desktop.IsInputDesktop };

    private static DisplayListResult Stamp(DisplayListResult result, DesktopHelperTarget? desktop) =>
        desktop is null ? result : result with { DesktopName = desktop.DesktopName, InputDesktop = desktop.IsInputDesktop };

    private static byte[]? DecodePng(string? base64, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            error = "The session helper returned invalid image data.";
            return null;
        }
    }
}
