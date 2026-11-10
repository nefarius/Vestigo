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
        new PowerModule(),
        new LogsModule(),
        new RegistryModule(),
        new EventLogModule(),
        new DesktopModule(),
        new InputModule(),
        new AudioModule(),
        new DebugOutputModule(),
        new DiagnosticsModule(),
        new WindowsServicingModule(),
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

        if (options.Input.AllowInput && enabled.Any(module => module.Name == "Input"))
        {
            capabilities.Add("Input injection (mouse and keyboard as the console user)");
        }

        if (options.ScheduledTasks.AllowChanges && enabled.Any(module => module.Name == "ScheduledTasks"))
        {
            capabilities.Add("Scheduled task changes");
        }

        if (options.System.AllowBootConfigurationChanges && enabled.Any(module => module.Name == "System"))
        {
            capabilities.Add("Boot configuration changes");
        }

        if (options.System.AllowUacChanges && enabled.Any(module => module.Name == "System"))
        {
            capabilities.Add("UAC changes");
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
        bool servicing = enabled.Any(module => module.Name == "WindowsServicing");
        bool registry = enabled.Any(module => module.Name == "Registry");

        bool diagnostics = enabled.Any(module => module.Name == "Diagnostics");
        bool diagnosticsOnline = diagnostics && options.Diagnostics.AllowOnlineRevocation;
        bool diagnosticsProbes = diagnostics && options.Diagnostics.AllowCaptureProbes;
        bool diagnosticsNetwork = diagnostics && options.Diagnostics.AllowNetworkProbes;
        bool diagnosticsPlayback = diagnostics && options.Diagnostics.AllowAudioPlayback;
        bool diagnosticsDumps = diagnostics && options.Diagnostics.AllowProcessDumps;
        if (capabilities.Count == 0 && !debugOutput && !desktop && !audio && !diagnostics && !servicing && !registry)
        {
            return null;
        }

        string reach = options.IsBoundBeyondLoopback
            ? $"The server listens on {options.ListenAddress}:{options.ListenPort}, so anyone who can reach this port on the network"
            : "Any local client";
        if (capabilities.Count == 0)
        {
            List<string> names = [];
            if (servicing) names.Add("WindowsServicing");
            if (registry) names.Add("Registry");
            List<string> clauses = [];
            if (servicing) clauses.Add($"{reach} can read reboot queue paths, update history, CBS package metadata and root-confined servicing logs.");
            if (registry) clauses.Add($"{reach} can read registry values under the allowed prefixes. Values are returned to the client and are not written to the log.");
            if (desktop)
            {
                names.Add("Desktop");
                clauses.Add(
                    desktopScreenshots
                        ? $"{reach} can list windows, titles, and displays on the console user's desktop, and can take screenshots of that desktop. A client can also publish a short-lived, single-use link to a downscaled preview; that link does not require an API token."
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

            if (diagnostics)
            {
                names.Add("Diagnostics");
                clauses.Add(DiagnosticsClause(reach, diagnosticsOnline, diagnosticsProbes, diagnosticsNetwork, diagnosticsPlayback, diagnosticsDumps));
            }

            string named = names.Count == 1
                ? $"the {names[0]} module is enabled"
                : $"the {string.Join(" and ", names)} modules are enabled";
            return $"SECURITY WARNING: Vestigo has no Vestigo:ApiTokens and {named}. " +
                   string.Join(" ", clauses) + " " +
                   "This is acceptable for an isolated lab network; otherwise configure Vestigo:ApiTokens.";
        }

        string extras = servicing ? "The WindowsServicing module can also disclose reboot queue paths, update history, CBS package metadata and root-confined servicing logs. " : "";
        if (registry)
        {
            extras += "The Registry module can also read registry values under the allowed prefixes. Values are returned to the client and are not written to the log. ";
        }
        if (desktop)
        {
            extras += desktopScreenshots
                ? "The Desktop module is also enabled, so the same clients can list windows, titles, and displays on the console user's desktop, and can take screenshots of that desktop. A client can also publish a short-lived, single-use link to a downscaled preview; that link does not require an API token. "
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

        if (diagnostics)
        {
            extras += "The Diagnostics module is also enabled, so the same clients can " + DiagnosticsDetail(diagnosticsOnline, diagnosticsProbes, diagnosticsNetwork, diagnosticsPlayback, diagnosticsDumps) + " ";
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

    private static string DiagnosticsClause(string reach, bool online, bool probes, bool network, bool playback, bool dumps) =>
        $"{reach} can {DiagnosticsDetail(online, probes, network, playback, dumps)}";

    private static string DiagnosticsDetail(bool online, bool probes, bool network, bool playback, bool dumps)
    {
        string text = online
            ? "inspect binaries inside configured roots, verify signatures including online revocation checks, list loaded modules of running processes, read SetupAPI device-install logs, explain a device problem code, list graphics adapters, list TCP and UDP bindings owned by a process, list Media Foundation video encoders and camera formats, check an audio format, and list Windows Error Reporting report metadata."
            : "inspect binaries inside configured roots, verify signatures, list loaded modules of running processes, read SetupAPI device-install logs, explain a device problem code, list graphics adapters, list TCP and UDP bindings owned by a process, list Media Foundation video encoders and camera formats, check an audio format, and list Windows Error Reporting report metadata.";
        if (probes)
        {
            text += " Capture probes may observe whether frames arrive, and they do not return pixels.";
        }

        if (network)
        {
            text += " Network probes may contact allow-listed destinations, and they do not capture packets.";
        }

        if (playback)
        {
            text += " Audio playback probes may play a synthetic tone on a render endpoint.";
        }

        if (dumps)
        {
            text += " Process dumps may write a minidump of a running process into a Write root.";
        }

        return text;
    }
}
