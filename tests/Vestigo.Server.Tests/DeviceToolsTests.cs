using Nefarius.Utilities.DeviceManagement.Exceptions;

using Vestigo.Abstractions;
using Vestigo.Configuration;
using Vestigo.Devices;
using Vestigo.Security;
using Vestigo.Tools;

namespace Vestigo.Server.Tests;

public sealed class DeviceToolsTests : IDisposable
{
    private const string ClassGuidText = "4d36e97d-e325-11ce-bfc1-08002be10318";
    private static readonly Guid ClassGuid = Guid.Parse(ClassGuidText);

    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-devtools-").FullName;
    private readonly FakeRunner _runner = new();
    private readonly FakeControl _control = new();
    private readonly DeviceTools _tools;
    private readonly string _inf;

    public DeviceToolsTests()
    {
        _inf = Path.Combine(_root, "drv.inf");
        File.WriteAllText(_inf, "[Version]");
        File.WriteAllText(Path.Combine(_root, "drv.txt"), "x");
        VestigoOptions options = new() { Roots = [new RootDefinition { Name = "drivers", Path = _root, Access = RootAccess.Read }] };
        _tools = new DeviceTools(_runner, _control, new RootPolicy(options));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task InstallDriver_ResolvesNamedRootAndMapsRebootRequired()
    {
        _runner.Result = new NefconResult(3010, "ok");
        OperationResult<DriverActionResult> result = await _tools.InstallDriver("drivers:drv.inf", "Root\\Foo", noDuplicates: true);

        Assert.True(result.Success);
        Assert.Equal(new DriverActionResult(3010, "ok", true, false), result.Value);
        Assert.Equal($"InstallDriver {_inf} Root\\Foo True False", _runner.Calls.Single());
    }

    [Fact]
    public async Task NefconFailure_KeepsExitCodeAndOutput()
    {
        _runner.Result = new NefconResult(1, "not admin");
        OperationResult<DriverActionResult> result = await _tools.UninstallDriver(_inf);

        Assert.False(result.Success);
        Assert.Equal("nefcon_failed", result.ErrorCode);
        Assert.Equal(1, result.Value!.ExitCode);
        Assert.Equal("not admin", result.Value.OutputTail);
        Assert.False(result.Value.RebootRequired);
    }

    [Fact]
    public async Task NefconTimeout_IsReported()
    {
        _runner.Result = new NefconResult(-1, "", TimedOut: true);
        OperationResult<DriverActionResult> result = await _tools.InfDefaultInstall(_inf);

        Assert.Equal("nefcon_timeout", result.ErrorCode);
        Assert.True(result.Value!.TimedOut);
    }

    [Theory]
    [InlineData("drivers:missing.inf", "not_found")]
    [InlineData("drivers:drv.txt", "invalid_path")]
    [InlineData("other:drv.inf", "unknown_root")]
    [InlineData("C:\\Windows\\System32\\drivers\\x.inf", "outside_root")]
    [InlineData("", "invalid_path")]
    public async Task InfPaths_AreConfinedToRoots(string path, string code)
    {
        OperationResult<DriverActionResult> result = await _tools.InstallFilterDriver(path);

        Assert.Equal(code, result.ErrorCode);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task InstallDriver_ValidatesDuplicateOptions()
    {
        Assert.Equal("invalid_request", (await _tools.InstallDriver(_inf, noDuplicates: true)).ErrorCode);
        Assert.Equal("invalid_request", (await _tools.InstallDriver(_inf, "Root\\Foo", removeDuplicates: true)).ErrorCode);
        Assert.Equal("invalid_request", (await _tools.InstallDriver(_inf, "-evil")).ErrorCode);
        Assert.Empty(_runner.Calls);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    public async Task Guids_AreValidated(string guid)
    {
        Assert.Equal("invalid_guid", (await _tools.CreateDeviceNode("System", guid, "Root\\Foo")).ErrorCode);
        Assert.Equal("invalid_guid", (await _tools.RemoveDeviceNode("Root\\Foo", guid)).ErrorCode);
        Assert.Equal("invalid_guid", (await _tools.AddClassFilter(guid, "svc", "upper")).ErrorCode);
        Assert.Equal("invalid_guid", (await _tools.RemoveDriverStorePackage("a.inf", guid)).ErrorCode);
        Assert.Equal("invalid_guid", _tools.FindDevicesByInterface(guid).ErrorCode);
        Assert.Equal("invalid_guid", _tools.FindDevicesByHardwareId(guid, "Root\\Foo").ErrorCode);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task ClassFilters_ParsePosition()
    {
        await _tools.AddClassFilter(ClassGuidText, "svc", "LOWER");
        await _tools.RemoveClassFilter("{" + ClassGuidText + "}", "svc", "upper");

        Assert.Equal([$"AddClassFilter {ClassGuid} svc Lower", $"RemoveClassFilter {ClassGuid} svc Upper"], _runner.Calls);
        Assert.Equal("invalid_request", (await _tools.AddClassFilter(ClassGuidText, "svc", "middle")).ErrorCode);
        Assert.Equal("invalid_request", (await _tools.AddClassFilter(ClassGuidText, "--x", "upper")).ErrorCode);
    }

    [Fact]
    public async Task UninstallFilterDriver_AllVersionsNeedsInf()
    {
        Assert.Equal("invalid_request", (await _tools.UninstallFilterDriver(ClassGuidText, "svc", "upper", allVersions: true)).ErrorCode);
        Assert.Empty(_runner.Calls);

        await _tools.UninstallFilterDriver(ClassGuidText, "svc", "upper", "drivers:drv.inf", allVersions: true);
        Assert.Equal($"UninstallFilterDriver {ClassGuid} svc Upper {_inf} True", _runner.Calls.Single());
    }

    [Fact]
    public async Task RemoveDriverStorePackage_RequiresPlainInfName()
    {
        foreach (string bad in new[] { "", "a.txt", "dir\\a.inf", "a.inf,b.inf", "-a.inf" })
        {
            Assert.Equal("invalid_request", (await _tools.RemoveDriverStorePackage(bad)).ErrorCode);
        }

        Assert.Empty(_runner.Calls);
        await _tools.RemoveDriverStorePackage("a.inf", ClassGuidText, "svc");
        Assert.Equal($"RemoveDriverStorePackage a.inf {ClassGuid} svc", _runner.Calls.Single());
    }

    [Fact]
    public async Task CreateAndRemoveDeviceNode_PassFixedArguments()
    {
        await _tools.CreateDeviceNode("System", ClassGuidText, "Root\\Foo", noDuplicates: true);
        await _tools.RemoveDeviceNode("Root\\Foo", ClassGuidText);

        Assert.Equal([$"CreateDeviceNode System {ClassGuid} Root\\Foo True", $"RemoveDeviceNode Root\\Foo {ClassGuid}"], _runner.Calls);
    }

    [Fact]
    public async Task RunnerExceptions_MapToErrorCodes()
    {
        _runner.Throw = new FileNotFoundException("missing nefcon");
        Assert.Equal("nefcon_not_found", (await _tools.InfDefaultUninstall(_inf)).ErrorCode);

        _runner.Throw = new ArgumentException("bad", "serviceName");
        Assert.Equal("invalid_request", (await _tools.AddClassFilter(ClassGuidText, "svc", "upper")).ErrorCode);
    }

    [Fact]
    public void DeviceState_CallsControlAndMapsErrors()
    {
        OperationResult<DeviceStateResult> ok = _tools.RestartDevice("ROOT\\SYSTEM\\0001");
        Assert.True(ok.Success);
        Assert.Equal(new DeviceStateResult("ROOT\\SYSTEM\\0001", "restart"), ok.Value);

        _tools.EnableDevice("ROOT\\SYSTEM\\0002");
        _tools.DisableDevice("ROOT\\SYSTEM\\0003");
        Assert.Equal(["restart ROOT\\SYSTEM\\0001", "enable ROOT\\SYSTEM\\0002", "disable ROOT\\SYSTEM\\0003"], _control.Calls);

        _control.Throw = new DeviceControlException(DeviceControlException.DeviceNotFound, "gone");
        OperationResult<DeviceStateResult> missing = _tools.DisableDevice("ROOT\\SYSTEM\\0009");
        Assert.Equal("device_not_found", missing.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NOSLASH")]
    [InlineData("-ROOT\\X")]
    [InlineData("ROOT\\X\n")]
    public void DeviceState_ValidatesInstanceId(string instanceId)
    {
        Assert.Equal("invalid_instance_id", _tools.RestartDevice(instanceId).ErrorCode);
        Assert.Equal("invalid_instance_id", _tools.EnableDevice(instanceId).ErrorCode);
        Assert.Equal("invalid_instance_id", _tools.DisableDevice(instanceId).ErrorCode);
        Assert.Empty(_control.Calls);
    }

    [Fact]
    public void Queries_ReturnMatchesAndTruncationFlag()
    {
        _control.Interfaces = [new DeviceInterfaceMatch("ROOT\\A\\0", "\\\\?\\root#a")];
        OperationResult<InterfaceSearchResult> byInterface = _tools.FindDevicesByInterface(ClassGuidText);
        Assert.Single(byInterface.Value!.Devices);
        Assert.False(byInterface.Value.Truncated);

        _control.Interfaces = Enumerable.Range(0, DeviceTools.MaxInterfaceResults).Select(i => new DeviceInterfaceMatch($"ROOT\\A\\{i}", "p")).ToList();
        Assert.True(_tools.FindDevicesByInterface(ClassGuidText).Value!.Truncated);

        _control.InstanceIds = ["ROOT\\A\\0"];
        Assert.Equal(["ROOT\\A\\0"], _tools.FindDevicesByHardwareId(ClassGuidText, "Root\\A", allowPartial: true).Value!.InstanceIds);
        Assert.Equal("invalid_request", _tools.FindDevicesByHardwareId(ClassGuidText, "").ErrorCode);

        _control.Throw = new DeviceControlException(DeviceControlException.WindowsRequired, "no");
        Assert.Equal("windows_required", _tools.FindDevicesByHardwareId(ClassGuidText, "Root\\A").ErrorCode);
        Assert.Equal("windows_required", _tools.FindDevicesByInterface(ClassGuidText).ErrorCode);
    }

    [Fact]
    public void LibraryExceptions_MapToStableCodes()
    {
        Assert.Equal("device_not_found", DeviceControl.Map((Exception)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PnPDeviceNotFoundException)))!.Code);
        Assert.Equal("win32_failed", DeviceControl.Map((Exception)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Nefarius.Utilities.DeviceManagement.Exceptions.Win32Exception)))!.Code);
        Assert.Equal("windows_required", DeviceControl.Map(new PlatformNotSupportedException())!.Code);
        Assert.Equal("device_operation_failed", DeviceControl.Map(new InvalidOperationException("x"))!.Code);
        Assert.Null(DeviceControl.Map(new OutOfMemoryException()));
    }

    [Fact]
    public void DevicesModule_IsRegisteredWithItsTools()
    {
        Vestigo.Modules.VestigoModule module = Vestigo.Modules.ModuleRegistry.All.Single(m => m.Name == "Devices");
        Assert.Equal([typeof(DeviceTools)], module.ToolTypes);
    }

    [Fact]
    public void RealDeviceControl_FindsNothingForUnusedInterface_OrSkips()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Requires Windows.");
        }

        // Read-only: a random interface GUID matches no device. Proves the net9.0 package assets load on net10.
        IReadOnlyList<DeviceInterfaceMatch> found;
        try
        {
            found = new DeviceControl().FindByInterface(Guid.NewGuid(), presentOnly: true, maxResults: 4);
        }
        catch (DeviceControlException ex) when (ex.Code is DeviceControlException.Win32Failed or DeviceControlException.ConfigManagerFailed)
        {
            Assert.Skip($"Device enumeration is not available here: {ex.Message}");
            return;
        }

        Assert.Empty(found);
    }

