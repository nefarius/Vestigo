using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.Performance;

/// <summary>Storage topology, minifilter, VM environment, and memory pressure inventory.</summary>
[McpServerToolType]
public sealed class PerformanceInventoryTools(
    IStorageTopologySource storage,
    IMinifilterSource minifilters,
    IVmEnvironmentSource vm,
    IMemoryPressureSource memory)
{
    [McpServerTool(Name = "get_storage_topology", ReadOnly = true)]
    [Description(
        "Returns physical disks, bus type, logical versus physical sector size (512e vs 4K native), partition style, "
        + "health, and reliability counters from ROOT\\Microsoft\\Windows\\Storage, plus CIMV2 adapter/driver names.")]
    public OperationResult<StorageTopology> GetStorageTopology() => storage.Read();

    [McpServerTool(Name = "list_filesystem_minifilters", ReadOnly = true)]
    [Description(
        "Lists attached filesystem minifilters with altitudes, frame IDs, and volume instances. "
        + "Use this to identify AV, EDR, sync, or cloud-filter drivers such as WdFilter or cldflt.")]
    public OperationResult<MinifilterList> ListFilesystemMinifilters() => minifilters.List();

    [McpServerTool(Name = "get_vm_environment", ReadOnly = true)]
    [Description(
        "Reports hypervisor presence and vendor, vCPU/NUMA topology, paravirt versus emulated storage/NIC/balloon devices, "
        + "system timer resolution, and VBS/HVCI state.")]
    public OperationResult<VmEnvironment> GetVmEnvironment() => vm.Read();

    [McpServerTool(Name = "get_memory_pressure", ReadOnly = true)]
    [Description(
        "Returns physical and available RAM, commit charge versus limit, pagefile usage, Memory Compression working set, "
        + "VM balloon footprint when present, kernel paged/non-paged pool, and optional top kernel pool tags.")]
    public OperationResult<MemoryPressure> GetMemoryPressure(
        [Description("Include top kernel pool-tag consumers.")] bool includePoolTags = false,
        [Description("Maximum pool tags to return (1-64).")] int poolTagLimit = 16) =>
        memory.Read(includePoolTags, Math.Clamp(poolTagLimit, 1, 64));
}
