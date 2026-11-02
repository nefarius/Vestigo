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
        new ScheduledTasksModule(),
        new SoftwareModule(),
        new Files.FilesModule(),
        new ProcessesModule(),
        new RemoteDebuggerModule(),
        new TraceModule(),
        new EnvironmentModule(),
        new SystemModule(),
        new LogsModule(),
        new EventLogModule(),
        new DesktopModule(),
        new AudioModule(),
        new DebugOutputModule(),
        new UpdatesModule()
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
    ///     Returns a startup warning naming administrator-equivalent capabilities, and separately a Desktop
    ///     enumerator, Audio enumerator, or machine-wide DebugOutput reader, that are enabled while no API token
    ///     is configured, otherwise null. This is a warning,
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

        if (options.ScheduledTasks.AllowChanges && enabled.Any(module => module.Name == "ScheduledTasks"))
        {
            capabilities.Add("Scheduled task changes");
        }

        if (options.System.AllowBootConfigurationChanges && enabled.Any(module => module.Name == "System"))
        {
            capabilities.Add("Boot configuration changes");
        }

        if (options.Updates.AllowSelfUpdate && enabled.Any(module => module.Name == "Updates"))
        {
            capabilities.Add("Self-update");
        }

        bool debugOutput = enabled.Any(module => module.Name == "DebugOutput");
        bool desktop = enabled.Any(module => module.Name == "Desktop");
        bool desktopScreenshots = desktop && options.Desktop.AllowScreenshots;
        bool audio = enabled.Any(module => module.Name == "Audio");
        bool audioSetDefault = audio && options.Audio.AllowSetDefault;
        if (capabilities.Count == 0 && !debugOutput && !desktop && !audio)
        {
            return null;
        }

        string reach = options.IsBoundBeyondLoopback
            ? $"The server listens on {options.ListenAddress}:{options.ListenPort}, so anyone who can reach this port on the network"
            : "Any local client";
        if (capabilities.Count == 0)
        {
            List<string> names = [];
            List<string> clauses = [];
            if (desktop)
            {
                names.Add("Desktop");
                clauses.Add(
                    desktopScreenshots
                        ? $"{reach} can list windows, titles, and displays on the console user's desktop, and can take screenshots of that desktop."
                        : $"{reach} can list windows, titles, and displays on the console user's desktop.");
            }

            if (audio)
            {
                names.Add("Audio");
                clauses.Add(
                    audioSetDefault
                        ? $"{reach} can list audio devices and the processes using them, and can change the default audio devices."
                        : $"{reach} can list audio devices and the processes using them.");
            }

            if (debugOutput)
            {
                names.Add("DebugOutput");
                clauses.Add($"{reach} can read OutputDebugString messages from every session.");
            }

            string named = names.Count == 1
                ? $"the {names[0]} module is enabled"
                : $"the {string.Join(" and ", names)} modules are enabled";
            return $"SECURITY WARNING: Vestigo has no Vestigo:ApiTokens and {named}. " +
                   string.Join(" ", clauses) + " " +
                   "This is acceptable for an isolated lab network; otherwise configure Vestigo:ApiTokens.";
        }

        string extras = "";
        if (desktop)
        {
            extras += desktopScreenshots
                ? "The Desktop module is also enabled, so the same clients can list windows, titles, and displays on the console user's desktop, and can take screenshots of that desktop. "
                : "The Desktop module is also enabled, so the same clients can list windows, titles, and displays on the console user's desktop. ";
        }

        if (audio)
        {
            extras += audioSetDefault
                ? "The Audio module is also enabled, so the same clients can list audio devices and the processes using them, and can change the default audio devices. "
                : "The Audio module is also enabled, so the same clients can list audio devices and the processes using them. ";
        }

        if (debugOutput)
        {
            extras += "The DebugOutput module is also enabled, so the same clients can read OutputDebugString messages from every session. ";
        }

        if (options.Updates.AllowSelfUpdate && enabled.Any(module => module.Name == "Updates"))
        {
            extras += "The Updates module can also install the latest signed Vestigo release and restart the service. ";
        }

        return $"SECURITY WARNING: Vestigo has no Vestigo:ApiTokens and these administrator-equivalent capabilities are enabled: {string.Join(", ", capabilities)}. " +
               $"{reach} can run programs, control services or drivers, or uninstall software as the service account. " +
               extras +
               "This is acceptable for an isolated lab network; otherwise configure Vestigo:ApiTokens.";
    }
}
