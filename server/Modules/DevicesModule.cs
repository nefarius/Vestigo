using Vestigo.Configuration;
using Vestigo.Devices;
using Vestigo.Tools;

namespace Vestigo.Modules;

/// <summary>Driver installation, device nodes, class filters, and device state changes.</summary>
public sealed class DevicesModule : VestigoModule
{
    public override string Name => "Devices";

    /// <summary>Kernel-privileged operations stay off until an administrator opts in.</summary>
    public override bool EnabledByDefault => false;

    public override string Description => "Driver install, device nodes, class filters, and device queries and state via nefcon and device management.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(DeviceTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options) =>
        services.AddSingleton<IDeviceControl, DeviceControl>();
}
