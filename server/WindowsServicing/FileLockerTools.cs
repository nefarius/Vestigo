using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.WindowsServicing;

public sealed record FileLocker(int Pid, DateTimeOffset? StartTime, string Application, string Service, uint SessionId);
public sealed record FileLockers(IReadOnlyList<FileLocker> Processes, uint RebootReasons, bool Truncated,
    string Limitation = "Restart Manager is best-effort; kernel references and all causes of replacement failure are not enumerated.");

public interface IFileLockerSource { FileLockers Query(string path, int limit); }

[McpServerToolType]
public sealed class FileLockerTools(RootPolicy roots, IFileLockerSource source, NativeDiagnosticWorker worker, VestigoOptions options)
{
    [McpServerTool(Name = "get_file_lockers", ReadOnly = true, Destructive = false)]
    [Description("Best-effort Restart Manager query for one existing file in a Read root. Returns process identities/start times, application/service names and reboot-reason flags. Never shuts down/restarts processes. Not a complete kernel handle or minifilter inventory; an empty list does not prove a file can be replaced.")]
    public Task<OperationResult<FileLockers>> GetFileLockers(
        [Description("File path in a configured Read root, absolute or root:relative.")] string path,
        [Description("Maximum processes, clamped to WindowsServicing:MaxResults.")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        PathCheck check = roots.ResolveExisting(path, RootAccess.Read);
        if (!check.Success) return Task.FromResult(OperationResult<FileLockers>.Fail(check.ErrorCode!, check.Error!));
        if (!File.Exists(check.FullPath)) return Task.FromResult(OperationResult<FileLockers>.Fail("not_a_file", "An existing file is required."));
        return worker.Run(() => source.Query(check.FullPath!, Math.Clamp(limit, 1, options.WindowsServicing.MaxResults)),
            options.WindowsServicing.NativeCallTimeout, cancellationToken);
    }
}
