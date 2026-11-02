using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;

using JetBrains.Annotations;

using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Common.Tools.DotNet;
using Fallout.Solutions;

using Vestigo.Build;

using Project = Fallout.Solutions.Project;

class Build : FalloutBuild
{
    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    const string PublishRuntime = "win-x64";

    static AbsolutePath PublishRoot => RootDirectory / "publish-x64";
    static AbsolutePath PublishServerDir => PublishRoot / "server";
    static AbsolutePath InstallerOutputDir => PublishRoot / "installer";
    static AbsolutePath UpdaterStageDir => PublishRoot / "updater";
    static AbsolutePath ToolsStageDir => PublishRoot / NefconPin.StageDirectoryName;
    static AbsolutePath StagedNefconExe => ToolsStageDir / NefconPin.Executable;
    static AbsolutePath StagedDebugViewExe => ToolsStageDir / DebugViewPin.Executable;
    static AbsolutePath StagedRipgrepExe => ToolsStageDir / RipgrepPin.Executable;
    static AbsolutePath InstallerProjectFile => RootDirectory / "installer" / "Vestigo.Installer.csproj";
    static AbsolutePath StagedUpdaterExe => UpdaterStageDir / UpdateFeed.UpdaterExecutable;

    [Parameter("Monotonic update-manifest version. Tag builds pass the GitHub Actions run id.")]
    readonly long ManifestVersion;

    [Parameter("UTC publish timestamp for updates.json (ISO 8601). Defaults to the current time.")]
    readonly string PublishedAt;

