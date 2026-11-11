using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.Diagnostics;

/// <summary>Read-only kernel dump inventory. Dump bytes and bugcheck fields are not returned.</summary>
[McpServerToolType]
public sealed class KernelDumpTools(IKernelDumpInventory inventory)
{
    [McpServerTool(Name = "list_kernel_dumps")]
    [Description(
        "Lists MEMORY.DMP, the dedicated CrashControl dump file, and Minidump *.dmp files in file-name order. "
        + "Each row has a stable id, location, file name, length, last write time, and a header prefix when the file opens. "
        + "PAGEDU64 and PAGEDUMP expose major and minor version, machine type, and processor count. "
        + "Bugcheck fields are not returned. MDMP is reported as minidump without kernel fields. "
        + "werMatches names Windows Error Reporting reports whose dump file name matches. "
        + "limit is clamped to 1..50. Dump bytes are not returned.")]
    public OperationResult<KernelDumpList> ListKernelDumps(
        [Description("Maximum dumps. Clamped to 1..50.")] int limit = 20) =>
        inventory.List(limit);
}
