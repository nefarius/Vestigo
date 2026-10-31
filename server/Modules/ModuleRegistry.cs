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
        new Files.FilesModule(),
        new ProcessesModule(),
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

        bool Active(string name) => Enabled(options).Any(module => module.Name == name);
        if (Active("Processes"))
        {
            if (!Active("Jobs"))
            {
                throw new InvalidOperationException("Vestigo:Modules:Processes requires Vestigo:Modules:Jobs to be enabled.");
            }

            if (!options.AuthenticationEnabled)
            {
                throw new InvalidOperationException("Vestigo:Modules:Processes runs arbitrary programs and requires at least one entry in Vestigo:ApiTokens.");
            }
        }
    }
}
