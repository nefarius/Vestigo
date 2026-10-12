using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using WixSharp;
using WixSharp.CommonTasks;

namespace Vestigo.Installer;

static class Program
{
    const string DefaultServerRelative = "publish-x64/server";
    const string WindowsServiceName = "Vestigo";
    const string Manufacturer = "Nefarius Software Solutions";

    internal const string EnvServer = "VESTIGO_INSTALLER_SERVER";
    internal const string EnvOut = "VESTIGO_INSTALLER_OUT";
    internal const string EnvUpdater = "VESTIGO_INSTALLER_UPDATER";
    const string UpdaterExecutableName = "Nefarius_Vestigo_Updater.exe";
    const string UpdaterConfigurationName = "Nefarius_Vestigo_Updater.json";
    const string NoticesName = "THIRD-PARTY-NOTICES.txt";

    static int Main(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            options.Validate();

            string licensePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "License.rtf"));
            string updaterConfigurationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, UpdaterConfigurationName));
            string noticesPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, NoticesName));
            if (!System.IO.File.Exists(licensePath))
            {
                throw new FileNotFoundException($"License file not found: {licensePath}");
            }

            if (!System.IO.File.Exists(updaterConfigurationPath))
            {
                throw new FileNotFoundException($"Updater configuration not found: {updaterConfigurationPath}");
            }

            if (!System.IO.File.Exists(noticesPath))
            {
                throw new FileNotFoundException($"Third-party notices not found: {noticesPath}");
            }

            if (!string.Equals(Path.GetFileName(options.UpdaterExe), UpdaterExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"The updater executable must be named {UpdaterExecutableName} so its configuration is discovered.");
            }

            UpdaterRegistry registry = UpdaterRegistry.Read(updaterConfigurationPath);
            Version version = ReadProductVersion(options.ServerPublishDir);

            Feature feature = new("Server", "Vestigo MCP server for WPP trace capture and decoding.");
            Files files = new(feature, Path.Combine(options.ServerPublishDir, "*.*"), IncludeInPublishedFile)
            {
                OnProcess = file =>
                {
                    if (!string.Equals(Path.GetFileName(file.Name), "Vestigo.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    file.ServiceInstaller = new ServiceInstaller(WindowsServiceName)
                    {
                        DisplayName = "Vestigo MCP Server",
                        Description = "Localhost MCP server for offline and realtime WPP trace capture and decoding.",
                        Start = SvcStartType.auto,
                        StartOn = SvcEvent.Install,
                        StopOn = SvcEvent.InstallUninstall_Wait,
                        RemoveOn = SvcEvent.Uninstall_Wait
                    };
                }
            };

            Project project = new(
                "Vestigo MCP Server",
                new Dir(
                    @"%ProgramFiles64Folder%\Nefarius Software Solutions\Vestigo",
                    files,
                    new WixSharp.File(feature, options.UpdaterExe),
                    new WixSharp.File(feature, updaterConfigurationPath),
                    new WixSharp.File(feature, noticesPath)),
                new RegKey(
                    feature,
                    RegistryHive.LocalMachine,
                    registry.Key,
                    new RegValue(registry.Value, version.ToString()) { Win64 = true })
                {
                    Win64 = true
                })
            {
                GUID = new Guid("7c2e1a44-6b8f-4d3a-9e15-2f6a8c0d4b71"),
                UpgradeCode = new Guid("9a4d6e28-1c7b-4f05-8d3e-5b2c7a9f0e64"),
                Platform = Platform.x64,
                Scope = InstallScope.perMachine,
                UI = WUI.WixUI_FeatureTree,
                LicenceFile = licensePath,
                Version = version,
                OutDir = options.OutputDir,
                OutFileName = "Vestigo"
            };
            project.ControlPanelInfo.Manufacturer = Manufacturer;
            project.MajorUpgrade = MajorUpgrade.Default;
            project.SetNetPrerequisite(
                "10.0.0",
                RuntimeType.aspnet,
                RollForward.latestMajor,
                Platform.x64,
                "Vestigo requires the x64 .NET 10 ASP.NET Core runtime. Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and then run this installer again.");
            WixExtension.UI.PreferredVersion = "5.0.2";
            // Registration is per installing user and must not fail the service install.
            // Deregistration skips major upgrades so the replacement product can register
            // after InstallFinalize without the old product removing that task.
            project.AddAction(UpdaterAction(
                "RegisterVestigoUpdater",
                "--install --silent --override-success-code 0",
                When.After,
                Step.InstallFinalize,
                new Condition("NOT REMOVE")));
            project.AddAction(UpdaterAction(
                "DeregisterVestigoUpdater",
                "--uninstall --silent --override-success-code 0",
                When.Before,
                Step.RemoveFiles,
                new Condition("REMOVE=\"ALL\" AND NOT UPGRADINGPRODUCTCODE")));

            string msi = project.BuildMsi();
            if (!System.IO.File.Exists(msi))
            {
                throw new InvalidOperationException($"WiX did not produce '{msi}'.");
            }

            Console.WriteLine(msi);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    static InstalledFileAction UpdaterAction(
        string id,
        string arguments,
        When when,
        Step step,
        Condition condition)
    {
        InstalledFileAction action = new(
            new Id(id),
            UpdaterExecutableName,
            arguments,
            Return.ignore,
            when,
            step,
            condition)
        {
            Execute = Execute.immediate,
            Impersonate = true
        };
        action.Name = id;
        return action;
    }

    static bool IncludeInPublishedFile(string path)
    {
        string leaf = Path.GetFileName(path);
        return !leaf.Equals("appsettings.Development.json", StringComparison.OrdinalIgnoreCase) &&
               !leaf.Equals("appsettings.Production.json", StringComparison.OrdinalIgnoreCase);
    }

    static Version ReadProductVersion(string serverPublishDir)
    {
        string exePath = Path.Combine(serverPublishDir, "Vestigo.exe");
        if (!System.IO.File.Exists(exePath))
        {
            return new Version(1, 0, 0);
        }

        FileVersionInfo info = FileVersionInfo.GetVersionInfo(exePath);
        return TryParseVersion(info.ProductVersion ?? info.FileVersion, out Version? version)
            ? version
            : new Version(1, 0, 0);
    }

    static bool TryParseVersion(string? raw, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string numeric = raw.Split('+', 2)[0].Split('-', 2)[0].Trim();
        string[] parts = numeric.Split('.');
        try
        {
            int major = parts.Length > 0 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
            int minor = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
            int build = parts.Length > 2 ? int.Parse(parts[2], CultureInfo.InvariantCulture) : 0;
            int revision = parts.Length > 3 ? int.Parse(parts[3], CultureInfo.InvariantCulture) : 0;
            version = new Version(major, minor, build, revision);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    sealed class Options
    {
        public required string ServerPublishDir { get; init; }

        public required string OutputDir { get; init; }

        public required string UpdaterExe { get; init; }

        public static Options Parse(string[] args)
        {
            string cwd = Directory.GetCurrentDirectory();
            string server = Path.GetFullPath(Path.Combine(cwd, DefaultServerRelative));
            string output = Path.GetFullPath(Path.Combine(cwd, "publish-x64", "installer"));
            string updater = "";
            ApplyEnvironmentOverrides(ref server, ref output, ref updater);

            for (int i = 0; i < args.Length; i++)
            {
                string argument = args[i];
                if (argument is "--server" or "-s")
                {
                    server = RequirePath(args, ref i, "server");
                }
                else if (argument is "--out" or "-o")
                {
                    output = RequirePath(args, ref i, "out");
                }
                else if (argument is "--updater" or "-u")
                {
                    updater = RequirePath(args, ref i, "updater");
                }
                else if (argument is "--help" or "-h")
                {
                    Console.WriteLine("""
                        Vestigo MCP Server — MSI builder (WixSharp / WiX 5.0.2)

                          --server,  -s <dir>   Published server output (default: ./publish-x64/server)
                          --out,     -o <dir>   MSI output directory (default: ./publish-x64/installer)
                          --updater, -u <file>  Staged Nefarius_Vestigo_Updater.exe

                        Environment: VESTIGO_INSTALLER_SERVER, VESTIGO_INSTALLER_OUT, VESTIGO_INSTALLER_UPDATER
                        """);
                    Environment.Exit(0);
                }
                else
                {
                    throw new ArgumentException($"Unknown argument: {argument}");
                }
            }

            return new Options { ServerPublishDir = server, OutputDir = output, UpdaterExe = updater };
        }

        public void Validate()
        {
            if (!Directory.Exists(ServerPublishDir))
            {
                throw new DirectoryNotFoundException($"Server publish directory not found: {ServerPublishDir}");
            }

            if (!System.IO.File.Exists(Path.Combine(ServerPublishDir, "Vestigo.exe")))
            {
                throw new FileNotFoundException($"Expected Vestigo.exe under {ServerPublishDir}");
            }

            if (!System.IO.File.Exists(UpdaterExe))
            {
                throw new FileNotFoundException($"Updater executable not found: {UpdaterExe}");
            }

            Directory.CreateDirectory(OutputDir);
        }

        static void ApplyEnvironmentOverrides(ref string server, ref string output, ref string updater)
        {
            string? serverOverride = Environment.GetEnvironmentVariable(EnvServer);
            if (!string.IsNullOrWhiteSpace(serverOverride))
            {
                server = Path.GetFullPath(serverOverride);
            }

            string? outputOverride = Environment.GetEnvironmentVariable(EnvOut);
            if (!string.IsNullOrWhiteSpace(outputOverride))
            {
                output = Path.GetFullPath(outputOverride);
            }

            string? updaterOverride = Environment.GetEnvironmentVariable(EnvUpdater);
            if (!string.IsNullOrWhiteSpace(updaterOverride))
            {
                updater = Path.GetFullPath(updaterOverride);
            }
        }

        static string RequirePath(string[] args, ref int index, string name)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for --{name}.");
            }

            index++;
            return Path.GetFullPath(args[index]);
        }
    }
}
