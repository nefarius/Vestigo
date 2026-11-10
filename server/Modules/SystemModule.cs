using Vestigo.Configuration;
using Vestigo.SystemInfo;

namespace Vestigo.Modules;

/// <summary>Host Windows version, UEFI, Secure Boot, test signing, UAC, code-integrity settings, and logon sessions.</summary>
public sealed class SystemModule : VestigoModule
{
    public override string Name => "System";

    public override string Description =>
        "Host Windows version, UEFI, Secure Boot, test signing, UAC, code-integrity settings, and logon sessions.";

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(SystemTools), typeof(SessionTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.AddSingleton<ISystemPlatform, WindowsVersionPlatform>();
        services.AddSingleton<ISessionPlatform, WindowsSessionPlatform>();
        services.AddSingleton<SystemService>();
    }
}
