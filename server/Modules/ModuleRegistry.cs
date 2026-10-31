using Vestigo.Configuration;
using Vestigo.Jobs;
using Vestigo.Tools;

namespace Vestigo.Modules;

/// <summary>
///     The explicit list of feature modules. Add new modules here.
/// </summary>
public static class ModuleRegistry
{
    public static IReadOnlyList<VestigoModule> All { get; } =
    [
        new DevicesModule(),
        new JobsModule(),
        new ServicesModule(),
        new SoftwareModule(),
        new Files.FilesModule(),
        new ProcessesModule(),
        new RemoteDebuggerModule(),
        new TraceModule(),
        new EnvironmentModule(),
        new LogsModule(),
        new EventLogModule()
    ];

    public static IReadOnlyList<VestigoModule> Enabled(VestigoOptions options) =>
        All.Where(module => module.IsEnabled(options)).ToList();

    /// <summary>Rejects configuration entries that name no known module.</summary>
    public static void ValidateConfiguration(VestigoOptions options)
    {
        foreach (string name in options.Modules.Keys)
        {
            if (!All.Any(module => string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Vestigo:Modules:{name} does not match a known module ({string.Join(", ", All.Select(module => module.Name))}).");
            }
        }

        foreach (VestigoModule module in Enabled(options))
        {
            foreach (string dependency in module.Dependencies)
            {
                VestigoModule? required = All.FirstOrDefault(item => string.Equals(item.Name, dependency, StringComparison.OrdinalIgnoreCase));
                if (required is null || !required.IsEnabled(options))
                {
                    throw new InvalidOperationException(
                        $"Vestigo:Modules:{module.Name} requires the {dependency} module, which is disabled.");
                }
            }
        }

        if (Enabled(options).Any(module => module.Name == "Software") && options.Software.AllowUninstall && !options.AuthenticationEnabled)
        {
            throw new InvalidOperationException("Vestigo:Software:AllowUninstall runs vendor uninstallers and requires at least one entry in Vestigo:ApiTokens.");
        }

        if (Enabled(options).Any(module => module.Name == "Processes") && !options.AuthenticationEnabled)
        {
            throw new InvalidOperationException("Vestigo:Modules:Processes runs arbitrary programs and requires at least one entry in Vestigo:ApiTokens.");
        }
    }
}