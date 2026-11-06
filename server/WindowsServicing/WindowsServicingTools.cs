using System.ComponentModel;
using System.ServiceProcess;
using System.Text.Json;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Jobs;

namespace Vestigo.WindowsServicing;

public sealed record UpdateServiceState(string Name, string State);
public sealed record WindowsUpdateStatus(OperationResult<WuaStatus> Agent, RebootSnapshot Reboot,
    IReadOnlyList<UpdateServiceState> Services, string Limitation);
public sealed record ServicingStatus(RebootSnapshot Registry, IReadOnlyList<ServicingFile> Files, string Health);

[McpServerToolType]
public sealed class WindowsServicingTools(RebootPendingService reboots, IWindowsUpdateAgent agent,
    IServicingPackageSource packages, NativeDiagnosticWorker worker, JobManager jobs, VestigoOptions options)
{
    [McpServerTool(Name = "get_reboot_pending", ReadOnly = true, Destructive = false)]
    [Description("Collects independent 64-bit registry and WUA reboot signals. Returns pending, notPending, or unknown; unreadable sources are never treated as clear. Installer and CBS session presence are advisory activity signals. Does not reboot or change the queue.")]
    public async Task<OperationResult<RebootSnapshot>> GetRebootPending(CancellationToken cancellationToken = default)
    {
        RebootSnapshot snapshot = reboots.RegistrySnapshot();
        OperationResult<bool> wua = await worker.Run(agent.RebootRequired, options.WindowsServicing.NativeCallTimeout, cancellationToken);
        return OperationResult<RebootSnapshot>.Ok(RebootPendingService.Snapshot([.. snapshot.Signals,
            new("WuaSystemInfo", wua.Success ? wua.Value ? "pending" : "notPending" : "unknown", wua.Error)]));
    }

    [McpServerTool(Name = "list_pending_file_operations", ReadOnly = true, Destructive = false)]
    [Description("Reads both Session Manager REG_MULTI_SZ rename/delete queues. Preserves raw paths, pair index and replacement marker; normalizes recognized NT DOS/UNC paths without accessing them. An unpaired source is malformed, never assumed deleted. Paths may disclose software/user names even outside configured roots; the module is opt-in. No files are opened or changed.")]
    public OperationResult<PendingFileQueue> ListPendingFileOperations(
        [Description("Maximum pairs across both queues; clamped to WindowsServicing:MaxResults.")] int limit = 100) =>
        OperationResult<PendingFileQueue>.Ok(reboots.PendingFiles(Limit(limit)));

    [McpServerTool(Name = "list_windows_updates", ReadOnly = true, Destructive = false)]
    [Description("Starts an offline-only WUA applicability inventory job. Returns a Jobs id; read_job_output returns a JSON OperationResult with update identity/revision, title, KBs and states. WUA may perform expensive local work and write its own diagnostic/cache data. No download, install or online scan is requested. Cancellation/timeout stop waiting, not the native call. One native servicing call runs at a time.")]
    public OperationResult<JobInfo> ListWindowsUpdates(
        [Description("pending or installed. Installed WUA results are not a complete CBS package inventory.")] string state = "pending",
        [Description("Include driver/firmware updates.")] bool includeDrivers = true,
        [Description("Include hidden updates.")] bool includeHidden = false,
        [Description("Maximum returned updates; clamped to WindowsServicing:MaxResults.")] int limit = 100)
    {
        if (state is not ("pending" or "installed"))
            return OperationResult<JobInfo>.Fail("invalid_request", "state must be pending or installed.");
        return Start("windows_update_inventory", () => agent.Inventory(state, includeDrivers, includeHidden, Limit(limit)));
    }

    [McpServerTool(Name = "get_update_history", ReadOnly = true, Destructive = false)]
    [Description("Queries a bounded newest-first WUA history window. Result codes 3, 4, 5 mean succeeded-with-errors, failed, aborted. failuresOnly filters within the examined window; advance offset by Examined, not returned count. HRESULTs retain unsigned hexadecimal form. History may have been reset and is not proof of installed-package state.")]
    public Task<OperationResult<BoundedList<UpdateHistoryEntry>>> GetUpdateHistory(
        [Description("Number of newest-first history entries to skip.")] int offset = 0,
        [Description("Maximum entries examined; clamped to WindowsServicing:MaxResults.")] int limit = 100,
        [Description("Return only results with errors, failure or abortion in this window.")] bool failuresOnly = false,
        CancellationToken cancellationToken = default) => offset < 0
        ? Task.FromResult(OperationResult<BoundedList<UpdateHistoryEntry>>.Fail("invalid_request", "offset cannot be negative."))
        : worker.Run(() => agent.History(offset, Limit(limit), failuresOnly), options.WindowsServicing.NativeCallTimeout, cancellationToken);

    [McpServerTool(Name = "get_windows_update_status", ReadOnly = true, Destructive = false)]
    [Description("Snapshots WUA reboot requirement, installer busy flag, registered update services and six Windows service states. Unavailable WUA data remains an explicit source error. Stopped demand/trigger-start services are not identified as blockers. Does not infer USO activity or trigger a scan.")]
    public async Task<OperationResult<WindowsUpdateStatus>> GetWindowsUpdateStatus(CancellationToken cancellationToken = default)
    {
        OperationResult<WuaStatus> status = await worker.Run(agent.Status, options.WindowsServicing.NativeCallTimeout, cancellationToken);
        RebootSnapshot snapshot = reboots.RegistrySnapshot();
        snapshot = RebootPendingService.Snapshot([.. snapshot.Signals, new("WuaSystemInfo",
            status.Success ? status.Value!.RebootRequired ? "pending" : "notPending" : "unknown", status.Error)]);
        List<UpdateServiceState> states = [];
        foreach (string name in new[] { "wuauserv", "BITS", "UsoSvc", "WaaSMedicSvc", "TrustedInstaller", "DoSvc" })
        {
            try { using ServiceController service = new(name); states.Add(new(name, service.Status.ToString())); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { states.Add(new(name, $"unknown (0x{ex.HResult:X8})")); }
        }
        return OperationResult<WindowsUpdateStatus>.Ok(new(status, snapshot, states,
            "Service state is not proof of a blocker. USO activity and effective WSUS/MDM policy are not determined."));
    }

    [McpServerTool(Name = "get_servicing_status", ReadOnly = true, Destructive = false)]
    [Description("Fast CBS registry snapshot plus pending.xml/reboot.xml metadata. No XML content is parsed and no DISM/SFC health scan or repair is invoked. Missing pending files do not establish component-store health.")]
    public OperationResult<ServicingStatus> GetServicingStatus() => OperationResult<ServicingStatus>.Ok(new(
        reboots.RegistrySnapshot(), new[] { "pending.xml", "reboot.xml" }.Select(name => Metadata(name,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS", name))).ToArray(),
        "unknown: no component-store integrity check performed"));

    [McpServerTool(Name = "list_servicing_packages", ReadOnly = true, Destructive = false)]
    [Description("Starts a read-only DISM API package inventory job against the running Windows image. read_job_output returns JSON with package names, documented state names/raw codes and release types. Usually requires elevation. No localized text parsing, arbitrary DISM arguments, health scan or repair. DISM may write its own log. Shares the single native-call slot with WUA.")]
    public OperationResult<JobInfo> ListServicingPackages(
        [Description("Maximum returned packages; clamped to WindowsServicing:MaxResults. Truncation is explicit.")] int limit = 200) =>
        Start("servicing_packages", () => packages.List(Limit(limit)));

    private int Limit(int requested) => Math.Clamp(requested, 1, options.WindowsServicing.MaxResults);

    private OperationResult<JobInfo> Start<T>(string kind, Func<T> query) => jobs.Start(kind, "Read-only Windows servicing inventory", async (context, ct) =>
    {
        OperationResult<T> result = await worker.Run(query, options.WindowsServicing.NativeCallTimeout, ct);
        context.WriteLine(JsonSerializer.Serialize(result));
        return result.Success ? 0 : 1;
    });

    internal static ServicingFile Metadata(string name, string path)
    {
        try
        {
            // GetAttributes preserves denied access as unknown; File.Exists would conflate it with absence.
            File.GetAttributes(path);
            FileInfo file = new(path);
            return new(name, "present", file.Length, file.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new(name, "absent", null, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(name, $"unknown (0x{ex.HResult:X8})", null, null); }
    }
}