    private sealed class FakeControl : IDeviceControl
    {
        public List<string> Calls { get; } = [];

        public Exception? Throw { get; set; }

        public IReadOnlyList<DeviceInterfaceMatch> Interfaces { get; set; } = [];

        public IReadOnlyList<string> InstanceIds { get; set; } = [];

        public void Restart(string instanceId) => Record("restart", instanceId);

        public void Enable(string instanceId) => Record("enable", instanceId);

        public void Disable(string instanceId) => Record("disable", instanceId);

        public IReadOnlyList<DeviceInterfaceMatch> FindByInterface(Guid interfaceGuid, bool presentOnly, int maxResults)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            return Interfaces;
        }

        public IReadOnlyList<string> FindByHardwareId(Guid classGuid, string hardwareId, bool presentOnly, bool allowPartial)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            return InstanceIds;
        }

        private void Record(string action, string instanceId)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Calls.Add($"{action} {instanceId}");
        }
    }

    private sealed class FakeRunner : INefconRunner
    {
        public List<string> Calls { get; } = [];

        public NefconResult Result { get; set; } = new(0, "");

        public Exception? Throw { get; set; }

        private Task<NefconResult> Call(string call)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Calls.Add(call);
            return Task.FromResult(Result);
        }

        public Task<NefconResult> InstallDriver(string infPath, string? hardwareId = null, bool noDuplicates = false, bool removeDuplicates = false, CancellationToken cancellationToken = default) =>
            Call($"InstallDriver {infPath} {hardwareId} {noDuplicates} {removeDuplicates}");

        public Task<NefconResult> UninstallDriver(string infPath, CancellationToken cancellationToken = default) => Call($"UninstallDriver {infPath}");

        public Task<NefconResult> CreateDeviceNode(string className, Guid classGuid, string hardwareId, bool noDuplicates = false, CancellationToken cancellationToken = default) =>
            Call($"CreateDeviceNode {className} {classGuid} {hardwareId} {noDuplicates}");

        public Task<NefconResult> RemoveDeviceNode(string hardwareId, Guid classGuid, CancellationToken cancellationToken = default) =>
            Call($"RemoveDeviceNode {hardwareId} {classGuid}");

        public Task<NefconResult> InstallFilterDriver(string infPath, CancellationToken cancellationToken = default) => Call($"InstallFilterDriver {infPath}");

        public Task<NefconResult> UninstallFilterDriver(Guid classGuid, string serviceName, ClassFilterPosition position, string? infPath = null, bool allVersions = false, CancellationToken cancellationToken = default) =>
            Call($"UninstallFilterDriver {classGuid} {serviceName} {position} {infPath} {allVersions}");

        public Task<NefconResult> AddClassFilter(Guid classGuid, string serviceName, ClassFilterPosition position, CancellationToken cancellationToken = default) =>
            Call($"AddClassFilter {classGuid} {serviceName} {position}");

        public Task<NefconResult> RemoveClassFilter(Guid classGuid, string serviceName, ClassFilterPosition position, CancellationToken cancellationToken = default) =>
            Call($"RemoveClassFilter {classGuid} {serviceName} {position}");

        public Task<NefconResult> InfDefaultInstall(string infPath, CancellationToken cancellationToken = default) => Call($"InfDefaultInstall {infPath}");

        public Task<NefconResult> InfDefaultUninstall(string infPath, CancellationToken cancellationToken = default) => Call($"InfDefaultUninstall {infPath}");

        public Task<NefconResult> CreateDriverService(string serviceName, string displayName, string binPath, CancellationToken cancellationToken = default) =>
            Call($"CreateDriverService {serviceName}");

        public Task<NefconResult> RemoveDriverService(string serviceName, CancellationToken cancellationToken = default) => Call($"RemoveDriverService {serviceName}");

        public Task<NefconResult> RemoveDriverStorePackage(string infName, Guid? classGuid = null, string? serviceName = null, CancellationToken cancellationToken = default) =>
            Call($"RemoveDriverStorePackage {infName} {classGuid} {serviceName}");

        public Task<NefconResult> DeleteFileOnReboot(string filePath, CancellationToken cancellationToken = default) => Call($"DeleteFileOnReboot {filePath}");
    }
}
