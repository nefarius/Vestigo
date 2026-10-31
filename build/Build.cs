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
    public Target BuildInstaller => _ => _
        .Description("Publish Vestigo and build the x64 MSI. Requires Windows and WiX 5.0.2.")
        .DependsOn(PublishLocal, PrepareUpdater)
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
            try
            {
                Environment.SetEnvironmentVariable(envServer, PublishServerDir);
                Environment.SetEnvironmentVariable(envOut, InstallerOutputDir);
                Environment.SetEnvironmentVariable(envUpdater, StagedUpdaterExe);
                DotNetTasks.DotNetRun(settings => settings
                    .SetProjectFile(InstallerProjectFile)
                    .SetConfiguration(Configuration.Release));
            }
            finally
            {
                Environment.SetEnvironmentVariable(envServer, null);
                Environment.SetEnvironmentVariable(envOut, null);
                Environment.SetEnvironmentVariable(envUpdater, null);
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

        string msiPath = InstallerOutputDir / "Vestigo.msi";
        if (!File.Exists(msiPath))
        {
            throw new FileNotFoundException("Signed MSI not found.", msiPath);
        }

        AuthenticodeTrust.RequireTrustedPublisher(msiPath, UpdateFeed.PublisherSubject);
        string productVersion = ReadMsiProductVersion(msiPath);
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
