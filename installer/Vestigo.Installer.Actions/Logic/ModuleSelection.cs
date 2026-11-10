#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Vestigo.Installer.Actions.Logic
{
    /// <summary>One installer-managed module feature. <see cref="Name" /> is both the MSI feature id and the server module name.</summary>
    public sealed class ModuleDefinition
    {
        public ModuleDefinition(string name, string title, string description, bool defaultOn, string? parent)
        {
            Name = name;
            Title = title;
            Description = description;
            DefaultOn = defaultOn;
            Parent = parent;
        }

        public string Name { get; }

        public string Title { get; }

        public string Description { get; }

        /// <summary>Matches the server default: Processes, Devices, RemoteDebugger, Desktop, DesktopScreenshots, Input, InputInjection, Audio, DebugOutput, Diagnostics, Registry, and gated extras are off.</summary>
        public bool DefaultOn { get; }

        /// <summary>Parent feature name, or null for a direct child of the Server feature. Nesting enforces dependencies.</summary>
        public string? Parent { get; }

        /// <summary>Name of the MSI property that carries this module's state from the existing settings.</summary>
        public string PropertyName => ModuleSelection.PropertyPrefix + Name.ToUpperInvariant();
    }

    /// <summary>
    ///     A pseudo-module that maps to a boolean option under <c>Vestigo:&lt;Section&gt;:&lt;Key&gt;</c> instead of
    ///     <c>Vestigo:Modules:&lt;Name&gt;:Enabled</c>.
    /// </summary>
    public sealed class OptionToggle
    {
        public OptionToggle(string name, string section, string key)
        {
            Name = name;
            Section = section;
            Key = key;
        }

        public string Name { get; }

        public string Section { get; }

        public string Key { get; }
    }

    /// <summary>
    ///     Pure logic for the module feature tree: the module list, reading choices from the existing settings, the
    ///     deferred-action data format, and the merge into <c>Vestigo:Modules</c> and option toggles such as
    ///     <c>Vestigo:Software:AllowUninstall</c>.
    /// </summary>
    public static class ModuleSelection
    {
        public const string PropertyPrefix = "VESTIGO_MOD_";
        public const string ServerFeature = "Server";

        /// <summary>Pseudo module: not a server module but <c>Vestigo:Software:AllowUninstall</c>.</summary>
        public const string SoftwareUninstall = "SoftwareUninstall";

        /// <summary>Pseudo module: not a server module but <c>Vestigo:ScheduledTasks:AllowChanges</c>.</summary>
        public const string ScheduledTaskChanges = "ScheduledTaskChanges";

        /// <summary>Pseudo module: not a server module but <c>Vestigo:Audio:AllowSetDefault</c>.</summary>
        public const string AudioSetDefault = "AudioSetDefault";

        /// <summary>Pseudo module: not a server module but <c>Vestigo:System:AllowBootConfigurationChanges</c>.</summary>
        public const string BootConfigurationChanges = "BootConfigurationChanges";

        /// <summary>Pseudo module: not a server module but <c>Vestigo:System:AllowUacChanges</c>.</summary>
        public const string UacChanges = "UacChanges";

        /// <summary>Pseudo module: not a server module but <c>Vestigo:Desktop:AllowScreenshots</c>.</summary>
        public const string DesktopScreenshots = "DesktopScreenshots";

        /// <summary>Pseudo module: not a server module but <c>Vestigo:Input:AllowInput</c>.</summary>
        public const string InputInjection = "InputInjection";

        /// <summary>Pseudo module: not a server module but <c>Vestigo:Diagnostics:AllowCaptureProbes</c>.</summary>
        public const string DiagnosticsCaptureProbes = "DiagnosticsCaptureProbes";

        /// <summary>Pseudo module: not a server module but <c>Vestigo:Diagnostics:AllowNetworkProbes</c>.</summary>
        public const string DiagnosticsNetworkProbes = "DiagnosticsNetworkProbes";

        /// <summary>Pseudo module: not a server module but <c>Vestigo:Diagnostics:AllowAudioPlayback</c>.</summary>
        public const string DiagnosticsAudioPlayback = "DiagnosticsAudioPlayback";

        public const string DiagnosticsProcessDumps = "DiagnosticsProcessDumps";

        /// <summary>Pseudo module: not a server module but <c>Vestigo:Updates:AllowSelfUpdate</c>.</summary>
        public const string SelfUpdate = "SelfUpdate";

        public static readonly IReadOnlyList<OptionToggle> OptionToggles = new List<OptionToggle>
        {
            new OptionToggle(SoftwareUninstall, "Software", "AllowUninstall"),
            new OptionToggle(ScheduledTaskChanges, "ScheduledTasks", "AllowChanges"),
            new OptionToggle(AudioSetDefault, "Audio", "AllowSetDefault"),
            new OptionToggle(BootConfigurationChanges, "System", "AllowBootConfigurationChanges"),
            new OptionToggle(UacChanges, "System", "AllowUacChanges"),
            new OptionToggle(DesktopScreenshots, "Desktop", "AllowScreenshots"),
            new OptionToggle(InputInjection, "Input", "AllowInput"),
            new OptionToggle(DiagnosticsCaptureProbes, "Diagnostics", "AllowCaptureProbes"),
            new OptionToggle(DiagnosticsNetworkProbes, "Diagnostics", "AllowNetworkProbes"),
            new OptionToggle(DiagnosticsAudioPlayback, "Diagnostics", "AllowAudioPlayback"),
            new OptionToggle(DiagnosticsProcessDumps, "Diagnostics", "AllowProcessDumps"),
            new OptionToggle(SelfUpdate, "Updates", "AllowSelfUpdate")
        };

        public static readonly IReadOnlyList<ModuleDefinition> All = new List<ModuleDefinition>
        {
            new ModuleDefinition("Trace", "Trace", "Offline ETL/WPP conversion and managed realtime capture.", true, null),
            new ModuleDefinition("Files", "Files", "Root-confined file tools plus authenticated PUT/GET transfer endpoints.", true, null),
            new ModuleDefinition("Environment", "Environment", "Read and change machine, user, and process environment variables.", true, null),
            new ModuleDefinition("System", "System", "Host Windows version, UEFI, Secure Boot, test signing, UAC, and code-integrity settings.", true, null),
            new ModuleDefinition("Logs", "Logs", "Tail, incremental reads and regex search of text log files.", true, null),
            new ModuleDefinition("Registry", "Registry", "Read allow-listed registry values and key metadata. Opt-in. There is no write tool.", false, null),
            new ModuleDefinition("EventLog", "Event log", "List and query Windows event log channels and .evtx files.", true, null),
            new ModuleDefinition("Desktop", "Desktop", "List windows, their processes, bounds, and displays on the console user's or service desktop. Screenshots are a separate child feature.", false, null),
            new ModuleDefinition(DesktopScreenshots, "Screenshots", "Allow capturing PNG screenshots of the console user's desktop, one display, one window, or a region (Vestigo:Desktop:AllowScreenshots). Discloses screen contents.", false, "Desktop"),
            new ModuleDefinition("Input", "Input", "Inject mouse movement, clicks, scrolling, typing and key chords into the console user's session. Needs Desktop. Injection is a separate child feature.", false, "Desktop"),
            new ModuleDefinition(InputInjection, "Allow input injection", "Allow send_input to act as the console user (Vestigo:Input:AllowInput). Anything on screen can be clicked or typed into. Administrator-equivalent when an elevated window is open.", false, "Input"),
            new ModuleDefinition("Audio", "Audio", "List audio playback and recording endpoints, volumes, sessions, and defaults. Optionally switch the default device.", false, null),
            new ModuleDefinition(AudioSetDefault, "Set default audio device", "Allow changing the default, multimedia, and communications audio devices (Vestigo:Audio:AllowSetDefault).", false, "Audio"),
            new ModuleDefinition("DebugOutput", "Debug output", "Capture and filter Win32 OutputDebugString messages through DebugViewConsole. Reads debug output from every session.", false, null),
            new ModuleDefinition("Services", "Services", "List, start, stop, and configure Windows services and kernel drivers.", true, null),
            new ModuleDefinition("ScheduledTasks", "Scheduled tasks", "List and inspect scheduled tasks and their run history.", true, null),
            new ModuleDefinition(ScheduledTaskChanges, "Run and delete tasks", "Allow running, stopping, enabling, disabling, and deleting scheduled tasks (Vestigo:ScheduledTasks:AllowChanges). Tasks run as their own principal, often SYSTEM. Administrator-equivalent.", false, "ScheduledTasks"),
            new ModuleDefinition("Devices", "Devices", "Driver install, device nodes, class filters, and device state via nefcon. Runs code with kernel privilege.", false, null),
            new ModuleDefinition("Jobs", "Jobs", "Background jobs with paged output, wait, and cancel. Needed by Processes, Software, and Diagnostics.", true, null),
            new ModuleDefinition("WindowsServicing", "Windows servicing", "Read-only Windows Update, reboot queues, CBS packages, servicing logs, storage and file lockers. Needs Jobs. May disclose sensitive paths and logs.", false, "Jobs"),
            new ModuleDefinition("Diagnostics", "Diagnostics", "Inspect PE files and signatures, list loaded modules, list graphics adapters, list process connections, list media capabilities, and list Windows Error Reporting report metadata. Needs Jobs. A valid Authenticode signature is not permission to load a kernel driver.", false, "Jobs"),
            new ModuleDefinition(DiagnosticsCaptureProbes, "Capture probes", "Allow probe_capture_support to observe whether frames arrive (Vestigo:Diagnostics:AllowCaptureProbes). No pixel data is returned.", false, "Diagnostics"),
            new ModuleDefinition(DiagnosticsNetworkProbes, "Network probes", "Allow test_endpoint to contact destinations listed in Vestigo:Diagnostics:AllowedProbeDestinations (Vestigo:Diagnostics:AllowNetworkProbes). The probe runs as the service and does not capture packets.", false, "Diagnostics"),
            new ModuleDefinition(DiagnosticsAudioPlayback, "Audio playback probes", "Allow probe_audio_format to play a synthetic tone on a render endpoint (Vestigo:Diagnostics:AllowAudioPlayback). Format checks do not emit sound.", false, "Diagnostics"),
            new ModuleDefinition(DiagnosticsProcessDumps, "Process dumps", "Allow capture_process_dump to write a minidump into a Write root (Vestigo:Diagnostics:AllowProcessDumps). Dumps are not uploaded and no download ticket is created.", false, "Diagnostics"),
            new ModuleDefinition("Processes", "Processes", "Run, list, close, and kill processes as the service or the console user. Administrator-equivalent.", false, "Jobs"),
            new ModuleDefinition("RemoteDebugger", "Remote debugger", "Install, start, and stop Visual Studio Remote Debugging Tools. Needs Processes.", false, "Processes"),
            new ModuleDefinition("Software", "Software", "List installed software.", true, "Jobs"),
            new ModuleDefinition(SoftwareUninstall, "Silent uninstall", "Allow silent uninstalls of installed software as the service account (Vestigo:Software:AllowUninstall). Administrator-equivalent.", false, "Software"),
            new ModuleDefinition(BootConfigurationChanges, "Boot configuration", "Allow changing BCD test-signing and WHQL developer test mode (Vestigo:System:AllowBootConfigurationChanges). Administrator-equivalent. Takes effect after reboot.", false, "System"),
            new ModuleDefinition(UacChanges, "UAC changes", "Allow changing the UAC slider and EnableLUA (Vestigo:System:AllowUacChanges). Administrator-equivalent. Turning UAC off takes effect after reboot.", false, "System"),
            new ModuleDefinition("Updates", "Updates", "Check the signed Buildbot feed for a newer Vestigo release.", true, null),
            new ModuleDefinition(SelfUpdate, "Silent self-update", "Allow start_self_update to run the packaged Vicius updater (Vestigo:Updates:AllowSelfUpdate). Needs the Automatic updates feature. Administrator-equivalent: installs the signed MSI and restarts the service.", false, "Updates")
        };

        public static bool IsOptionToggle(string name)
        {
            return FindOptionToggle(name) != null;
        }

        public static OptionToggle? FindOptionToggle(string name)
        {
            foreach (OptionToggle toggle in OptionToggles)
            {
                if (string.Equals(toggle.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return toggle;
                }
            }

            return null;
        }

        public static ModuleDefinition? Find(string name)
        {
            foreach (ModuleDefinition module in All)
            {
                if (string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return module;
                }
            }

            return null;
        }

        public static IDictionary<string, bool> Defaults()
        {
            Dictionary<string, bool> result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (ModuleDefinition module in All)
            {
                result[module.Name] = module.DefaultOn;
            }

            return result;
        }

        /// <summary>
        ///     Reads module states from the existing settings. Only modules the file mentions are returned, so a fresh
        ///     install (or a file without module keys) keeps the feature defaults. Returns an empty map for missing or invalid JSON.
        /// </summary>
        public static IDictionary<string, bool> ReadExisting(string? json)
        {
            Dictionary<string, bool> result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            object? root;
            try
            {
                root = MiniJson.Parse(json!);
            }
            catch (FormatException)
            {
                return result;
            }

            if (!(root is JsonObject rootObject) ||
                !rootObject.TryGet(ListenSettings.SectionName, out object? section) ||
                !(section is JsonObject vestigo))
            {
                return result;
            }

            if (vestigo.TryGetIgnoreCase("Modules", out object? modules) && modules is JsonObject moduleObject)
            {
                foreach (KeyValuePair<string, object?> member in moduleObject.Members)
                {
                    ModuleDefinition? module = Find(member.Key);
                    if (module != null && !IsOptionToggle(module.Name) && member.Value is JsonObject settings &&
                        settings.TryGetIgnoreCase("Enabled", out object? enabled) && enabled is bool flag)
                    {
                        result[module.Name] = flag;
                    }
                }
            }

            foreach (OptionToggle toggle in OptionToggles)
            {
                if (vestigo.TryGetIgnoreCase(toggle.Section, out object? sectionValue) &&
                    sectionValue is JsonObject sectionObject &&
                    sectionObject.TryGetIgnoreCase(toggle.Key, out object? allow) && allow is bool allowFlag)
                {
                    result[toggle.Name] = allowFlag;
                }
            }

            return result;
        }

        /// <summary>
        ///     True when an unselected, previously absent feature must not be serialized as an explicit disable.
        ///     <see cref="DesktopScreenshots" /> is new: an upgrade from a build that lacked the feature reports
        ///     it absent, and an unelevated immediate action cannot read <c>Desktop:AllowScreenshots</c>.
        ///     Omitting the name from <see cref="Serialize" /> leaves the existing value in <see cref="Apply" />.
        /// </summary>
        public static bool PreservesExistingWhenUnselected(string name)
        {
            return string.Equals(name, DesktopScreenshots, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, InputInjection, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///     False when the feature state is only the default for a new, unselected toggle and must not be
        ///     written. An explicit request (select or remove) or a currently installed feature is always written.
        /// </summary>
        public static bool ShouldWriteFeatureState(string name, bool requestIsExplicit, bool currentlyInstalled)
        {
            return requestIsExplicit || currentlyInstalled || !PreservesExistingWhenUnselected(name);
        }

        /// <summary>
        ///     Serializes states as <c>Name=1;Name=0</c> for the deferred action's CustomActionData.
        ///     Names that are omitted are left unchanged by <see cref="Apply" />.
        /// </summary>
        public static string Serialize(IDictionary<string, bool> states)
        {
            StringBuilder builder = new StringBuilder();
            foreach (ModuleDefinition module in All)
            {
                if (!states.TryGetValue(module.Name, out bool enabled))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(';');
                }

                builder.Append(module.Name).Append('=').Append(enabled ? '1' : '0');
            }

            return builder.ToString();
        }

        /// <summary>Parses <see cref="Serialize" /> output. Unknown names and malformed pairs are ignored.</summary>
        public static IDictionary<string, bool> Parse(string? text)
        {
            Dictionary<string, bool> result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            foreach (string pair in text!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int index = pair.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                ModuleDefinition? module = Find(pair.Substring(0, index).Trim());
                string value = pair.Substring(index + 1).Trim();
                if (module != null && (value == "0" || value == "1"))
                {
                    result[module.Name] = value == "1";
                }
            }

            return result;
        }

        /// <summary>
        ///     Writes <c>Vestigo:Modules:&lt;Name&gt;:Enabled</c> and each option toggle (for example
        ///     <c>Vestigo:Software:AllowUninstall</c>) for the given states, keeping every other member
        ///     (including other keys inside the touched objects).
        /// </summary>
        public static void Apply(JsonObject vestigo, IDictionary<string, bool> states)
        {
            if (states.Count == 0)
            {
                return;
            }

            JsonObject modules;
            if (vestigo.TryGetIgnoreCase("Modules", out object? existing) && existing is JsonObject existingModules)
            {
                modules = existingModules;
            }
            else
            {
                modules = new JsonObject();
                vestigo.SetIgnoreCase("Modules", modules);
            }

            foreach (ModuleDefinition module in All)
            {
                if (IsOptionToggle(module.Name) || !states.TryGetValue(module.Name, out bool enabled))
                {
                    continue;
                }

                // Keep the member's original key casing, since configuration keys are case-insensitive.
                string key = module.Name;
                JsonObject? settings = null;
                foreach (KeyValuePair<string, object?> member in modules.Members)
                {
                    if (string.Equals(member.Key, module.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        key = member.Key;
                        settings = member.Value as JsonObject;
                        break;
                    }
                }

                if (settings == null)
                {
                    settings = new JsonObject();
                    modules.Set(key, settings);
                }

                settings.SetIgnoreCase("Enabled", enabled);
            }

            foreach (OptionToggle toggle in OptionToggles)
            {
                if (!states.TryGetValue(toggle.Name, out bool allow))
                {
                    continue;
                }

                JsonObject section;
                if (vestigo.TryGetIgnoreCase(toggle.Section, out object? existingSection) &&
                    existingSection is JsonObject existingSectionObject)
                {
                    section = existingSectionObject;
                }
                else
                {
                    section = new JsonObject();
                    vestigo.SetIgnoreCase(toggle.Section, section);
                }

                section.SetIgnoreCase(toggle.Key, allow);
            }
        }
    }
}
