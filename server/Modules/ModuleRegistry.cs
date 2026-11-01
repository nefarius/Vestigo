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
        new EventLogModule(),
        new DebugOutputModule()
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

    }

    /// <summary>
    ///     Returns a startup warning naming administrator-equivalent capabilities, and separately a machine-wide
    ///     DebugOutput reader, that are enabled while no API token is configured, otherwise null. This is a warning,
    ///     never a failure: an operator may deliberately run an open server on an isolated network. The message
    ///     never contains token values.
    /// </summary>
    public static string? GetPrivilegedModuleWarning(VestigoOptions options)
    {
        if (options.AuthenticationEnabled)
        {
            return null;
        }

        IReadOnlyList<VestigoModule> enabled = Enabled(options);
        List<string> capabilities = [];
        // Services is on by default, so it only counts once the server is reachable from the network;
        // otherwise every default loopback install would warn.
        foreach (string name in new[] { "Processes", "RemoteDebugger", "Devices", "Services" })
        {
            if (name == "Services" && !options.IsBoundBeyondLoopback)
            {
                continue;
            }

            if (enabled.Any(module => module.Name == name))
            {
                capabilities.Add(name);
            }
        }

        if (options.Software.AllowUninstall && enabled.Any(module => module.Name == "Software"))
        {
            capabilities.Add("Software uninstall");
        }

        bool debugOutput = enabled.Any(module => module.Name == "DebugOutput");
        if (capabilities.Count == 0 && !debugOutput)
        {
            return null;
        }

        string reach = options.IsBoundBeyondLoopback
            ? $"The server listens on {options.ListenAddress}:{options.ListenPort}, so anyone who can reach this port on the network"
            : "Any local client";
        string debugClause = debugOutput
            ? $"{reach} can read OutputDebugString messages from every session. "
            : "";
        if (capabilities.Count == 0)
        {
            return $"SECURITY WARNING: Vestigo has no Vestigo:ApiTokens and the DebugOutput module is enabled. " +
                   debugClause +
                   "This is acceptable for an isolated lab network; otherwise configure Vestigo:ApiTokens.";
        }

        string debugSuffix = debugOutput
            ? "The DebugOutput module is also enabled, so the same clients can read OutputDebugString messages from every session. "
            : "";
        return $"SECURITY WARNING: Vestigo has no Vestigo:ApiTokens and these administrator-equivalent capabilities are enabled: {string.Join(", ", capabilities)}. " +
               $"{reach} can run programs, control services or drivers, or uninstall software as the service account. " +
               debugSuffix +
               "This is acceptable for an isolated lab network; otherwise configure Vestigo:ApiTokens.";
    }
}
