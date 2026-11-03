using Vestigo.Configuration;
using Vestigo.Devices;
using Vestigo.Diagnostics;
using Vestigo.EventLogs;
using Vestigo.Services;
using Vestigo.SystemInfo;

namespace Vestigo.Server.Tests;

public sealed class DeviceProblemTests
{
    private const string Id = @"ROOT\VESTIGOTEST\0000";

    [Fact]
    public void Rules_CoverEveryListedCodeWithoutRanking()
    {
        foreach (uint code in new uint[] { 1, 10, 18, 19, 22, 24, 28, 31, 37, 39, 43, 45, 52 })
        {
            DeviceProblemReport report = DeviceProblemRules.Assess(Evidence(Device(code)));

            Assert.False(report.InsufficientEvidence);
            Assert.NotNull(report.Explanation);
            Assert.Contains(code.ToString(), report.Explanation, StringComparison.Ordinal);
            Assert.DoesNotContain("rank", report.Explanation, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Rules_TreatsAMissingProblemCodeAsInsufficientEvidence()
    {
        DeviceProblemReport missing = DeviceProblemRules.Assess(Evidence(Device(null)));

        Assert.True(missing.InsufficientEvidence);
        Assert.Null(missing.Explanation);
        Assert.Empty(missing.Hypotheses);

        DeviceProblemReport clear = DeviceProblemRules.Assess(Evidence(Device(0)));
        Assert.False(clear.InsufficientEvidence);
        Assert.Equal("No CM_PROB code is set on this device.", clear.Explanation);
    }

    [Fact]
    public void Rules_ReportInsufficientEvidenceForAnUnknownCode()
    {
        DeviceProblemReport report = DeviceProblemRules.Assess(Evidence(Device(99)));

        Assert.True(report.InsufficientEvidence);
        Assert.Null(report.Explanation);
        Assert.Empty(report.Hypotheses);
    }

    [Fact]
    public void Rules_TieHypothesesToInstallServiceAndBootEvidence()
    {
        InstallSection failed = new("title", null, null, "FAILURE(0xE0000228)", ["0xE0000228"], [], [], "setupapi.dev.log", true, ["SECRET-RAW"], false);
        DeviceProblemEvidence install = Evidence(Device(28)) with
        {
            InstallHistory = new InstallHistory(Id, false, [failed])
        };
        DeviceProblemReport failedInstall = DeviceProblemRules.Assess(install);
        Assert.Contains(failedInstall.Hypotheses, item => item.Label == "install-exit" && item.Statement.Contains("0xE0000228", StringComparison.Ordinal));
        Assert.DoesNotContain("SECRET-RAW", string.Join('\n', failedInstall.Observed.Select(fact => fact.Fact)), StringComparison.Ordinal);

        InstallSection succeeded = new("title", null, null, "success", [], [], [], "setupapi.dev.log", true, null, false);
        DeviceProblemEvidence recovered = Evidence(Device(28)) with
        {
            InstallHistory = new InstallHistory(Id, false, [failed, succeeded])
        };
        Assert.DoesNotContain(DeviceProblemRules.Assess(recovered).Hypotheses, item => item.Label == "install-exit");

        DeviceProblemEvidence stopped = Evidence(Device(10, service: "vestigo")) with
        {
            Service = new ServiceInfo("vestigo", "Vestigo", "driver", "KERNEL_DRIVER", ServiceStates.Stopped, "disabled", null, null, [])
        };
        Assert.Contains(DeviceProblemRules.Assess(stopped).Hypotheses, item => item.Label == "service-state");

        DeviceProblemReport disabled = DeviceProblemRules.Assess(Evidence(Device(22)));
        Assert.Empty(disabled.Hypotheses);

        DeviceProblemEvidence unsigned = Evidence(Device(52)) with
        {
            BootSecurity = new BootSecurityResult(true, true, false, false, null, false, [])
        };
        Assert.Contains(DeviceProblemRules.Assess(unsigned).Hypotheses, item => item.Label == "boot-security");
        Assert.Empty(DeviceProblemRules.Assess(Evidence(Device(52))).Hypotheses);
    }

    [Fact]
    public void Gatherer_ReturnsDeviceNotFoundAndSkipsUnavailableProbes()
    {
        FakeDevices missing = new() { Error = new DeviceControlException(DeviceControlException.DeviceNotFound, "The device was not found.") };
        FakeLogs logs = new();
        DeviceEvidenceGatherer gatherer = new(missing, logs, new VestigoOptions());

        Assert.Equal("device_not_found", gatherer.Explain(Id).ErrorCode);

        FakeDevices present = new() { Device = Device(10, service: "vestigo") };
        DeviceProblemReport report = new DeviceEvidenceGatherer(present, logs, new VestigoOptions()).Explain(Id).Value!;

        Assert.False(report.InsufficientEvidence);
        Assert.Contains(report.Observed, fact => fact.Source == "service" && fact.Fact == "unavailable");
        Assert.Contains(report.Observed, fact => fact.Source == "kernel-pnp" && fact.Fact == "unavailable");
        Assert.False(logs.IncludeRaw);
    }

    private static DeviceProblemEvidence Evidence(DeviceDetails device) =>
        new(device, new ClassFilterSet([], []), null, null, null, null, null, [], null, new InstallHistory(device.InstanceId, false, []), null);

    private static DeviceDetails Device(uint? code, string? service = null) => new(
        Id,
        null,
        [],
        [],
        "Vestigo test",
        "Test",
        Guid.Empty,
        service,
        true,
        code,
        0,
        null,
        null,
        null,
        "1.2.3.4",
        null,
        @"C:\Windows\INF\oem7.inf");

    private sealed class FakeDevices : IDeviceControl
    {
        public DeviceDetails? Device { get; init; }

        public Exception? Error { get; init; }

        public DeviceDetails GetDevice(string instanceId) => Error is not null ? throw Error : Device!;

        public ClassFilterSet GetClassFilters(Guid classGuid) => new([], []);

        public void Restart(string instanceId) => throw new NotSupportedException();

        public void Enable(string instanceId) => throw new NotSupportedException();

        public void Disable(string instanceId) => throw new NotSupportedException();

        public IReadOnlyList<DeviceInterfaceMatch> FindByInterface(Guid interfaceGuid, bool presentOnly, int maxResults) => throw new NotSupportedException();

        public IReadOnlyList<string> FindByHardwareId(Guid classGuid, string hardwareId, bool presentOnly, bool allowPartial) => throw new NotSupportedException();

        public IReadOnlyList<DeviceEntry> EnumerateDevices(bool presentOnly) => throw new NotSupportedException();

        public IReadOnlyList<DriverStoreEntry> GetDriverStore(bool includeInbox) => throw new NotSupportedException();
    }

    private sealed class FakeLogs : ISetupApiLogSource
    {
        public bool? IncludeRaw { get; private set; }

        public OperationResult<InstallHistory> Read(string instanceId, DateTimeOffset? since, int limit, bool includeRaw, long maxBytes)
        {
            IncludeRaw = includeRaw;
            return OperationResult<InstallHistory>.Ok(new InstallHistory(instanceId, false, []));
        }
    }
}
