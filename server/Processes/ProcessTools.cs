using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Security;

namespace Vestigo.Processes;

/// <summary>Result of <c>run_process</c>: the job plus the end of its output when the caller waited.</summary>
public sealed record RunProcessResult(JobInfo Job, string? OutputTail, long NextOffset, bool StillRunning);

/// <summary>
///     MCP tools that start, list, close, and kill processes. They execute arbitrary code as the Vestigo service
///     account or the console user by design; see docs/security.md.
/// </summary>
[McpServerToolType]
public sealed class ProcessTools(
    RootPolicy roots,
    ExecutableResolver resolver,
    JobManager jobs,
    IProcessLauncher launcher,
    AppCloser closer)
{
    private const int TailBytes = 16 * 1024;

    [McpServerTool(Name = "run_process")]
    [Description(
        "Starts a process as a job. path is a file under a root with Execute access (name:relative\\path or absolute), "
        + "or a bare name such as cmd.exe when AllowPathLookup is enabled. .ps1 files run through pwsh.exe, else powershell.exe. "
        + "context 'service' runs as the Vestigo service account; 'interactive' runs as the console user on winsta0\\default "
        + "(elevated: true uses the linked administrator token). stdout and stderr go to the job output. "
        + "wait > 0 waits that many seconds and returns the output tail; wait 0 returns the job id immediately.")]
    public async Task<OperationResult<RunProcessResult>> RunProcess(
        [Description("Executable or script path, or a bare name resolved from PATH.")] string path,
        [Description("Command-line arguments, one array element per argument.")] string[]? args = null,
        [Description("Working directory under a root with Read access. Defaults to the executable's directory.")] string? workingDirectory = null,
        [Description("Environment variable overrides.")] Dictionary<string, string>? env = null,
        [Description("'service' (default) or 'interactive'.")] string context = "service",
        [Description("Interactive only: run with the elevated linked token.")] bool elevated = false,
        [Description("Seconds to wait (0 to 300) before returning. 0 returns the job id immediately.")] int wait = 0,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseContext(context, out LaunchContext launchContext))
        {
            return Fail("invalid_request", "context must be 'service' or 'interactive'.");
        }

        IReadOnlyList<string> arguments = args ?? [];
        IReadOnlyDictionary<string, string> environment = env ?? new Dictionary<string, string>();
        if (LaunchPlanner.Validate(arguments, environment, workingDirectory) is { } invalid)
        {
            return Fail("invalid_request", invalid);
        }

        OperationResult<ResolvedExecutable> resolved = resolver.Resolve(path);
        if (!resolved.Success)
        {
            return Fail(resolved.ErrorCode!, resolved.Error!);
        }

        string? directory;
        if (workingDirectory is null)
        {
            directory = Path.GetDirectoryName(resolved.Value!.Path);
        }
        else
        {
            PathCheck check = roots.ResolveExisting(workingDirectory, RootAccess.Read);
            if (!check.Success)
            {
                return Fail(check.ErrorCode!, check.Error!);
            }

            if (!Directory.Exists(check.FullPath))
            {
                return Fail("invalid_path", "workingDirectory is not a directory.");
            }

            directory = check.FullPath;
        }

        LaunchPlan plan = LaunchPlanner.Plan(
            resolved.Value!.Path,
            arguments,
            FindPwsh,
            LaunchPlanner.WindowsPowerShellPath(Environment.SystemDirectory));
        if (LaunchPlanner.ValidateCommandLine(plan) is { } tooLong)
        {
            return Fail("invalid_request", tooLong);
        }

        OperationResult<bool> available = launcher.CheckAvailability(launchContext, elevated);
        if (!available.Success)
        {
            return Fail(available.ErrorCode!, available.Error!);
        }

        LaunchRequest request = new(plan.Executable, plan.Arguments, directory, environment, launchContext, elevated);
        OperationResult<JobInfo> started = jobs.Start(
            "process",
            $"{launchContext.ToString().ToLowerInvariant()}: {Path.GetFileName(resolved.Value.Path)}",
            (output, token) => launcher.RunAsync(request, output, token));
        if (!started.Success)
        {
            return Fail(started.ErrorCode!, started.Error!);
        }

        int seconds = Math.Clamp(wait, 0, 300);
        if (seconds == 0)
        {
            return OperationResult<RunProcessResult>.Ok(new RunProcessResult(started.Value!, null, 0, true));
        }

        OperationResult<JobInfo> finished = await jobs.WaitAsync(started.Value!.Id, TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
        JobInfo info = finished.Value!;
        long offset = Math.Max(0, info.OutputBytes - TailBytes);
        OperationResult<JobOutputPage> page = jobs.ReadOutput(info.Id, offset, TailBytes);
        return OperationResult<RunProcessResult>.Ok(new RunProcessResult(
            info,
            page.Value?.Text,
            page.Value?.NextOffset ?? 0,
            info.State == JobState.Running));
    }

    [McpServerTool(Name = "list_processes")]
    [Description("Lists running processes with pid, name, session id and start time. Filters are optional.")]
    public IReadOnlyList<ProcessSummary> ListProcesses(
        [Description("Case-insensitive substring of the process name.")] string? nameFilter = null,
        [Description("Only this process id.")] int? pid = null,
        [Description("Only this session id (0 is services, the console session is usually 1).")] int? sessionId = null,
        [Description("Maximum rows, 1 to 2000.")] int limit = 500) =>
        ProcessControl.List(nameFilter, pid, sessionId, Math.Clamp(limit, 1, 2000));

    [McpServerTool(Name = "close_app")]
    [Description(
        "Politely closes applications by posting WM_CLOSE to their top-level windows. Select by pid, processName and/or "
        + "titleRegex (filters combine: pid or name, narrowed by title). Waits gracefulTimeoutSeconds, then reports which "
        + "processes are still alive; it never kills them. context 'interactive' (default) acts on the console user's desktop.")]
    public async Task<OperationResult<CloseReport>> CloseApp(
        [Description("Process id.")] int? pid = null,
        [Description("Process name, with or without .exe.")] string? processName = null,
        [Description(".NET regular expression matched against window titles (case-insensitive).")] string? titleRegex = null,
        [Description("Seconds to wait for exit after WM_CLOSE, 0 to 600.")] int gracefulTimeoutSeconds = 10,
        [Description("'interactive' (default) or 'service'.")] string context = "interactive",
        [Description("Interactive only: use the elevated token so elevated windows can be closed.")] bool elevated = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseContext(context, out LaunchContext launchContext))
        {
            return OperationResult<CloseReport>.Fail("invalid_request", "context must be 'service' or 'interactive'.");
        }

        CloseSelector selector = new(
            pid is { } id ? [id] : [],
            string.IsNullOrWhiteSpace(processName) ? [] : [processName.Trim()],
            string.IsNullOrWhiteSpace(titleRegex) ? null : titleRegex);
        if (selector.IsEmpty)
        {
            return OperationResult<CloseReport>.Fail("invalid_request", "Provide pid, processName, or titleRegex.");
        }

        if (pid is { } own && own == Environment.ProcessId)
        {
            return OperationResult<CloseReport>.Fail("refused", "Refusing to close the Vestigo process itself.");
        }

        return await closer.CloseAsync(selector, Math.Clamp(gracefulTimeoutSeconds, 0, 600), launchContext, elevated, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "kill_process")]
    [Description(
        "Terminates a process by pid or exact name. entireTree also kills its descendants. "
        + "Refuses the Vestigo process, pid 0 and pid 4.")]
    public OperationResult<IReadOnlyList<KillOutcome>> KillProcess(
        [Description("Process id.")] int? pid = null,
        [Description("Exact process name, with or without .exe. Every match is killed.")] string? processName = null,
        [Description("Also kill child processes.")] bool entireTree = false) =>
        ProcessControl.Kill(pid, processName, entireTree, Environment.ProcessId);

    internal static bool TryParseContext(string? value, out LaunchContext context)
    {
        context = LaunchContext.Service;
        if (string.Equals(value, "service", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "interactive", StringComparison.OrdinalIgnoreCase))
        {
            context = LaunchContext.Interactive;
            return true;
        }

        return false;
    }

    private string? FindPwsh()
    {
        string? found = resolver.FindOnPath("pwsh.exe");
        if (found is not null)
        {
            return found;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string standard = Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        return File.Exists(standard) ? standard : null;
    }

    private static OperationResult<RunProcessResult> Fail(string code, string message) =>
        OperationResult<RunProcessResult>.Fail(code, message);
}
