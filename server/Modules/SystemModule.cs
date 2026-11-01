using Vestigo.Configuration;
using Vestigo.SystemInfo;

namespace Vestigo.Modules;

/// <summary>Host Windows version, UEFI, Secure Boot, test signing, and code-integrity settings.</summary>
public sealed class SystemModule : VestigoModule
{
    public override string Name => "System";

    public override string Description =>
        "Host Windows version, UEFI, Secure Boot, test signing, and code-integrity settings.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(SystemTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddSingleton<ISystemPlatform, WindowsVersionPlatform>();
        services.AddSingleton<SystemService>();
    }
}
