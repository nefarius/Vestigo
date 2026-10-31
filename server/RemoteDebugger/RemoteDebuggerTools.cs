using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Processes;

namespace Vestigo.RemoteDebugger;

/// <summary>
///     MCP tools for the Visual Studio Remote Debugging Tools (msvsmon.exe). A running monitor lets a remote
///     debugger run code on this machine; see docs/security.md.
/// </summary>
[McpServerToolType]
public sealed class RemoteDebuggerTools(RemoteDebuggerService service)
{
    [McpServerTool(Name = "get_remote_debugger_status", ReadOnly = true)]
    [Description(
        "Reports, for each Visual Studio version (2022, 2026) and architecture (x64, arm64, x86), whether msvsmon.exe is installed "
        + "(path, file version, how it was found), plus running msvsmon processes with pid, session id, port (from the command line when "
        + "readable) and whether that port is listening.")]
    public OperationResult<RemoteDebuggerStatus> GetStatus(
        [Description("Optional filter: '2022' or '2026'.")] string? version = null,
        [Description("Optional filter: 'x64', 'arm64' or 'x86'.")] string? arch = null)
    {
        DebuggerVersion? parsedVersion = null;
        DebuggerArch? parsedArch = null;
        if (!string.IsNullOrWhiteSpace(version))
        {
            if (!RemoteDebuggerPlanner.TryParseVersion(version, out DebuggerVersion v))
            {
                return Invalid<RemoteDebuggerStatus>("version must be '2022' or '2026'.");
            }

            parsedVersion = v;
        }

        if (!string.IsNullOrWhiteSpace(arch))
        {
            if (!RemoteDebuggerPlanner.TryParseArch(arch, out DebuggerArch a))
            {
                return Invalid<RemoteDebuggerStatus>("arch must be 'x64', 'arm64' or 'x86'.");
            }

            parsedArch = a;
        }

        return OperationResult<RemoteDebuggerStatus>.Ok(service.GetStatus(parsedVersion, parsedArch));
    }

    [McpServerTool(Name = "install_remote_debugger")]
    [Description(
        "Installs the Visual Studio Remote Debugging Tools as a job: downloads the official installer (aka.ms) into the spool, requires a valid "
        + "Microsoft Corporation Authenticode signature, then runs it silently (/install /quiet /norestart) as the service account. Returns the job id; "
        + "poll with wait_job or read_job_output. Skipped when msvsmon is already installed unless force is true.")]
    public OperationResult<InstallRemoteDebuggerResult> Install(
        [Description("'2022' or '2026'.")] string version,
        [Description("'x64' (default), 'arm64' or 'x86'.")] string arch = "x64",
        [Description("Reinstall even when msvsmon.exe is already present.")] bool force = false)
    {
        if (!Parse(version, arch, out DebuggerVersion v, out DebuggerArch a, out string? error))
        {
            return Invalid<InstallRemoteDebuggerResult>(error!);
        }

        return service.Install(v, a, force);
    }

