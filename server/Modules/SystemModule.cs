using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.Configuration;
using Vestigo.Processes;
using Vestigo.SystemInfo;

namespace Vestigo.Modules;

/// <summary>Host Windows version, UEFI, Secure Boot, test signing, UAC, code-integrity settings, boot state, logon sessions, opt-in machine reboot, AutoLogon, and interactive logon.</summary>
public sealed class SystemModule : VestigoModule
{
    public override string Name => "System";

    public override string Description =>
        "Host Windows version, UEFI, Secure Boot, test signing, UAC, code-integrity settings, boot state, logon sessions, opt-in machine reboot, AutoLogon, and interactive logon.";

    public override IReadOnlyList<Type> ToolTypes { get; } =
        [typeof(SystemTools), typeof(SessionTools), typeof(AutoLogonTools), typeof(InteractiveLogonTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddSingleton<ISystemPlatform, WindowsVersionPlatform>();
        services.AddSingleton<IBootStatePlatform, WindowsBootStatePlatform>();
        services.AddSingleton<ISessionPlatform, WindowsSessionPlatform>();
        services.AddSingleton<IRebootPlatform, WindowsRebootPlatform>();
        services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();
        services.TryAddSingleton<SessionHelperClient>();
        services.AddSingleton<IWorkstationLockPlatform, SessionHelperWorkstationLockPlatform>();
        services.AddSingleton<SystemService>();
        services.AddSingleton<IAutoLogonPlatform, WindowsAutoLogonPlatform>();
        services.AddSingleton<LogonConsentStore>();
        services.AddSingleton<LogonRequestService>();
        services.AddSingleton<AutoLogonService>();
        services.AddSingleton<InteractiveLogonService>();
        if (options.System.AllowInteractiveLogon)
        {
            services.AddSingleton<CredentialProviderHost>();
            services.AddSingleton<IInteractiveLogonDispatcher>(provider =>
                provider.GetRequiredService<CredentialProviderHost>());
            services.AddHostedService(provider => provider.GetRequiredService<CredentialProviderHost>());
        }
        else
        {
            services.AddSingleton<IInteractiveLogonDispatcher, UnavailableInteractiveLogonDispatcher>();
        }
    }

    public override void MapEndpoints(WebApplication app, VestigoOptions options) =>
        LogonConsentEndpoint.Map(app, options);
}
