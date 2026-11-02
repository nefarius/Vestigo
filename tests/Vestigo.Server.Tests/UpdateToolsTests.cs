using System.Net;
using System.Net.Http;
using System.Text;

using Minisign;

using Vestigo.Build;
using Vestigo.Configuration;
using Vestigo.Modules;
using Vestigo.Updates;

namespace Vestigo.Server.Tests;

public class UpdateToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-updates-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Module_IsEnabledByDefaultAndListsTheTools()
    {
        VestigoModule module = Assert.Single(ModuleRegistry.All, candidate => candidate.Name == "Updates");
        Assert.True(module.EnabledByDefault);
        Assert.Contains(typeof(UpdateTools), module.ToolTypes);
        Assert.Contains(ModuleRegistry.Enabled(new VestigoOptions()), item => item.Name == "Updates");
        ModuleRegistry.ValidateConfiguration(new VestigoOptions());
    }

    [Fact]
    public void PrivilegedWarning_NamesSelfUpdateWithoutTokens()
    {
        VestigoOptions open = new() { Updates = { AllowSelfUpdate = true } };
        VestigoOptions tokens = new() { ApiTokens = ["t"], Updates = { AllowSelfUpdate = true } };
        VestigoOptions off = new();

        ModuleRegistry.ValidateConfiguration(open);
        string? warning = ModuleRegistry.GetPrivilegedModuleWarning(open);
        Assert.Contains("Self-update", warning);
        Assert.Contains("signed Vestigo release", warning);
        ModuleRegistry.ValidateConfiguration(tokens);
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(tokens));
        Assert.Null(ModuleRegistry.GetPrivilegedModuleWarning(off));
    }

    [Fact]
    public void InstalledVersion_PrefersRegistryThenAssembly()
    {
        Assert.Equal(new InstalledVersion("1.2.3.4", "registry"), new InstalledVersionReader(() => "1.2.3.4", () => "9.9.9").Read());
        Assert.Equal(new InstalledVersion("2.1.0", "assembly"), new InstalledVersionReader(() => "  ", () => "2.1.0+abcd").Read());
        Assert.Equal(new InstalledVersion("0.0.0.0", "unknown"), new InstalledVersionReader(() => null, () => null).Read());
    }

    [Fact]
    public void VersionCompare_TreatsMissingBuildAndRevisionAsZero()
    {
        Assert.True(UpdateFeedClient.IsNewer("1.2.4", "1.2.3.0"));
        Assert.False(UpdateFeedClient.IsNewer("1.2.3", "1.2.3.0"));
        Assert.False(UpdateFeedClient.IsNewer("1.2.3.0", "1.2.3"));
        Assert.True(UpdateFeedClient.IsNewer("2.0.0.0", "1.9.9"));
        Assert.False(UpdateFeedClient.IsNewer("not-a-version", "1.0.0"));
    }

    [Fact]
    public async Task Feed_AcceptsAValidSignedManifest()
    {
        using SignedFeed feed = new();
        feed.Sign(UpdateFeed.CreateManifest(Sample("9.8.7.6")));
        UpdateFeedClient client = Client(feed);

        OperationResult<UpdateFeedRelease> result = await client.GetLatestAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("9.8.7.6", result.Value!.Version);
        Assert.Equal(42, result.Value.ManifestVersion);
        Assert.Equal(UpdateFeed.PackageUrl("9.8.7.6"), result.Value.DownloadUrl);
        Assert.Equal(3_960_832, result.Value.DownloadSize);
    }

    [Fact]
    public async Task Feed_RejectsABadSignature()
    {
        using SignedFeed feed = new();
        feed.Sign(UpdateFeed.CreateManifest(Sample("9.8.7.6")));
        feed.Manifest[10] ^= 0xFF;
        UpdateFeedClient client = Client(feed);

        OperationResult<UpdateFeedRelease> result = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal("manifest_invalid", result.ErrorCode);
    }

    [Fact]
    public async Task Feed_RejectsAMissingSidecar()
    {
        using SignedFeed feed = new();
        feed.Sign(UpdateFeed.CreateManifest(Sample("9.8.7.6")));
        UpdateFeedClient client = Client(feed, sidecarStatus: HttpStatusCode.NotFound);

        OperationResult<UpdateFeedRelease> result = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal("feed_unavailable", result.ErrorCode);
    }

    [Fact]
    public async Task Feed_RejectsAForeignUrl()
    {
        using SignedFeed feed = new();
        feed.Sign(Encoding.UTF8.GetBytes(
            """
            {
              "manifestVersion": 7,
              "releases": [
                {
                  "name": "evil",
                  "version": "9.0.0",
                  "summary": "no",
                  "publishedAt": "2026-10-12T00:00:00Z",
                  "downloadUrl": "https://evil.example/Vestigo.msi"
                }
              ]
            }
            """));
        UpdateFeedClient client = Client(feed);

        OperationResult<UpdateFeedRelease> result = await client.GetLatestAsync(CancellationToken.None);

        Assert.Equal("manifest_invalid", result.ErrorCode);
        Assert.Contains(UpdateEndpoints.DirectoryUrl, result.Error);
    }

    [Fact]
    public async Task Check_ReportsAnAvailableUpdateAndTheLastAttempt()
    {
        FakeRunner runner = new()
        {
            Last = new SelfUpdateAttempt(
                DateTimeOffset.Parse("2026-11-01T00:00:00Z"),
                "1.0.0.0",
                "2.0.0.0",
                Path.Combine(_root, "last.log"),
                207,
                "succeeded",
                true,
                "done")
        };
        UpdateTools tools = Tools(Release("2.0.0.0"), new InstalledVersion("1.0.0.0", "registry"), runner);

        UpdateCheck check = (await tools.CheckForUpdates()).Value!;

        Assert.Equal("1.0.0.0", check.Installed.Version);
        Assert.Equal("registry", check.Installed.Source);
        Assert.Equal("2.0.0.0", check.Latest!.Version);
        Assert.True(check.UpdateAvailable);
        Assert.True(check.UpdaterInstalled);
        Assert.True(check.SelfUpdateAllowed);
        Assert.Equal("succeeded", check.LastAttempt!.State);
        Assert.True(check.LastAttempt.Succeeded);
    }

    [Fact]
    public async Task Check_ReportsUpToDate()
    {
        UpdateTools tools = Tools(Release("1.2.3.0"), new InstalledVersion("1.2.3", "assembly"));

        UpdateCheck check = (await tools.CheckForUpdates()).Value!;

        Assert.False(check.UpdateAvailable);
        Assert.Equal("assembly", check.Installed.Source);
    }

    [Fact]
    public async Task Check_SurfacesFeedErrors()
    {
        UpdateTools tools = Tools(OperationResult<UpdateFeedRelease>.Fail("feed_unavailable", "down"));

        Assert.Equal("feed_unavailable", (await tools.CheckForUpdates()).ErrorCode);
    }

    [Fact]
    public async Task Start_RefusesWhenDisabledMissingOrUntrusted()
    {
        Assert.Equal(
            "self_update_disabled",
            (await Tools(Release("2.0.0.0"), allow: false).StartSelfUpdate()).ErrorCode);
        Assert.Equal(
            "updater_not_installed",
            (await Tools(Release("2.0.0.0"), runner: new FakeRunner { IsInstalled = false }).StartSelfUpdate()).ErrorCode);
        Assert.Equal(
            "untrusted_executable",
            (await Tools(Release("2.0.0.0"), runner: new FakeRunner { TrustFailure = "writable by Users" }).StartSelfUpdate()).ErrorCode);
    }

    [Fact]
    public async Task Start_RefusesWhenAlreadyRunningOrUpToDate()
    {
        Assert.Equal(
            "update_in_progress",
            (await Tools(Release("2.0.0.0"), runner: new FakeRunner { IsInProgress = true }).StartSelfUpdate()).ErrorCode);
        Assert.Equal(
            "up_to_date",
            (await Tools(Release("1.0.0.0"), new InstalledVersion("1.0.0.0", "registry")).StartSelfUpdate()).ErrorCode);
    }

    [Fact]
    public async Task Start_DryRunDoesNotLaunch()
    {
        FakeRunner runner = new();
        UpdateTools tools = Tools(Release("2.0.0.0"), runner: runner);

        SelfUpdateResult result = (await tools.StartSelfUpdate(dryRun: true)).Value!;

        Assert.True(result.DryRun);
        Assert.Equal("dry_run", result.State);
        Assert.True(runner.StartedDryRun);
        Assert.Equal("1.0.0.0", runner.LastFrom);
        Assert.Equal("2.0.0.0", runner.LastTarget);
    }

    [Fact]
    public async Task Start_MapsHandOffAndFailure()
    {
        FakeRunner ok = new()
        {
            Result = OperationResult<SelfUpdateResult>.Ok(new SelfUpdateResult(
                false, "started", "1.0.0.0", "2.0.0.0", 207, "log", "note", null))
        };
        FakeRunner fail = new()
        {
            Result = OperationResult<SelfUpdateResult>.Fail("updater_failed", "exit 104")
        };

        Assert.Equal("started", (await Tools(Release("2.0.0.0"), runner: ok).StartSelfUpdate()).Value!.State);
        Assert.Equal("updater_failed", (await Tools(Release("2.0.0.0"), runner: fail).StartSelfUpdate()).ErrorCode);
    }

    [Fact]
    public async Task Runner_RefusesAMissingOrUntrustedUpdater()
    {
        SelfUpdateRunner missing = new(_root, Path.Combine(_root, "records"), _ => null);
        Assert.False(missing.IsInstalled);
        Assert.Equal("updater_not_installed", (await missing.StartAsync("1", "2", false, CancellationToken.None)).ErrorCode);

        StageUpdater();
        SelfUpdateRunner untrusted = new(_root, Path.Combine(_root, "records"), _ => "writable by Users");
        Assert.Equal("untrusted_executable", (await untrusted.StartAsync("1", "2", false, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Runner_DryRunReturnsTheCommand()
    {
        StageUpdater();
        bool launched = false;
        SelfUpdateRunner runner = new(
            _root,
            Path.Combine(_root, "records"),
            _ => null,
            (_, _, _) =>
            {
                launched = true;
                return 207;
            });

        SelfUpdateResult result = (await runner.StartAsync("1.0.0.0", "2.0.0.0", dryRun: true, CancellationToken.None)).Value!;

        Assert.False(launched);
        Assert.Equal("dry_run", result.State);
        Assert.Contains("--silent-update", result.Note);
        Assert.Contains("--skip-self-update", result.Note);
        Assert.Contains("--ignore-busy-state", result.Note);
        Assert.Contains("--ignore-postpone", result.Note);
        Assert.Contains("--log-to-file", result.Note);
    }

    [Theory]
    [InlineData(207, "started", true)]
    [InlineData(202, "up_to_date", true)]
    [InlineData(104, "failed", false)]
    [InlineData(109, "failed", false)]
    [InlineData(116, "failed", false)]
    public async Task Runner_MapsExitCodes(int exit, string state, bool success)
    {
        StageUpdater();
        string records = Path.Combine(_root, "records-" + exit);
        SelfUpdateRunner runner = new(
            _root,
            records,
            _ => null,
            (_, _, log) =>
            {
                File.WriteAllText(log, "vicius exit " + exit);
                return exit;
            });

        OperationResult<SelfUpdateResult> result = await runner.StartAsync("1.0.0.0", "2.0.0.0", false, CancellationToken.None);

        Assert.Equal(success, result.Success);
        if (success)
        {
            Assert.Equal(state, result.Value!.State);
            Assert.Equal(exit, result.Value.ExitCode);
            Assert.Contains("vicius exit", result.Value.LogTail);
        }
        else
        {
            Assert.Equal("updater_failed", result.ErrorCode);
            Assert.Contains(exit.ToString(), result.Error);
        }

        SelfUpdateAttempt attempt = runner.ReadLastAttempt(exit == 207 ? "2.0.0.0" : "1.0.0.0")!;
        Assert.Equal(exit, attempt.ExitCode);
        if (exit == 207)
        {
            Assert.Equal("succeeded", attempt.State);
            Assert.True(attempt.Succeeded);
        }
    }

    [Fact]
    public async Task Runner_BlocksAConcurrentAttempt()
    {
        StageUpdater();
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);
        SelfUpdateRunner runner = new(
            _root,
            Path.Combine(_root, "records"),
            _ => null,
            (_, _, _) =>
            {
                entered.Set();
                release.Wait();
                return 207;
            });

        Task<OperationResult<SelfUpdateResult>> first = Task.Run(
            () => runner.StartAsync("1.0.0.0", "2.0.0.0", false, CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        OperationResult<SelfUpdateResult> second = await runner.StartAsync("1.0.0.0", "2.0.0.0", false, CancellationToken.None);
        Assert.Equal("update_in_progress", second.ErrorCode);
        release.Set();
        Assert.True((await first).Success);
    }

    [Fact]
    public void LastAttempt_InfersSuccessFromTheInstalledVersion()
    {
        Assert.Equal("succeeded", SelfUpdateRunner.StateFor(207, true));
        Assert.Equal("started", SelfUpdateRunner.StateFor(207, null));
        Assert.Equal("up_to_date", SelfUpdateRunner.StateFor(202, true));
        Assert.Equal("failed", SelfUpdateRunner.StateFor(104, false));
        Assert.True(SelfUpdateRunner.SucceededFor(207, "2.0.0.0", "2.0.0"));
        Assert.Null(SelfUpdateRunner.SucceededFor(207, "1.0.0.0", "2.0.0.0"));
        Assert.False(SelfUpdateRunner.SucceededFor(104, "1.0.0.0", "2.0.0.0"));
    }

    private UpdateTools Tools(
        OperationResult<UpdateFeedRelease> feed,
        InstalledVersion? installed = null,
        FakeRunner? runner = null,
        bool allow = true)
    {
        VestigoOptions options = new() { Updates = { AllowSelfUpdate = allow } };
        return new UpdateTools(
            new FakeFeed(feed),
            new FakeVersions(installed ?? new InstalledVersion("1.0.0.0", "registry")),
            runner ?? new FakeRunner(),
            options);
    }

    private static OperationResult<UpdateFeedRelease> Release(string version) =>
        OperationResult<UpdateFeedRelease>.Ok(new UpdateFeedRelease(
            version,
            42,
            DateTimeOffset.Parse("2026-10-12T00:00:00Z"),
            "notes",
            UpdateFeed.PackageUrl(version.Contains('.') && version.Split('.').Length == 4 ? version : version + ".0"),
            100));

    private static UpdateManifestSpec Sample(string version) => new()
    {
        ProductVersion = version,
        ManifestVersion = 42,
        PublishedAt = DateTimeOffset.Parse("2026-10-12T00:00:00Z"),
        DownloadSize = 3_960_832,
        Sha256 = new string('a', 64)
    };

    private UpdateFeedClient Client(SignedFeed feed, HttpStatusCode sidecarStatus = HttpStatusCode.OK) =>
        new(
            new StubFactory(request =>
            {
                string path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith(".minisig", StringComparison.OrdinalIgnoreCase))
                {
                    return sidecarStatus == HttpStatusCode.OK
                        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(feed.Sidecar) }
                        : new HttpResponseMessage(sidecarStatus);
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(feed.Manifest) };
            }),
            feed.PublicKey,
            UpdateEndpoints.DirectoryUrl);

    private void StageUpdater()
    {
        File.WriteAllText(Path.Combine(_root, UpdateEndpoints.UpdaterExecutable), "MZ");
        File.WriteAllText(Path.Combine(_root, UpdateEndpoints.UpdaterConfiguration), "{}");
    }

    private sealed class FakeFeed(OperationResult<UpdateFeedRelease> result) : IUpdateFeedClient
    {
        public Task<OperationResult<UpdateFeedRelease>> GetLatestAsync(CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class FakeVersions(InstalledVersion version) : IInstalledVersionSource
    {
        public InstalledVersion Read() => version;
    }

    private sealed class FakeRunner : ISelfUpdateRunner
    {
        public bool IsInstalled { get; set; } = true;

        public string? TrustFailure { get; set; }

        public bool IsInProgress { get; set; }

        public SelfUpdateAttempt? Last { get; set; }

        public OperationResult<SelfUpdateResult> Result { get; set; } = OperationResult<SelfUpdateResult>.Ok(
            new SelfUpdateResult(true, "dry_run", "1.0.0.0", "2.0.0.0", null, "log", "planned", null));

        public bool StartedDryRun { get; private set; }

        public string? LastFrom { get; private set; }

        public string? LastTarget { get; private set; }

        public SelfUpdateAttempt? ReadLastAttempt(string? installedVersion) => Last;

        public Task<OperationResult<SelfUpdateResult>> StartAsync(
            string fromVersion,
            string targetVersion,
            bool dryRun,
            CancellationToken cancellationToken)
        {
            LastFrom = fromVersion;
            LastTarget = targetVersion;
            StartedDryRun = dryRun;
            return Task.FromResult(Result);
        }
    }

    private sealed class SignedFeed : IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("vestigo-minisign-").FullName;

        public SignedFeed()
        {
            Core.GenerateKeyPair("test-password", true, _directory, "vestigo-test");
            PublicKey = File.ReadAllLines(Path.Combine(_directory, "vestigo-test.pub"))[1].Trim();
        }

        public string PublicKey { get; }

        public byte[] Manifest { get; private set; } = [];

        public byte[] Sidecar { get; set; } = [];

        public void Sign(byte[] manifest)
        {
            string path = Path.Combine(_directory, UpdateFeed.ManifestFileName);
            File.WriteAllBytes(path, manifest);
            UpdateFeed.SignManifest(path, Path.Combine(_directory, "vestigo-test.key"), "test-password");
            Manifest = File.ReadAllBytes(path);
            Sidecar = File.ReadAllBytes(path + ".minisig");
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class StubFactory(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
    {
        private readonly StubHandler _handler = new(handler);

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
