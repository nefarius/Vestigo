using System.ComponentModel;
using System.Text;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Processes;

namespace Vestigo.Software;

/// <summary>Runs an uninstall plan and appends its output to the job spool. Tests replace it.</summary>
public interface IUninstallRunner
{
    Task<int> RunAsync(UninstallPlan plan, JobContext output, CancellationToken cancellationToken);
}

/// <summary>Runs the plan as the Vestigo service account through CliWrap (the process tree is killed on cancel).</summary>
public sealed class ServiceUninstallRunner : IUninstallRunner
{
    public Task<int> RunAsync(UninstallPlan plan, JobContext output, CancellationToken cancellationToken) =>
        ServiceProcessRunner.RunAsync(
            new LaunchRequest(
                plan.Executable,
                plan.Arguments,
                Path.GetDirectoryName(plan.Executable),
                new Dictionary<string, string>(),
                LaunchContext.Service,
                Elevated: false),
            output,
            cancellationToken);
}

/// <summary>
///     MCP tools that inventory installed software and run silent uninstalls as jobs. Uninstalling executes
///     vendor uninstallers as the Vestigo service account; see docs/security.md.
/// </summary>
[McpServerToolType]
public sealed class SoftwareTools(
    ISoftwareInventory inventory,
    UninstallPlanner planner,
    IUninstallRunner runner,
    JobManager jobs,
    VestigoOptions options)
{
    private const int MaxListLimit = 1000;
    private const int TailBytes = 16 * 1024;
    private const int ErrorTimeout = 1460;

    internal string LogDirectory { get; init; } = Path.Combine(VestigoPaths.SpoolDirectory, "software");

    [McpServerTool(Name = "list_installed_software")]
    [Description("Lists installed programs from the Uninstall registry keys (machine 64/32-bit and loaded user hives) with version, publisher, install location, uninstall strings, and detected installer type, sorted by name.")]
    public OperationResult<SoftwareList> ListInstalledSoftware(
        [Description("Case-insensitive substring of the display name.")] string? nameContains = null,
        [Description("Case-insensitive substring of the publisher.")] string? publisherContains = null,
        [Description("machine, user or all.")] string scope = "all",
        [Description("Include entries flagged SystemComponent.")] bool includeSystemComponents = false,
        [Description("Include updates and patches that belong to a parent product.")] bool includeUpdates = false,
        [Description("Maximum entries to return, 1 to 1000.")] int limit = 200)
    {
        if (!Enum.TryParse(scope, ignoreCase: true, out SoftwareScope parsedScope) || !Enum.IsDefined(parsedScope))
        {
            return OperationResult<SoftwareList>.Fail("invalid_scope", "scope must be machine, user or all.");
        }

        List<InstalledSoftware> matches = inventory.List()
            .Where(item => parsedScope == SoftwareScope.All || string.Equals(item.Scope, parsedScope.ToString(), StringComparison.OrdinalIgnoreCase))
            .Where(item => includeSystemComponents || !item.SystemComponent)
            .Where(item => includeUpdates || item.ParentKeyName is null)
            .Where(item => string.IsNullOrWhiteSpace(nameContains) || item.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(publisherContains)
                || item.Publisher?.Contains(publisherContains, StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id, StringComparer.Ordinal)
            .ToList();

        List<InstalledSoftware> page = matches.Take(Math.Clamp(limit, 1, MaxListLimit)).ToList();
        return OperationResult<SoftwareList>.Ok(new SoftwareList(matches.Count, page.Count, matches.Count > page.Count, page));
    }

    [McpServerTool(Name = "get_installed_software")]
    [Description("Returns one entry by id (from list_installed_software) with the silent uninstall command Vestigo would run, or the reason none is available.")]
    public OperationResult<InstalledSoftwareDetails> GetInstalledSoftware([Description("Entry id such as machine-x64:{KeyName}.")] string id)
    {
        InstalledSoftware? item = Find(id);
        if (item is null)
        {
            return OperationResult<InstalledSoftwareDetails>.Fail("not_found", $"No installed software with id '{id}'.");
        }

        OperationResult<UninstallPlan> plan = planner.Plan(item, null);
        return OperationResult<InstalledSoftwareDetails>.Ok(new InstalledSoftwareDetails(item, plan.Value, plan.ErrorCode, plan.Error));
    }

    [McpServerTool(Name = "uninstall_software")]
    [Description("Silently uninstalls a machine-scope program as a job. MSI uses msiexec /x /qn; EXE installers use QuietUninstallString or known switches for Burn, Inno Setup and NSIS; other installers are refused. Requires Vestigo:Software:AllowUninstall. dryRun returns the planned command only. wait > 0 waits that many seconds and returns the outcome.")]
    public async Task<OperationResult<UninstallResult>> UninstallSoftware(
        [Description("Entry id from list_installed_software.")] string id,
        [Description("Return the planned command without running it.")] bool dryRun = false,
        [Description("Seconds to wait (0 to 300) before returning. 0 returns the job immediately.")] int wait = 0,
        CancellationToken cancellationToken = default)
    {
        if (!options.Software.AllowUninstall)
        {
            return Fail("uninstall_disabled", "Uninstalling is disabled. Set Vestigo:Software:AllowUninstall to true.");
        }

        InstalledSoftware? item = Find(id);
        if (item is null)
        {
            return Fail("not_found", $"No installed software with id '{id}'.");
        }

        if (options.Software.IsProtected(item.Name))
        {
            return Fail("protected_software", $"'{item.Name}' is protected by Vestigo:Software:ProtectedNames.");
        }

        if (item.NoRemove)
        {
            return Fail("not_removable", $"'{item.Name}' is marked NoRemove.");
        }

        if (item.SystemComponent)
        {
            return Fail("not_removable", $"'{item.Name}' is a system component.");
        }

        if (!string.Equals(item.Scope, "machine", StringComparison.Ordinal))
        {
            return Fail("unsupported_scope", "Per-user installations cannot be uninstalled from the service account.");
        }

        string? logPath = item.IsMsi ? Path.Combine(LogDirectory, Guid.NewGuid().ToString("N") + ".log") : null;
        OperationResult<UninstallPlan> planned = planner.Plan(item, logPath);
        if (!planned.Success)
        {
            return Fail(planned.ErrorCode!, planned.Error!);
        }

        UninstallPlan plan = planned.Value!;
        if (dryRun)
        {
            return OperationResult<UninstallResult>.Ok(new UninstallResult(plan, true, null, null, null, null, null));
        }

        if (logPath is not null)
        {
            Directory.CreateDirectory(LogDirectory);
        }

        Outcome outcome = new();
        TimeSpan max = options.Software.MaxUninstallDuration;
        OperationResult<JobInfo> started = jobs.Start(
            "uninstall",
            $"uninstall: {item.Name}",
            (output, token) => RunAsync(plan, output, outcome, max, token));
        if (!started.Success)
        {
            return Fail(started.ErrorCode!, started.Error!);
        }

        int seconds = Math.Clamp(wait, 0, 300);
        if (seconds == 0)
        {
            return OperationResult<UninstallResult>.Ok(new UninstallResult(plan, false, started.Value, null, null, null, null));
        }

        JobInfo info = (await jobs.WaitAsync(started.Value!.Id, TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false)).Value!;
        if (info.State == JobState.Running)
        {
            return OperationResult<UninstallResult>.Ok(new UninstallResult(plan, false, info, null, null, null, null));
        }

        long offset = Math.Max(0, info.OutputBytes - TailBytes);
        string? tail = jobs.ReadOutput(info.Id, offset, TailBytes).Value?.Text;
        bool stillInstalled = inventory.List().Any(entry => string.Equals(entry.Id, item.Id, StringComparison.Ordinal));
        return OperationResult<UninstallResult>.Ok(new UninstallResult(
            plan, false, info, outcome.ExitCode, outcome.RebootRequired, stillInstalled, tail));
    }

    private static async Task<int> RunAsyncCore(IUninstallRunner runner, UninstallPlan plan, JobContext output, Outcome outcome, TimeSpan max, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(max);
        int exit;
        try
        {
            exit = await runner.RunAsync(plan, output, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            output.WriteLine($"[vestigo] uninstaller exceeded {max} and was killed.");
            outcome.ExitCode = ErrorTimeout;
            return ErrorTimeout;
        }

        outcome.ExitCode = exit;
        outcome.RebootRequired = exit is 3010 or 1641;
        output.WriteLine($"[vestigo] uninstaller exit code {exit}{(outcome.RebootRequired ? " (reboot required)" : string.Empty)}");
        AppendLogTail(plan.LogPath, output);
        return exit is 0 or 3010 or 1641 ? 0 : exit;
    }

    private Task<int> RunAsync(UninstallPlan plan, JobContext output, Outcome outcome, TimeSpan max, CancellationToken token) =>
        RunAsyncCore(runner, plan, output, outcome, max, token);

    private static void AppendLogTail(string? logPath, JobContext output)
    {
        if (logPath is null)
        {
            return;
        }

        try
        {
            if (!File.Exists(logPath))
            {
                return;
            }

            using FileStream stream = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int count = (int)Math.Min(stream.Length, TailBytes);
            stream.Seek(-count, SeekOrigin.End);
            byte[] buffer = new byte[count];
            int read = stream.Read(buffer, 0, count);
            output.WriteLine("[vestigo] msiexec log tail:");
            output.WriteLine(Encoding.Latin1.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is a diagnostic extra.
        }
    }

    private InstalledSoftware? Find(string id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : inventory.List().FirstOrDefault(entry => string.Equals(entry.Id, id.Trim(), StringComparison.Ordinal));

    private static OperationResult<UninstallResult> Fail(string code, string message) =>
        OperationResult<UninstallResult>.Fail(code, message);

    private sealed class Outcome
    {
        public volatile bool RebootRequired;
        public int? ExitCode { get; set; }
    }
}