    [Solution] readonly Solution Solution;

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            DotNetTasks.DotNetClean(settings => settings
                .SetProject(Solution.Path)
                .SetConfiguration(Configuration));
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetTasks.DotNetRestore(settings => settings
                .SetProjectFile(Solution.Path));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetTasks.DotNetBuild(settings => settings
                .SetProjectFile(Solution.Path)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    [UsedImplicitly]
    public Target PublishLocal => _ => _
        .Description("Publish Vestigo locally using the Release configuration and win-x64 runtime")
        .Executes(() =>
        {
            PublishProject(Solution.GetAllProjects("Vestigo.Server").Single(), PublishServerDir);
        });

    [UsedImplicitly]
    public Target PrepareUpdater => _ => _
        .Description("Stage the pinned, signed Vicius x64 updater for the MSI")
        .Executes(StageUpdater);

    [UsedImplicitly]
    public Target PrepareNefcon => _ => _
        .Description("Stage the pinned, signed nefcon x64 CLI (tools\\nefconc.exe) for the MSI")
        .Executes(StageNefcon);

    [UsedImplicitly]
    public Target PrepareDebugView => _ => _
        .Description("Stage the pinned DebugViewConsole.exe (tools\\DebugViewConsole.exe) for the MSI")
        .Executes(StageDebugView);

    [UsedImplicitly]
    public Target PrepareRipgrep => _ => _
        .Description("Stage the pinned ripgrep x64 CLI (tools\\rg.exe) for the MSI")
        .Executes(StageRipgrep);

    [UsedImplicitly]
    public Target BuildInstaller => _ => _
        .Description("Publish Vestigo and build the x64 MSI. Requires Windows and WiX 5.0.2.")
        .DependsOn(PublishLocal, PrepareUpdater, PrepareNefcon, PrepareDebugView, PrepareRipgrep)
        .Executes(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException(
                    "BuildInstaller must run on Windows with the WiX 5.0.2 CLI available.");
            }

            const string envServer = "VESTIGO_INSTALLER_SERVER";
            const string envOut = "VESTIGO_INSTALLER_OUT";
            const string envUpdater = "VESTIGO_INSTALLER_UPDATER";
            const string envNefcon = "VESTIGO_INSTALLER_NEFCON";
            const string envDebugView = "VESTIGO_INSTALLER_DEBUGVIEW";
            const string envRipgrep = "VESTIGO_INSTALLER_RIPGREP";
            try
            {
                Environment.SetEnvironmentVariable(envServer, PublishServerDir);
                Environment.SetEnvironmentVariable(envOut, InstallerOutputDir);
                Environment.SetEnvironmentVariable(envUpdater, StagedUpdaterExe);
                Environment.SetEnvironmentVariable(envNefcon, StagedNefconExe);
                Environment.SetEnvironmentVariable(envDebugView, StagedDebugViewExe);
                Environment.SetEnvironmentVariable(envRipgrep, StagedRipgrepExe);
                DotNetTasks.DotNetRun(settings => settings
                    .SetProjectFile(InstallerProjectFile)
                    .SetConfiguration(Configuration.Release));
            }
            finally
            {
                Environment.SetEnvironmentVariable(envServer, null);
                Environment.SetEnvironmentVariable(envOut, null);
                Environment.SetEnvironmentVariable(envUpdater, null);
                Environment.SetEnvironmentVariable(envNefcon, null);
                Environment.SetEnvironmentVariable(envDebugView, null);
                Environment.SetEnvironmentVariable(envRipgrep, null);
            }
        });

    [UsedImplicitly]
    public Target PublishUpdateManifest => _ => _
        .Description("Write and minisign the Buildbot update manifest for the signed MSI")
        .Executes(PublishSignedManifest);

    void StageUpdater()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("PrepareUpdater requires Windows to verify Authenticode.");
        }

        Directory.CreateDirectory(UpdaterStageDir);
        using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(2) };
        byte[] archive = client.GetByteArrayAsync(UpdateFeed.UpdaterArchiveUrl).GetAwaiter().GetResult();
        string actualHash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        if (!string.Equals(actualHash, UpdateFeed.UpdaterArchiveSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Vicius {UpdateFeed.UpdaterVersion} archive hash {actualHash} does not match the pinned digest.");
        }

        string stagedPath = StagedUpdaterExe;
        if (File.Exists(stagedPath))
        {
            File.Delete(stagedPath);
        }

        using MemoryStream stream = new(archive, writable: false);
        using ZipArchive zip = new(stream, ZipArchiveMode.Read);
        ZipArchiveEntry[] matches = zip.Entries
            .Where(entry => string.Equals(entry.Name, UpdateFeed.UpdaterArchiveExecutable, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one {UpdateFeed.UpdaterArchiveExecutable} entry in the Vicius archive but found {matches.Length}.");
        }

        matches[0].ExtractToFile(stagedPath);
        AuthenticodeTrust.RequireTrustedPublisher(stagedPath, UpdateFeed.PublisherSubject);
    }

    void StageNefcon()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("PrepareNefcon requires Windows to verify Authenticode.");
        }

        Directory.CreateDirectory(ToolsStageDir);
        using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(2) };
        byte[] archive = client.GetByteArrayAsync(NefconPin.ArchiveUrl).GetAwaiter().GetResult();
        string actualHash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        if (!string.Equals(actualHash, NefconPin.ArchiveSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"nefcon {NefconPin.Version} archive hash {actualHash} does not match the pinned digest.");
        }

        string stagedPath = StagedNefconExe;
        if (File.Exists(stagedPath))
        {
            File.Delete(stagedPath);
        }

        using MemoryStream stream = new(archive, writable: false);
        using ZipArchive zip = new(stream, ZipArchiveMode.Read);
        ZipArchiveEntry[] matches = zip.Entries
            .Where(entry => string.Equals(
                entry.FullName.Replace('\\', '/'),
                NefconPin.ArchiveEntryPath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one {NefconPin.ArchiveEntryPath} entry in the nefcon archive but found {matches.Length}.");
        }

        matches[0].ExtractToFile(stagedPath);
        AuthenticodeTrust.RequireTrustedPublisher(stagedPath, UpdateFeed.PublisherSubject);
    }

    void StageDebugView()
    {
        Directory.CreateDirectory(ToolsStageDir);
        using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(2) };
        byte[] executable = client.GetByteArrayAsync(DebugViewPin.AssetUrl).GetAwaiter().GetResult();
        string actualHash = Convert.ToHexString(SHA256.HashData(executable)).ToLowerInvariant();
        if (!string.Equals(actualHash, DebugViewPin.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"DebugViewConsole {DebugViewPin.Version} hash {actualHash} does not match the pinned digest.");
        }

        string stagedPath = StagedDebugViewExe;
        File.WriteAllBytes(stagedPath, executable);
    }

    void StageRipgrep()
    {
        Directory.CreateDirectory(ToolsStageDir);
        using HttpClient client = new() { Timeout = TimeSpan.FromMinutes(2) };
        byte[] archive = client.GetByteArrayAsync(RipgrepPin.ArchiveUrl).GetAwaiter().GetResult();
        string actualHash = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
        if (!string.Equals(actualHash, RipgrepPin.ArchiveSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ripgrep {RipgrepPin.Version} archive hash {actualHash} does not match the pinned digest.");
        }

        using MemoryStream stream = new(archive, writable: false);
        using ZipArchive zip = new(stream, ZipArchiveMode.Read);
        ZipArchiveEntry[] matches = zip.Entries
            .Where(entry => string.Equals(
                entry.FullName.Replace('\\', '/'),
                RipgrepPin.ArchiveEntryPath,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"Expected one {RipgrepPin.ArchiveEntryPath} entry in the ripgrep archive but found {matches.Length}.");
        }

        using MemoryStream executable = new();
        using (Stream entry = matches[0].Open())
        {
            entry.CopyTo(executable);
        }

        string executableHash = Convert.ToHexString(SHA256.HashData(executable.ToArray())).ToLowerInvariant();
        if (!string.Equals(executableHash, RipgrepPin.ExecutableSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"ripgrep {RipgrepPin.Version} rg.exe hash {executableHash} does not match the pinned digest.");
        }

        File.WriteAllBytes(StagedRipgrepExe, executable.ToArray());
    }

    void PublishSignedManifest()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("PublishUpdateManifest requires Windows Installer and Authenticode.");
        }

        if (ManifestVersion <= 0)
        {
            throw new InvalidOperationException("Pass --manifest-version with a positive monotonic manifest version.");
        }

        string keyPath = Environment.GetEnvironmentVariable("VESTIGO_MINISIGN_KEY");
        string password = Environment.GetEnvironmentVariable("VESTIGO_MINISIGN_PASSWORD");
        if (string.IsNullOrWhiteSpace(keyPath) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                "Set VESTIGO_MINISIGN_KEY to the minisign private-key path and VESTIGO_MINISIGN_PASSWORD to its passphrase.");
        }

        string msiPath = RequireCurrentMsi(InstallerOutputDir);
        AuthenticodeTrust.RequireTrustedPublisher(msiPath, UpdateFeed.PublisherSubject);
        string productVersion = ReadMsiProductVersion(msiPath);
        string expectedName = UpdateFeed.PackageFileName(productVersion);
        if (!string.Equals(Path.GetFileName(msiPath), expectedName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Signed MSI '{Path.GetFileName(msiPath)}' does not match product version '{productVersion}'.");
        }

        string manifestPath = InstallerOutputDir / UpdateFeed.ManifestFileName;
        using FileStream msi = File.OpenRead(msiPath);
        UpdateManifestSpec spec = new()
        {
            ProductVersion = productVersion,
            ManifestVersion = ManifestVersion,
            PublishedAt = string.IsNullOrWhiteSpace(PublishedAt)
                ? DateTimeOffset.UtcNow
                : DateTimeOffset.Parse(
                    PublishedAt,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            DownloadSize = msi.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(msi)).ToLowerInvariant()
        };

        File.WriteAllBytes(manifestPath, UpdateFeed.CreateManifest(spec));
        UpdateFeed.SignManifest(manifestPath, keyPath, password);
        UpdateFeed.VerifyManifest(manifestPath, UpdateFeed.ManifestPublicKey);
    }

    static string RequireCurrentMsi(AbsolutePath directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Installer output directory not found: {directory}");
        }

        string pointerPath = directory / UpdateFeed.CurrentPackagePointerFileName;
        if (!File.Exists(pointerPath))
        {
            throw new FileNotFoundException("BuildInstaller pointer not found. Run BuildInstaller first.", pointerPath);
        }

        string name = File.ReadAllText(pointerPath).Trim();
        if (name.Length == 0 ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !name.StartsWith(UpdateFeed.PackageFileNamePrefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The installer pointer '{name}' is not a versioned Vestigo MSI file name.");
        }

        string msiPath = directory / name;
        if (!File.Exists(msiPath))
        {
            throw new FileNotFoundException("Signed MSI not found.", msiPath);
        }

        return msiPath;
    }

    static string ReadMsiProductVersion(string msiPath)
    {
#pragma warning disable CA1416
        Type installerType = Type.GetTypeFromProgID("WindowsInstaller.Installer")
            ?? throw new InvalidOperationException("Windows Installer automation is unavailable.");
        object installer = Activator.CreateInstance(installerType)
            ?? throw new InvalidOperationException("Windows Installer automation could not be created.");
        dynamic database = ((dynamic)installer).OpenDatabase(msiPath, 0);
        dynamic view = database.OpenView("SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'");
        view.Execute();
        dynamic record = view.Fetch();
        if (record is null)
        {
            throw new InvalidOperationException("The MSI has no ProductVersion property.");
        }

        string version = record.StringData(1);
        view.Close();
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException("The MSI ProductVersion is empty.");
        }

        return version;
#pragma warning restore CA1416
    }

    void PublishProject(Project project, AbsolutePath publishDirectory)
    {
        DotNetTasks.DotNetPublish(settings => settings
            .SetProject(project)
            .SetConfiguration(Configuration.Release)
            .SetRuntime(PublishRuntime)
            .SetOutput(publishDirectory)
            .AddProperty("PublishReadyToRun", "true")
            .AddProperty("PublishSingleFile", "false")
            .AddProperty("SelfContained", "false")
            .AddProperty("DebugType", "none")
            .AddProperty("GenerateDocumentationFile", "false")
            .AddProperty("AllowedReferenceRelatedFileExtensions", "none"));
    }
}
