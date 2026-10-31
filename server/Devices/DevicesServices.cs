namespace Vestigo.Devices;

/// <summary>
///     Registers device-management infrastructure that is always available to modules.
/// </summary>
internal static class DevicesServices
{
    public static IServiceCollection AddDevices(this IServiceCollection services)
    {
        services.AddSingleton<INefconProcess, NefconProcess>();
        services.AddSingleton<INefconRunner, NefconRunner>();
        return services;
    }
}