    [McpServerTool(Name = "start_remote_debugger")]
    [Description(
        "Starts msvsmon.exe with /nostatus /silent /nofirewallwarn, /port, /timeout and the chosen authentication. Default context is 'interactive' "
        + "(the console user's desktop), so attach rights match that user. auth 'windows' (default) authenticates callers; allowUsers adds /allow entries. "
        + "auth 'none' disables authentication completely and requires iUnderstandNoAuthIsInsecure: true. Refuses a second instance on the same port. "
        + "Connect Visual Studio to <host>:<port> afterwards.")]
    public Task<OperationResult<StartRemoteDebuggerResult>> Start(
        [Description("'2022' or '2026'.")] string version,
        [Description("'x64' (default), 'arm64' or 'x86'.")] string arch = "x64",
        [Description("TCP port; defaults to Vestigo:RemoteDebugger:DefaultPort (4026).")] int? port = null,
        [Description("Seconds msvsmon waits without a connection before exiting, 30 to 86400.")] int timeoutSeconds = RemoteDebuggerPlanner.DefaultTimeoutSeconds,
        [Description("'windows' (default) or 'none'.")] string auth = "windows",
        [Description("Required to be true when auth is 'none'. Acknowledges that anyone reaching the port can run code.")] bool iUnderstandNoAuthIsInsecure = false,
        [Description("Windows users allowed to connect (DOMAIN\\user or user@domain), each passed as /allow. Only with auth 'windows'.")] string[]? allowUsers = null,
        [Description("'interactive' (default) or 'service'.")] string context = "interactive",
        [Description("Interactive only: run with the elevated linked token so elevated processes can be debugged.")] bool elevated = false,
        CancellationToken cancellationToken = default)
    {
        if (!Parse(version, arch, out DebuggerVersion v, out DebuggerArch a, out string? error))
        {
            return Task.FromResult(Invalid<StartRemoteDebuggerResult>(error!));
        }

        if (!RemoteDebuggerPlanner.TryParseAuth(auth, out DebuggerAuth parsedAuth))
        {
            return Task.FromResult(Invalid<StartRemoteDebuggerResult>("auth must be 'windows' or 'none'."));
        }

        if (!ProcessTools.TryParseContext(context, out LaunchContext launchContext))
        {
            return Task.FromResult(Invalid<StartRemoteDebuggerResult>("context must be 'service' or 'interactive'."));
        }

        return service.StartAsync(
            v, a, port, timeoutSeconds, parsedAuth, iUnderstandNoAuthIsInsecure, allowUsers ?? [], launchContext, elevated, cancellationToken);
    }

    [McpServerTool(Name = "stop_remote_debugger")]
    [Description(
        "Stops msvsmon.exe by pid, or every instance started by Vestigo (all: true). Tries a graceful close, waits gracefulTimeoutSeconds, then kills "
        + "the process. Only msvsmon.exe processes are accepted.")]
    public Task<OperationResult<IReadOnlyList<StopRemoteDebuggerOutcome>>> Stop(
        [Description("Process id of an msvsmon.exe.")] int? pid = null,
        [Description("Stop all instances started by Vestigo.")] bool all = false,
        [Description("Seconds to wait after the graceful close before killing, 0 to 120.")] int gracefulTimeoutSeconds = 5,
        CancellationToken cancellationToken = default) =>
        service.StopAsync(pid, all, gracefulTimeoutSeconds, cancellationToken);

    [McpServerTool(Name = "prepare_remote_debugger_computer")]
    [Description(
        "Runs 'msvsmon /prepcomputer /private /quiet' as a job: configures the Windows firewall and permissions for remote debugging on private "
        + "networks. It changes firewall rules; needs the service context (default). Returns the job id.")]
    public OperationResult<PrepareRemoteDebuggerResult> Prepare(
        [Description("'2022' or '2026'.")] string version,
        [Description("'x64' (default), 'arm64' or 'x86'.")] string arch = "x64",
        [Description("'service' (default) or 'interactive'.")] string context = "service")
    {
        if (!Parse(version, arch, out DebuggerVersion v, out DebuggerArch a, out string? error))
        {
            return Invalid<PrepareRemoteDebuggerResult>(error!);
        }

        return ProcessTools.TryParseContext(context, out LaunchContext launchContext)
            ? service.Prepare(v, a, launchContext)
            : Invalid<PrepareRemoteDebuggerResult>("context must be 'service' or 'interactive'.");
    }

    private static bool Parse(string version, string? arch, out DebuggerVersion v, out DebuggerArch a, out string? error)
    {
        a = DebuggerArch.X64;
        error = null;
        if (!RemoteDebuggerPlanner.TryParseVersion(version, out v))
        {
            error = "version must be '2022' or '2026'.";
            return false;
        }

        if (!RemoteDebuggerPlanner.TryParseArch(arch, out a))
        {
            error = "arch must be 'x64', 'arm64' or 'x86'.";
            return false;
        }

        return true;
    }

    private static OperationResult<T> Invalid<T>(string message) => OperationResult<T>.Fail("invalid_request", message);
}
