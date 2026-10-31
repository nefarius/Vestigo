using Microsoft.Extensions.Configuration;

using Vestigo.Configuration;
using Vestigo.Devices;
using Vestigo.Security;
using Vestigo.Services;

namespace Vestigo.Server.Tests;

public sealed class ServiceToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vestigo-svc-").FullName;
    private readonly FakeControl _control = new();
    private readonly StubNefcon _nefcon = new();
    private readonly VestigoOptions _options;
    private readonly ServiceTools _tools;

    public ServiceToolsTests()
    {
        File.WriteAllBytes(Path.Combine(_root, "good.sys"), []);
        File.WriteAllBytes(Path.Combine(_root, "tool.exe"), []);
        _options = new VestigoOptions
        {
            Roots = [new RootDefinition { Name = "drv", Path = _root, Access = RootAccess.Read | RootAccess.Execute }]
        };
        _tools = new ServiceTools(_control, _nefcon, new RootPolicy(_options), _options)
        {
            PollInterval = TimeSpan.FromMilliseconds(5)
        };
        _control.Add("Spooler", "Print Spooler", "win32", "WIN32_OWN_PROCESS", ServiceStates.Running, "auto");
        _control.Add("Dependent", "Dependent svc", "win32", "WIN32_OWN_PROCESS", ServiceStates.Running, "demand");
        _control.Add("Idle", "Idle service", "win32", "WIN32_SHARE_PROCESS", ServiceStates.Stopped, "demand");
        _control.Add("MyDrv", "My driver", "driver", "KERNEL_DRIVER", ServiceStates.Stopped, "demand");
        _control.Add("FsDrv", "Filter driver", "driver", "FILE_SYSTEM_DRIVER", ServiceStates.Running, "system");
        _control.Add("RpcSs", "RPC", "win32", "WIN32_SHARE_PROCESS", ServiceStates.Running, "auto");
        _control.Dependents["Spooler"] = ["Dependent"];
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void List_FiltersByKindNameAndState()
    {
        Assert.Equal(["FsDrv", "MyDrv"], Names(_tools.ListServices("driver")));
        Assert.Equal(["Dependent", "Idle", "RpcSs", "Spooler"], Names(_tools.ListServices("WIN32")));
        Assert.Equal(["Spooler"], Names(_tools.ListServices(nameContains: "print")));
        Assert.Equal(["Idle", "MyDrv"], Names(_tools.ListServices(state: "Stopped")));
        Assert.Equal(["Dependent", "FsDrv", "Idle", "MyDrv", "RpcSs", "Spooler"], Names(_tools.ListServices()));
    }

    [Fact]
    public void List_RejectsBadFilters_AndTruncates()
    {
        Assert.Equal("invalid_kind", _tools.ListServices("kernel").ErrorCode);
        Assert.Equal("invalid_state", _tools.ListServices(state: "bogus").ErrorCode);
        ServiceList page = _tools.ListServices(limit: 2).Value!;
        Assert.Equal(6, page.Total);
        Assert.Equal(2, page.Returned);
        Assert.True(page.Truncated);
    }

    [Fact]
    public void Get_ReturnsServiceOrNotFound()
    {
        Assert.Equal("KERNEL_DRIVER", _tools.GetService("MyDrv").Value!.ServiceType);
        Assert.Equal("not_found", _tools.GetService("Missing").ErrorCode);
        Assert.Equal("invalid_name", _tools.GetService("..\\x").ErrorCode);
        Assert.True(_tools.GetService("RpcSs").Success);
    }

    [Fact]
    public async Task MutatingTools_RefuseProtectedNames_CaseInsensitively()
    {
        Assert.Equal("protected_service", (await _tools.StartService("rpcss")).ErrorCode);
        Assert.Equal("protected_service", (await _tools.StopService("RPCSS")).ErrorCode);
        Assert.Equal("protected_service", (await _tools.RestartService("Vestigo")).ErrorCode);
        Assert.Equal("protected_service", _tools.SetServiceStartType("SamSs", "disabled").ErrorCode);
        Assert.Equal("protected_service", (await _tools.DeleteService("Power")).ErrorCode);
        Assert.Equal(
            "protected_service",
            (await _tools.CreateDriverService("LSM", "drv:good.sys")).ErrorCode);
        Assert.Empty(_control.Calls);
        Assert.Empty(_nefcon.Calls);
    }

    [Fact]
    public async Task Start_WaitsForRunning()
    {
        _control.PollsUntilTransition = 3;
        OperationResult<ServiceInfo> result = await _tools.StartService("Idle", ["a", "b"], timeoutSeconds: 5);
        Assert.True(result.Success);
        Assert.Equal(ServiceStates.Running, result.Value!.State);
        Assert.Contains("start Idle a,b", _control.Calls);
    }

    [Fact]
    public async Task Start_TimesOutWhenNeverRunning()
    {
        _control.PollsUntilTransition = int.MaxValue;
        OperationResult<ServiceInfo> result = await _tools.StartService("Idle", timeoutSeconds: 1);
        Assert.Equal("timeout", result.ErrorCode);
        Assert.Contains("start_pending", result.Error);
    }

    [Fact]
    public async Task Start_ZeroTimeoutReturnsWithoutWaiting()
    {
        _control.PollsUntilTransition = int.MaxValue;
        OperationResult<ServiceInfo> result = await _tools.StartService("Idle", timeoutSeconds: 0);
        Assert.True(result.Success);
        Assert.Equal(ServiceStates.StartPending, result.Value!.State);
    }

    [Fact]
    public async Task Start_AlreadyRunningIsNoOp_AndScmErrorsMap()
    {
        Assert.True((await _tools.StartService("Spooler")).Success);
        Assert.DoesNotContain(_control.Calls, call => call.StartsWith("start", StringComparison.Ordinal));

        _control.StartFailure = new ServiceControlException("driver_signature", "unsigned");
        OperationResult<ServiceInfo> failed = await _tools.StartService("MyDrv");
        Assert.Equal("driver_signature", failed.ErrorCode);
    }

    [Fact]
    public async Task Stop_WithoutDependentsFlag_DoesNotTouchDependents()
    {
        OperationResult<ServiceInfo> result = await _tools.StopService("Spooler");
        Assert.True(result.Success);
        Assert.Equal(["stop Spooler"], _control.Calls);
    }

    [Fact]
    public async Task Stop_WithDependents_StopsDependentsFirst()
    {
        OperationResult<ServiceInfo> result = await _tools.StopService("Spooler", stopDependents: true);
        Assert.True(result.Success);
        Assert.Equal(["stop Dependent", "stop Spooler"], _control.Calls);
    }

    [Fact]
    public async Task Stop_WithProtectedDependent_Refuses()
    {
        _control.Dependents["Spooler"] = ["RpcSs"];
        OperationResult<ServiceInfo> result = await _tools.StopService("Spooler", stopDependents: true);
        Assert.Equal("protected_service", result.ErrorCode);
        Assert.Empty(_control.Calls);
    }

    [Fact]
    public async Task Stop_TimesOut_AndAlreadyStoppedIsNoOp()
    {
        _control.PollsUntilTransition = int.MaxValue;
        Assert.Equal("timeout", (await _tools.StopService("Spooler", timeoutSeconds: 1)).ErrorCode);

        _control.Calls.Clear();
        Assert.True((await _tools.StopService("Idle")).Success);
        Assert.Empty(_control.Calls);
    }

    [Fact]
    public async Task Restart_StopsThenStarts()
    {
        OperationResult<ServiceInfo> result = await _tools.RestartService("Spooler");
        Assert.True(result.Success);
        Assert.Equal(["stop Spooler", "start Spooler "], _control.Calls);
        Assert.Equal(ServiceStates.Running, result.Value!.State);
    }

    [Fact]
    public async Task Restart_StopTimeoutAbortsBeforeStart()
    {
        _control.PollsUntilTransition = int.MaxValue;
        OperationResult<ServiceInfo> result = await _tools.RestartService("Spooler", timeoutSeconds: 1);
        Assert.Equal("timeout", result.ErrorCode);
        Assert.DoesNotContain(_control.Calls, call => call.StartsWith("start", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("boot", "MyDrv", true)]
    [InlineData("SYSTEM", "FsDrv", true)]
    [InlineData("auto", "Spooler", true)]
    [InlineData("disabled", "Spooler", true)]
    [InlineData("boot", "Spooler", false)]
    [InlineData("system", "Idle", false)]
    [InlineData("later", "Idle", false)]
    public void SetStartType_ValidatesKind(string startType, string name, bool allowed)
    {
        OperationResult<ServiceInfo> result = _tools.SetServiceStartType(name, startType);
        Assert.Equal(allowed, result.Success);
        if (allowed)
        {
            Assert.Equal(startType.ToLowerInvariant(), result.Value!.StartType);
        }
        else
        {
            Assert.Equal("invalid_start_type", result.ErrorCode);
            Assert.Empty(_control.Calls);
        }
    }

    [Fact]
    public void SetStartType_UnknownService_NotFound() =>
        Assert.Equal("not_found", _tools.SetServiceStartType("Nope", "auto").ErrorCode);

    [Fact]
    public async Task CreateDriverService_ResolvesThroughRootPolicy()
    {
        OperationResult<ServiceChangeResult> result = await _tools.CreateDriverService("NewDrv", "drv:good.sys", "New driver");
        Assert.True(result.Success);
        Assert.Equal(("NewDrv", "New driver", Path.Combine(_root, "good.sys")), _nefcon.Created.Single());

        _nefcon.Created.Clear();
        await _tools.CreateDriverService("NewDrv2", Path.Combine(_root, "good.sys"));
        Assert.Equal("NewDrv2", _nefcon.Created.Single().Display);
    }

    [Fact]
    public async Task CreateDriverService_RejectsBadPaths()
    {
        Assert.Equal("unknown_root", (await _tools.CreateDriverService("NewDrv", "other:good.sys")).ErrorCode);
        Assert.Equal("not_found", (await _tools.CreateDriverService("NewDrv", "drv:missing.sys")).ErrorCode);
        Assert.Equal("unsupported_extension", (await _tools.CreateDriverService("NewDrv", "drv:tool.exe")).ErrorCode);
        Assert.NotEqual(
            null,
            (await _tools.CreateDriverService("NewDrv", @"drv:..\..\Windows\System32\drivers\null.sys")).ErrorCode);
        Assert.Equal("invalid_name", (await _tools.CreateDriverService("-bad", "drv:good.sys")).ErrorCode);
        Assert.Equal("already_exists", (await _tools.CreateDriverService("MyDrv", "drv:good.sys")).ErrorCode);
        Assert.Empty(_nefcon.Created);
    }

    [Fact]
    public async Task CreateDriverService_RootWithoutExecute_Refused()
    {
        _options.Roots[0].Access = RootAccess.Read;
        Assert.Equal("access_denied", (await _tools.CreateDriverService("NewDrv", "drv:good.sys")).ErrorCode);
    }

    [Fact]
    public async Task NefconResults_AreMapped()
    {
        _nefcon.Next = new NefconResult(3010, "reboot");
        OperationResult<ServiceChangeResult> rebooted = await _tools.DeleteService("MyDrv");
        Assert.True(rebooted.Value!.RebootRequired);
        Assert.Equal(["MyDrv"], _nefcon.Removed);

        _nefcon.Next = new NefconResult(1, "access denied");
        OperationResult<ServiceChangeResult> failed = await _tools.DeleteService("MyDrv");
        Assert.Equal("nefcon_failed", failed.ErrorCode);
        Assert.Contains("exit code 1", failed.Error);

        _nefcon.Next = new NefconResult(-1, "", TimedOut: true);
        Assert.Equal("nefcon_timeout", (await _tools.DeleteService("MyDrv")).ErrorCode);
    }

    [Fact]
    public async Task DeleteService_RefusesWin32AndMissing()
    {
        Assert.Equal("not_a_driver", (await _tools.DeleteService("Idle")).ErrorCode);
        Assert.Equal("not_a_driver", (await _tools.DeleteService("FsDrv")).ErrorCode);
        Assert.Equal("not_found", (await _tools.DeleteService("Nope")).ErrorCode);
        Assert.Equal("invalid_name", (await _tools.DeleteService(@"C:\x\y.sys")).ErrorCode);
        Assert.Empty(_nefcon.Removed);
    }

    [Fact]
    public void ServicesOptions_DefaultsBindingAndValidation()
    {
        Assert.Equal(["Vestigo", "RpcSs", "DcomLaunch", "PlugPlay", "Power", "LSM", "SamSs"], new VestigoOptions().Services.ProtectedNames);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Vestigo:Services:ProtectedNames:0"] = "MyCritical" })
            .Build();
        VestigoOptions bound = configuration.GetSection("Vestigo").Get<VestigoOptions>()!;
        bound.Validate();
        Assert.True(bound.Services.IsProtected("mycritical"));
        Assert.True(bound.Services.IsProtected("RpcSs"));

        VestigoOptions invalid = new() { Services = new ServicesOptions { ProtectedNames = ["ok", " "] } };
        Assert.Throws<InvalidOperationException>(invalid.Validate);
    }

    private static string[] Names(OperationResult<ServiceList> result) =>
        result.Value!.Services.Select(service => service.Name).ToArray();

    private sealed class FakeControl : IServiceControl
    {
        private readonly Dictionary<string, ServiceInfo> _services = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Target, int Polls)> _pending = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Calls { get; } = [];

        public Dictionary<string, string[]> Dependents { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Get calls needed before a pending start/stop completes.</summary>
        public int PollsUntilTransition { get; set; }

        public ServiceControlException? StartFailure { get; set; }

        public void Add(string name, string display, string kind, string type, string state, string startType) =>
            _services[name] = new ServiceInfo(name, display, kind, type, state, startType, @"C:\x.sys", "LocalSystem", []);

        public IReadOnlyList<ServiceInfo> List(ServiceKind kind) => _services.Values
            .Where(service => kind == ServiceKind.All || service.Kind == kind.ToString().ToLowerInvariant())
            .ToList();

        public ServiceInfo? Get(string name)
        {
            if (!_services.TryGetValue(name, out ServiceInfo? info))
            {
                return null;
            }

            if (_pending.TryGetValue(name, out (string Target, int Polls) pending))
            {
                if (pending.Polls <= 0)
                {
                    _pending.Remove(name);
                    info = info with { State = pending.Target };
                    _services[name] = info;
                }
                else
                {
                    _pending[name] = (pending.Target, pending.Polls - 1);
                }
            }

            return info;
        }

        public void Start(string name, IReadOnlyList<string> arguments)
        {
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            Calls.Add($"start {name} {string.Join(',', arguments)}");
            Transition(name, ServiceStates.StartPending, ServiceStates.Running);
        }

        public void Stop(string name)
        {
            Calls.Add($"stop {name}");
            Transition(name, ServiceStates.StopPending, ServiceStates.Stopped);
        }

        public IReadOnlyList<string> ActiveDependents(string name) =>
            Dependents.TryGetValue(name, out string[]? list) ? list : [];

        public void SetStartType(string name, ServiceStartType startType)
        {
            Calls.Add($"set {name} {startType}");
            _services[name] = _services[name] with { StartType = startType.ToString().ToLowerInvariant() };
        }

        private void Transition(string name, string interim, string target)
        {
            if (PollsUntilTransition <= 0)
            {
                _services[name] = _services[name] with { State = target };
                return;
            }

            _services[name] = _services[name] with { State = interim };
            _pending[name] = (target, PollsUntilTransition);
        }
    }

    private sealed class StubNefcon : INefconRunner
    {
        public NefconResult Next { get; set; } = new(0, "ok");

        public List<(string Name, string Display, string Path)> Created { get; } = [];

        public List<string> Removed { get; } = [];

        public List<string[]> Calls => [.. Created.Select(c => new[] { c.Name }), .. Removed.Select(r => new[] { r })];

        public Task<NefconResult> CreateDriverService(string serviceName, string displayName, string binPath, CancellationToken cancellationToken = default)
        {
            Created.Add((serviceName, displayName, binPath));
            return Task.FromResult(Next);
        }

        public Task<NefconResult> RemoveDriverService(string serviceName, CancellationToken cancellationToken = default)
        {
            Removed.Add(serviceName);
            return Task.FromResult(Next);
        }

        public Task<NefconResult> InstallDriver(string infPath, string? hardwareId = null, bool noDuplicates = false, bool removeDuplicates = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NefconResult> UninstallDriver(string infPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NefconResult> CreateDeviceNode(string className, Guid classGuid, string hardwareId, bool noDuplicates = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NefconResult> RemoveDeviceNode(string hardwareId, Guid classGuid, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NefconResult> InstallFilterDriver(string infPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NefconResult> UninstallFilterDriver(Guid classGuid, string serviceName, ClassFilterPosition position, string? infPath = null, bool allVersions = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NefconResult> AddClassFilter(Guid classGuid, string serviceName, ClassFilterPosition position, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NefconResult> RemoveClassFilter(Guid classGuid, string serviceName, ClassFilterPosition position, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NefconResult> InfDefaultInstall(string infPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NefconResult> InfDefaultUninstall(string infPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NefconResult> RemoveDriverStorePackage(string infName, Guid? classGuid = null, string? serviceName = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<NefconResult> DeleteFileOnReboot(string filePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
