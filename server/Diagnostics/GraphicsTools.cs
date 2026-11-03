using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.Diagnostics;

/// <summary>MCP tools that inventory graphics adapters.</summary>
[McpServerToolType]
public sealed class GraphicsTools(IGraphicsInventory inventory)
{
    [McpServerTool(Name = "list_graphics_adapters")]
    [Description(
        "Lists DXGI adapters in the service process, including software and remote adapters. Each adapter includes vendor, "
        + "device, subsystem, and revision when they are non-zero, the LUID, video memory, the user-mode driver version when "
        + "CheckInterfaceSupport returns one, and outputs with the adapter LUID they are attached to. A Display-class device "
        + "node is matched by PCI ids and reported as matchConfidence exact, subsystem, device, or ambiguous. Unknown numbers "
        + "are null. This does not rank drivers.")]
    public OperationResult<GraphicsAdapterList> ListGraphicsAdapters() => inventory.List();
}
