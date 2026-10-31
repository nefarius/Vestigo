using Vestigo.Configuration;
using Vestigo.Devices;
using Vestigo.Tools;

namespace Vestigo.Modules;

/// <summary>Driver installation, device nodes, class filters, and device state changes.</summary>
public sealed class DevicesModule : VestigoModule
{
    public override string Name => "Devices";

    public override string Description => "Driver install, device nodes, class filters, and device state via nefcon and device management.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(DeviceTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options) =>
        services.AddSingleton<IDeviceControl, DeviceControl>();
}
