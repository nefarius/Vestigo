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
        CancellationToken cancellationToken)
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
                return OperationResult<WindowListResult>.Ok(
                    await Task.Run(() => WindowEnumerator.List(selector), cancellationToken).ConfigureAwait(false));
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

        return await helper.RunAsync<WindowListResult>(
            SessionHelper.BuildListWindowsArguments(selector),
            elevated,
            HelperTimeout,
            HelperMaxOutputBytes,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<WindowInspectionReport>> InspectWindowAsync(
        string handle,
        int? expectedPid,
        DateTimeOffset? expectedStart,
        LaunchContext context,
        bool elevated,
        CancellationToken cancellationToken)
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

        return await helper.RunAsync<WindowInspectionReport>(
            SessionHelper.BuildInspectWindowArguments(handle, expectedPid, expectedStart),
            elevated,
            HelperTimeout,
            HelperMaxOutputBytes,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<DisplayListResult>> ListDisplaysAsync(
        LaunchContext context,
        bool elevated,
        CancellationToken cancellationToken,
        bool includeModes = false)
    {
        if (context == LaunchContext.Service)
        {
            if (!OperatingSystem.IsWindows())
            {
                return OperationResult<DisplayListResult>.Fail("windows_required", "Display enumeration requires Windows.");
            }

            return OperationResult<DisplayListResult>.Ok(
                await Task.Run(() => DisplayTopology.List(includeModes), cancellationToken).ConfigureAwait(false));
        }

        OperationResult<bool> available = launcher.CheckAvailability(LaunchContext.Interactive, elevated);
        if (!available.Success)
        {
            return available.FailAs<DisplayListResult>();
        }

        return await helper.RunAsync<DisplayListResult>(
            SessionHelper.BuildListDisplaysArguments(includeModes),
            elevated,
            HelperTimeout,
            HelperMaxOutputBytes,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationResult<CapturedScreenshot>> CaptureAsync(
        ScreenCaptureRequest request,
        string? savePath,
        bool overwrite,
        bool elevated,
        CancellationToken cancellationToken)
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
            cancellationToken).ConfigureAwait(false);
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
                value.SessionId),
            preview));
    }

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
