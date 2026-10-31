using Microsoft.Extensions.Logging.Abstractions;

using Vestigo.Configuration;
using Vestigo.Devices;

namespace Vestigo.Server.Tests;

public sealed class NefconRunnerTests : IDisposable
{
    private static readonly Guid ClassGuid = Guid.Parse("4d36e97d-e325-11ce-bfc1-08002be10318");
    private const string ClassGuidText = "{4D36E97D-E325-11CE-BFC1-08002BE10318}";

    private readonly string _directory = Directory.CreateTempSubdirectory("vestigo-nefcon-").FullName;
    private readonly FakeProcess _process = new();
    private readonly NefconRunner _runner;

    public NefconRunnerTests()
    {
        string exe = Path.Combine(_directory, "nefconc.exe");
        File.WriteAllBytes(exe, []);
        _runner = new NefconRunner(
            new VestigoOptions { Devices = new DevicesOptions { NefconPath = exe, NefconTimeout = TimeSpan.FromSeconds(7) } },
            _process,
            NullLogger<NefconRunner>.Instance);
    }

    public void Dispose()
    {
        _runner.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private static string Inf => @"C:\drivers\my driver.inf";

    [Fact]
    public async Task InstallDriver_WithoutHardwareId_UsesInstallSwitch()
    {
        await _runner.InstallDriver(Inf);
        Assert.Equal(["--install-driver", "--inf-path", Inf], _process.Calls.Single());
        Assert.Equal(TimeSpan.FromSeconds(7), _process.Timeouts.Single());
        Assert.Equal(Path.Combine(_directory, "nefconc.exe"), _process.Executables.Single());
    }

    [Fact]
    public async Task InstallDriver_WithHardwareId_UsesDevconForm()
    {
        await _runner.InstallDriver(Inf, "Root\\Foo", noDuplicates: true, removeDuplicates: true);
        Assert.Equal(["install", Inf, "Root\\Foo", "--no-duplicates", "--remove-duplicates"], _process.Calls.Single());
    }

    [Fact]
    public async Task UninstallDriver_UsesSwitch()
    {
        await _runner.UninstallDriver(Inf);
        Assert.Equal(["--uninstall-driver", "--inf-path", Inf], _process.Calls.Single());
    }

    [Fact]
    public async Task CreateDeviceNode_BuildsFixedArguments()
    {
        await _runner.CreateDeviceNode("System", ClassGuid, "Root\\Foo", noDuplicates: true);
        Assert.Equal(
            ["--create-device-node", "--hardware-id", "Root\\Foo", "--class-name", "System", "--class-guid", ClassGuidText, "--no-duplicates"],
            _process.Calls.Single());
    }

    [Fact]
    public async Task RemoveDeviceNode_BuildsFixedArguments()
    {
        await _runner.RemoveDeviceNode("Root\\Foo", ClassGuid);
        Assert.Equal(["--remove-device-node", "--hardware-id", "Root\\Foo", "--class-guid", ClassGuidText], _process.Calls.Single());
    }

    [Fact]
    public async Task FilterDriverOperations_BuildFixedArguments()
    {
        await _runner.InstallFilterDriver(Inf);
        await _runner.UninstallFilterDriver(ClassGuid, "MyFilter", ClassFilterPosition.Lower, Inf, allVersions: true);
        await _runner.UninstallFilterDriver(ClassGuid, "MyFilter", ClassFilterPosition.Upper);
        await _runner.AddClassFilter(ClassGuid, "MyFilter", ClassFilterPosition.Upper);
        await _runner.RemoveClassFilter(ClassGuid, "MyFilter", ClassFilterPosition.Lower);

        Assert.Equal(["--install-filter-driver", "--inf-path", Inf], _process.Calls[0]);
        Assert.Equal(
            ["--uninstall-filter-driver", "--position", "lower", "--service-name", "MyFilter", "--class-guid", ClassGuidText, "--inf-path", Inf, "--all-versions"],
            _process.Calls[1]);
        Assert.Equal(
            ["--uninstall-filter-driver", "--position", "upper", "--service-name", "MyFilter", "--class-guid", ClassGuidText],
            _process.Calls[2]);
        Assert.Equal(["--add-class-filter", "--position", "upper", "--service-name", "MyFilter", "--class-guid", ClassGuidText], _process.Calls[3]);
        Assert.Equal(["--remove-class-filter", "--position", "lower", "--service-name", "MyFilter", "--class-guid", ClassGuidText], _process.Calls[4]);
    }

    [Fact]
    public async Task InfDefaultOperations_BuildFixedArguments()
    {
        await _runner.InfDefaultInstall(Inf);
        await _runner.InfDefaultUninstall(Inf);
        Assert.Equal(["--inf-default-install", "--inf-path", Inf], _process.Calls[0]);
        Assert.Equal(["--inf-default-uninstall", "--inf-path", Inf], _process.Calls[1]);
    }

    [Fact]
    public async Task DriverServiceOperations_BuildFixedArguments()
    {
        await _runner.CreateDriverService("MyDrv", "My driver", @"C:\drivers\my.sys");
        await _runner.RemoveDriverService("MyDrv");
        Assert.Equal(
            ["--create-driver-service", "--bin-path", @"C:\drivers\my.sys", "--service-name", "MyDrv", "--display-name", "My driver"],
            _process.Calls[0]);
        Assert.Equal(["--remove-driver-service", "--service-name", "MyDrv"], _process.Calls[1]);
    }

    [Fact]
    public async Task DriverStoreAndRebootDelete_BuildFixedArguments()
    {
        await _runner.RemoveDriverStorePackage("my.inf", ClassGuid, "MyFilter");
        await _runner.RemoveDriverStorePackage("my.inf");
        await _runner.DeleteFileOnReboot(@"C:\drivers\old.sys");
        Assert.Equal(
            ["--remove-driver-store-package", "--inf-name", "my.inf", "--class-guid", ClassGuidText, "--service-name", "MyFilter"],
            _process.Calls[0]);
        Assert.Equal(["--remove-driver-store-package", "--inf-name", "my.inf"], _process.Calls[1]);
        Assert.Equal(["--delete-file-on-reboot", "--file-path", @"C:\drivers\old.sys"], _process.Calls[2]);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(1, false, false)]
    [InlineData(5, false, false)]
    public async Task ExitCodes_MapToResult(int exitCode, bool succeeded, bool reboot)
    {
        _process.Next = new NefconProcessOutput(exitCode, "tail", TimedOut: false);
        NefconResult result = await _runner.RemoveDriverService("MyDrv");
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal("tail", result.Output);
        Assert.Equal(succeeded, result.Succeeded);
        Assert.Equal(reboot, result.RebootRequired);
    }

    [Fact]
    public async Task Timeout_IsNeverSuccessful()
    {
        _process.Next = new NefconProcessOutput(-1, "", TimedOut: true);
        NefconResult result = await _runner.RemoveDriverService("MyDrv");
        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.False(result.RebootRequired);
    }

    [Fact]
    public async Task Invocations_RunOneAtATime()
    {
        _process.Delay = TimeSpan.FromMilliseconds(40);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _runner.RemoveDriverService("MyDrv")));
        Assert.Equal(5, _process.Calls.Count);
        Assert.Equal(1, _process.MaxConcurrent);
    }

    [Fact]
    public void InvalidInput_IsRejectedBeforeSpawning()
    {
        Assert.Throws<ArgumentException>(() => { _ = _runner.InstallDriver(@"relative\x.inf"); });
        Assert.Throws<ArgumentException>(() => { _ = _runner.InstallDriver(Inf, "--evil"); });
        Assert.Throws<ArgumentException>(() => { _ = _runner.InstallDriver(Inf, noDuplicates: true); });
        Assert.Throws<ArgumentException>(() => { _ = _runner.InstallDriver(Inf, "Root\\Foo", removeDuplicates: true); });
        Assert.Throws<ArgumentException>(() => { _ = _runner.RemoveDeviceNode("Root\\Foo", Guid.Empty); });
        Assert.Throws<ArgumentException>(() => { _ = _runner.RemoveDriverService("bad;name"); });
        Assert.Throws<ArgumentException>(() => { _ = _runner.RemoveDriverService("--all"); });
        Assert.Throws<ArgumentException>(() => { _ = _runner.RemoveDriverStorePackage("a.inf,b.inf"); });
        Assert.Throws<ArgumentException>(() => { _ = _runner.UninstallFilterDriver(ClassGuid, "F", ClassFilterPosition.Upper, allVersions: true); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = _runner.AddClassFilter(ClassGuid, "F", (ClassFilterPosition)9); });
        Assert.Empty(_process.Calls);
    }

    [Fact]
    public async Task MissingExecutable_Throws()
    {
        NefconRunner runner = new(
            new VestigoOptions { Devices = new DevicesOptions { NefconPath = Path.Combine(_directory, "missing.exe") } },
            _process,
            NullLogger<NefconRunner>.Instance);
        await Assert.ThrowsAsync<FileNotFoundException>(() => runner.RemoveDriverService("MyDrv"));
        Assert.Empty(_process.Calls);
    }

    [Fact]
    public void DefaultExecutable_IsBundledToolsPath()
    {
        NefconRunner runner = new(new VestigoOptions(), _process, NullLogger<NefconRunner>.Instance);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "tools", "nefconc.exe"), runner.ExecutablePath);
    }

    [Fact]
    public void Options_RejectRelativePathAndBadTimeout()
    {
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Devices = new DevicesOptions { NefconPath = "nefconc.exe" } }.Validate());
        Assert.Throws<InvalidOperationException>(() => new VestigoOptions { Devices = new DevicesOptions { NefconTimeout = TimeSpan.Zero } }.Validate());
    }

    private sealed class FakeProcess : INefconProcess
    {
        private int _running;
        private int _maxConcurrent;

        public List<string[]> Calls { get; } = [];

        public List<string> Executables { get; } = [];

        public List<TimeSpan> Timeouts { get; } = [];

        public NefconProcessOutput Next { get; set; } = new(0, "", TimedOut: false);

        public TimeSpan Delay { get; set; }

        public int MaxConcurrent => _maxConcurrent;

        public async Task<NefconProcessOutput> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            int running = Interlocked.Increment(ref _running);
            int seen;
            while (running > (seen = Volatile.Read(ref _maxConcurrent)))
            {
                Interlocked.CompareExchange(ref _maxConcurrent, running, seen);
            }

            lock (Calls)
            {
                Calls.Add([.. arguments]);
                Executables.Add(executable);
                Timeouts.Add(timeout);
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            Interlocked.Decrement(ref _running);
            return Next;
        }
    }
}