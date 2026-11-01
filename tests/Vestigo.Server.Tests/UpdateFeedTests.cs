using System.Text;
using System.Text.Json;

using Minisign;

using Vestigo.Build;

namespace Vestigo.Server.Tests;

public class UpdateFeedTests
{
    [Fact]
    public void Manifest_DescribesOnlyTheSignedBuildbotPackage()
    {
        byte[] bytes = UpdateFeed.CreateManifest(Sample());
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.DoesNotContain((byte)0, bytes);

        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        JsonElement release = root.GetProperty("releases")[0];
        Assert.Equal(42, root.GetProperty("manifestVersion").GetInt64());
        Assert.Equal("1.2.3.4", release.GetProperty("version").GetString());
        Assert.Equal(UpdateFeed.PackageUrl("1.2.3.4"), release.GetProperty("downloadUrl").GetString());
        Assert.Equal(
            "https://buildbot.nefarius.at/builds/Vestigo/latest/Vestigo-v1.2.3.4.msi",
            release.GetProperty("downloadUrl").GetString());
        Assert.Equal(3_960_832, release.GetProperty("downloadSize").GetInt64());
        Assert.Equal(new string('a', 64), release.GetProperty("checksum").GetProperty("checksum").GetString());
        Assert.Equal("SHA256", release.GetProperty("checksum").GetProperty("checksumAlg").GetString());
        Assert.Equal(UpdateFeed.LaunchArguments, release.GetProperty("launchArguments").GetString());
        Assert.True(release.GetProperty("runAsAdmin").GetBoolean());
        Assert.Equal(UpdateFeed.PublisherSubject, release.GetProperty("signature").GetProperty("subjectName").GetString());
        Assert.Equal("Required", root.GetProperty("shared").GetProperty("signatureVerificationMode").GetString());
        Assert.Equal("Strict", root.GetProperty("shared").GetProperty("signaturePolicy").GetString());
        Assert.Equal("FromConfiguration", root.GetProperty("shared").GetProperty("signatureStrategy").GetString());
        Assert.Equal(
            new[] { 0, 3010, 1641 },
            release.GetProperty("exitCode").GetProperty("successCodes").EnumerateArray().Select(item => item.GetInt32()).ToArray());
        JsonElement cancelled = release.GetProperty("exitCode").GetProperty("messages").GetProperty("1602");
        Assert.False(cancelled.TryGetProperty("isSuccess", out JsonElement success) && success.GetBoolean());
        Assert.Equal("2026-10-12T00:00:00Z", release.GetProperty("publishedAt").GetString());
        UpdateFeed.RequireBuildbotResources(root);
        Assert.DoesNotContain("github.com", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifest_NormalizesTheChecksumAndRejectsAnEmptyFeed()
    {
        const string digest = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        UpdateManifestSpec spec = new()
        {
            ProductVersion = "1.2.3.4",
            ManifestVersion = 42,
            PublishedAt = DateTimeOffset.Parse("2026-10-12T00:00:00Z"),
            DownloadSize = 3_960_832,
            Sha256 = digest.ToUpperInvariant()
        };
        using JsonDocument document = JsonDocument.Parse(UpdateFeed.CreateManifest(spec));
        Assert.Equal(digest, document.RootElement.GetProperty("releases")[0].GetProperty("checksum").GetProperty("checksum").GetString());
        Assert.Throws<ArgumentOutOfRangeException>(() => UpdateFeed.CreateManifest(new UpdateManifestSpec
        {
            ProductVersion = "1.2.3.4",
            ManifestVersion = 0,
            PublishedAt = spec.PublishedAt,
            DownloadSize = spec.DownloadSize,
            Sha256 = spec.Sha256
        }));
    }

    [Fact]
    public void Manifest_RoundTripsMinisignAndRejectsAnotherKey()
    {
        string directory = Directory.CreateTempSubdirectory("vestigo-manifest").FullName;
        try
        {
            Core.GenerateKeyPair("test-password", true, directory, "vestigo-test");
            string manifestPath = Path.Combine(directory, UpdateFeed.ManifestFileName);
            File.WriteAllBytes(manifestPath, UpdateFeed.CreateManifest(Sample()));
            UpdateFeed.SignManifest(manifestPath, Path.Combine(directory, "vestigo-test.key"), "test-password");
            string publicKey = File.ReadAllLines(Path.Combine(directory, "vestigo-test.pub"))[1].Trim();
            UpdateFeed.VerifyManifest(manifestPath, publicKey);
            Assert.Throws<InvalidOperationException>(() => UpdateFeed.VerifyManifest(manifestPath, UpdateFeed.ManifestPublicKey));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UpdaterPackage_UsesThePinnedReleaseAndBuildbotFeed()
    {
        string root = FindRepositoryRoot();
        using JsonDocument configuration = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "installer", UpdateFeed.UpdaterConfiguration)));
        JsonElement instance = configuration.RootElement.GetProperty("instance");
        JsonElement shared = configuration.RootElement.GetProperty("shared");
        Assert.Equal("Local", instance.GetProperty("authority").GetString());
        Assert.Equal("vestigo", instance.GetProperty("stateId").GetString());
        Assert.Equal(UpdateFeed.ManifestUrl, instance.GetProperty("serverUrlTemplate").GetString());
        Assert.Equal("RegistryValue", shared.GetProperty("detectionMethod").GetString());
        Assert.Equal(UpdateFeed.RegistryKey, shared.GetProperty("detection").GetProperty("key").GetString());
        Assert.Equal(UpdateFeed.RegistryValue, shared.GetProperty("detection").GetProperty("value").GetString());
        Assert.Equal("HKLM", shared.GetProperty("detection").GetProperty("hive").GetString());
        Assert.Equal("WOW64_64KEY", shared.GetProperty("detection").GetProperty("view").GetString());
        Assert.True(shared.GetProperty("runAsTemporaryCopy").GetBoolean());
        Assert.True(shared.GetProperty("allowUserToDisableUpdates").GetBoolean());
        Assert.Equal("Required", shared.GetProperty("signatureVerificationMode").GetString());
        Assert.Equal("Strict", shared.GetProperty("signaturePolicy").GetString());
        Assert.Equal("FromConfiguration", shared.GetProperty("signatureStrategy").GetString());
        Assert.Equal(UpdateFeed.PublisherSubject, shared.GetProperty("signatureConfig").GetProperty("subjectName").GetString());
        UpdateFeed.RequireBuildbotResources(configuration.RootElement);

        string installer = File.ReadAllText(Path.Combine(root, "installer", "Program.cs"));
        Assert.Contains("UpdaterRegistry.Read", installer, StringComparison.Ordinal);
        Assert.Contains("version.ToString()", installer, StringComparison.Ordinal);
        Assert.Contains("Win64 = true", installer, StringComparison.Ordinal);
        Assert.Contains(UpdateFeed.UpdaterExecutable, installer, StringComparison.Ordinal);
        Assert.Contains("--install --silent --override-success-code 0", installer, StringComparison.Ordinal);
        Assert.Contains("--uninstall --silent --override-success-code 0", installer, StringComparison.Ordinal);
        Assert.Contains("NOT UPGRADINGPRODUCTCODE", installer, StringComparison.Ordinal);
        Assert.Contains("Return.ignore", installer, StringComparison.Ordinal);
        Assert.Contains("Impersonate = true", installer, StringComparison.Ordinal);
        Assert.Contains("Step.InstallFinalize", installer, StringComparison.Ordinal);
        Assert.Contains("Step.RemoveFiles", installer, StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-NOTICES.txt", installer, StringComparison.Ordinal);
        Assert.Contains("OutFileName = \"Vestigo-v\" + version", installer, StringComparison.Ordinal);
        Assert.Contains(UpdateFeed.CurrentPackagePointerFileName, installer, StringComparison.Ordinal);

        string build = File.ReadAllText(Path.Combine(root, "build", "Build.cs"));
        Assert.Contains("DependsOn(PublishLocal, PrepareUpdater, PrepareNefcon, PrepareDebugView)", build, StringComparison.Ordinal);
        Assert.Contains("VESTIGO_INSTALLER_NEFCON", build, StringComparison.Ordinal);
        Assert.Contains("VESTIGO_INSTALLER_NEFCON", installer, StringComparison.Ordinal);
        Assert.Contains("VESTIGO_INSTALLER_DEBUGVIEW", build, StringComparison.Ordinal);
        Assert.Contains("VESTIGO_INSTALLER_DEBUGVIEW", installer, StringComparison.Ordinal);
        Assert.Contains("nefcon-LICENSE.txt", installer, StringComparison.Ordinal);
        Assert.Contains("DebugViewPP-LICENSE.txt", installer, StringComparison.Ordinal);
        Assert.Contains("RequireTrustedPublisher(stagedPath, UpdateFeed.PublisherSubject)", build, StringComparison.Ordinal);
        Assert.Contains("RequireCurrentMsi(InstallerOutputDir)", build, StringComparison.Ordinal);
        Assert.Contains("UpdateFeed.CurrentPackagePointerFileName", build, StringComparison.Ordinal);
        Assert.Contains("UpdateFeed.PackageFileName(productVersion)", build, StringComparison.Ordinal);
        Assert.Equal("Vestigo-v1.2.3.4.msi", UpdateFeed.PackageFileName("1.2.3.4"));
        Assert.Equal("current-msi.txt", UpdateFeed.CurrentPackagePointerFileName);
        Assert.Equal("Vestigo-v*.msi", UpdateFeed.PackageFileNamePattern);
        Assert.Equal("1.20.0", UpdateFeed.UpdaterVersion);
        Assert.Equal("ca627af5f9f1c87f5a54d3be8122ee4909e00e9320e62fb8229cf37a8161a3d8", UpdateFeed.UpdaterArchiveSha256);
        Assert.Equal("RWTZDJM+Ify8UukJiWfvLcLvjvrVlHcXY/A9cgbOIStr+c19FIlY8x4B", UpdateFeed.ManifestPublicKey);

        string notices = File.ReadAllText(Path.Combine(root, "installer", "THIRD-PARTY-NOTICES.txt"));
        Assert.Contains("BSD 3-Clause License", notices, StringComparison.Ordinal);
        Assert.Contains("Benjamin Höglinger-Stelzer", notices, StringComparison.Ordinal);
        Assert.Contains("Vicius 1.20.0", notices, StringComparison.Ordinal);
        Assert.Contains("DebugView++ 1.30.0.0", notices, StringComparison.Ordinal);
        Assert.Contains("Boost Software License", notices, StringComparison.Ordinal);

        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "build.yml"));
        Assert.Contains("needs: [build, security, osv]", workflow, StringComparison.Ordinal);
        Assert.Contains("gitleaks detect", workflow, StringComparison.Ordinal);
        Assert.Contains("gitleaks dir .", workflow, StringComparison.Ordinal);
        Assert.Contains("semgrep scan --config p/csharp", workflow, StringComparison.Ordinal);
        Assert.Contains("semgrep/semgrep@sha256:2e45517a85e13a9cca14a2a00df6bb48affd66b8ebfae1b0991cd57578362fb0", workflow, StringComparison.Ordinal);
        Assert.Contains("Vestigo.Installer.Actions/Vestigo.Installer.Actions.csproj", workflow, StringComparison.Ordinal);
        Assert.Contains("installer/Vestigo.Installer.Actions/packages.lock.json", workflow, StringComparison.Ordinal);
        Assert.Contains("ghcr.io/google/osv-scanner-action@sha256:d54770fe0b09493894e76a02aaed9f51a0513fa57c5186452a755d257b0068a6", workflow, StringComparison.Ordinal);
        int verified = workflow.IndexOf("Verify signed MSI", StringComparison.Ordinal);
        int signed = workflow.IndexOf("Sign update manifest", StringComparison.Ordinal);
        int uploaded = workflow.IndexOf("Upload MSI artifact", StringComparison.Ordinal);
        int webhook = workflow.IndexOf("Send webhook to Buildbot", StringComparison.Ordinal);
        Assert.True(verified >= 0 && verified < signed && signed < uploaded && uploaded < webhook);
        Assert.Contains("updates.json.minisig", workflow, StringComparison.Ordinal);
        Assert.Contains("github.run_id", workflow, StringComparison.Ordinal);
        Assert.Contains("dist/updates.json", workflow, StringComparison.Ordinal);
        Assert.Contains("Vestigo-v*.msi", workflow, StringComparison.Ordinal);
        Assert.Contains("Resolve versioned MSI", workflow, StringComparison.Ordinal);
        Assert.Contains("current-msi.txt", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("installer/Vestigo.msi", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("dist/Vestigo.msi", workflow, StringComparison.Ordinal);
    }

    private static UpdateManifestSpec Sample()
    {
        return new UpdateManifestSpec
        {
            ProductVersion = "1.2.3.4",
            ManifestVersion = 42,
            PublishedAt = DateTimeOffset.Parse("2026-10-12T00:00:00Z"),
            DownloadSize = 3_960_832,
            Sha256 = new string('a', 64)
        };
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Vestigo.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
