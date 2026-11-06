using Microsoft.Extensions.DependencyInjection.Extensions;

using Vestigo.Configuration;
using Vestigo.Input;
using Vestigo.Processes;

namespace Vestigo.Modules;

/// <summary>Injects mouse and keyboard input into the console user's session. Gated by <c>Vestigo:Input:AllowInput</c>.</summary>
public sealed class InputModule : VestigoModule
{
    public override string Name => "Input";

    public override string Description =>
        "Inject mouse movement, clicks, scrolling, typing and key chords into the console user's session. Pairs with the Desktop module's inspect_screen.";

    /// <summary>Injected input acts as the console user, so the module is opt-in.</summary>
    public override bool EnabledByDefault => false;

    /// <summary>The perception services and screenshot gate live in Desktop.</summary>
    public override IReadOnlyList<string> Dependencies => ["Desktop"];

    public override IReadOnlyList<Type> ToolTypes { get; } = [typeof(InputTools)];

    public override void ConfigureServices(IServiceCollection services, VestigoOptions options)
    {
        services.TryAddSingleton<IProcessLauncher, ProcessLauncher>();
        services.TryAddSingleton<SessionHelperClient>();
    }
}
