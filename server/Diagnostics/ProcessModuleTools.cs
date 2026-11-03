using System.ComponentModel;

using Microsoft.Extensions.Logging;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Security;

namespace Vestigo.Diagnostics;

/// <summary>Lists modules loaded in a process without disclosing paths outside configured roots.</summary>
[McpServerToolType]
public sealed class ProcessModuleTools(
    RootPolicy roots,
    IProcessModuleSource source,
    VestigoOptions options,
    ILogger<ProcessModuleTools> logger)
{
    [McpServerTool(Name = "list_process_modules")]
    [Description("Lists modules loaded in a process: name, size, architecture, and version. baseAddress is omitted when process memory is not readable. includePaths returns root:relative aliases for modules inside a Read root; other modules have path null and pathOutsideRoots true. Absolute paths are never returned. Hashing and signature checks are separate calls. expectedStartTime, from list_processes, rejects a reused pid. The list is a best-effort snapshot and can be partial.")]
    public OperationResult<ProcessModuleList> ListProcessModules(
        [Description("Process id.")] int pid,
        [Description("Maximum modules to return. Clamped to 1..Vestigo:Diagnostics:MaxProcessModules.")] int limit = 200,
        [Description("Return root aliases for modules inside a Read root.")] bool includePaths = false,
        [Description("Start time from list_processes. When set, a different process start time fails with process_identity_mismatch.")] DateTimeOffset? expectedStartTime = null)
    {
        if (pid <= 0)
        {
            return Fail(pid, "invalid_request", "pid must be a positive process id.");
        }

        int bounded = Math.Clamp(limit, 1, options.Diagnostics.MaxProcessModules);
        ProcessModuleQuery query = source.Query(pid, bounded);
        if (!query.Success)
        {
            return Fail(pid, query.ErrorCode!, query.Error!);
        }

        if (expectedStartTime is { } expected)
        {
            if (query.StartTime is not { } actual || (expected.UtcDateTime - actual.UtcDateTime).Duration() > TimeSpan.FromSeconds(1))
            {
                return Fail(pid, "process_identity_mismatch", "The process start time does not match expectedStartTime. The pid may have been reused.");
            }
        }

        List<ProcessModuleInfo> modules = [];
        foreach (LoadedModuleRecord module in query.Modules)
        {
            (string? alias, bool outside) = includePaths && module.FullPath is not null
                ? roots.DescribeRead(module.FullPath)
                : (null, false);
            modules.Add(new ProcessModuleInfo(
                module.Name,
                module.BaseAddress is { } address ? "0x" + address.ToString("X") : null,
                module.Size,
                module.Architecture,
                module.FileVersion,
                module.ProductVersion,
                includePaths ? alias : null,
                includePaths && outside));
        }

        logger.LogInformation("list_process_modules pid {Pid} count {Count} partial {Partial}", pid, modules.Count, query.Partial);
        return OperationResult<ProcessModuleList>.Ok(new ProcessModuleList(
            pid,
            query.StartTime,
            DateTimeOffset.UtcNow,
            query.Partial,
            query.Truncated,
            query.Skipped,
            modules));
    }

    private OperationResult<ProcessModuleList> Fail(int pid, string code, string error)
    {
        logger.LogInformation("list_process_modules pid {Pid} failed {Code}", pid, code);
        return OperationResult<ProcessModuleList>.Fail(code, error);
    }
}
