using System.ComponentModel;

using ModelContextProtocol.Server;

namespace Vestigo.Desktop;

/// <summary>Reports the virtual-display backend. Creation stays unavailable until a released backend is selected.</summary>
[McpServerToolType]
public sealed class VirtualDisplayTools(VirtualDisplayCoordinator displays)
{
    [McpServerTool(Name = "get_virtual_display_backend", ReadOnly = true)]
    [Description(
        "Reports whether Vestigo can create a temporary virtual display. "
        + "canCreate false and errorCode virtual_display_backend_unavailable mean no released Vestigo backend component is selected. "
        + "prerequisite names that missing component. An installed indirect-display driver is not an API and is not treated as success. "
        + "This does not change the display topology.")]
    public Task<OperationResult<VirtualDisplayBackendStatus>> GetVirtualDisplayBackend(CancellationToken cancellationToken = default) =>
        displays.StatusAsync(cancellationToken);

    [McpServerTool(Name = "create_virtual_display", Destructive = true)]
    [Description(
        "Would create a temporary virtual display, arrange it, and restore the original topology on close. "
        + "This build has no released backend, so the call returns virtual_display_backend_unavailable and does not change the topology. "
        + "It does not write a vendor registry policy and does not install a driver.")]
    public Task<OperationResult<VirtualDisplaySession>> CreateVirtualDisplay(
        [Description("Width in physical pixels.")] int width = 1920,
        [Description("Height in physical pixels.")] int height = 1080,
        [Description("Refresh numerator. Default 60.")] uint refreshNumerator = 60,
        [Description("Refresh denominator. Default 1.")] uint refreshDenominator = 1,
        [Description("Desktop position X.")] int x = 0,
        [Description("Desktop position Y.")] int y = 0,
        [Description("Rejected with invalid_request. The display-layout request cannot select a primary yet.")] bool primary = false,
        CancellationToken cancellationToken = default) =>
        displays.CreateAsync(
            new VirtualDisplayRequest(width, height, refreshNumerator, refreshDenominator, x, y, primary),
            cancellationToken);
}
