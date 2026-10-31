using Vestigo.Services;

namespace Vestigo.Server.Tests;

/// <summary>Read-only integration tests against the real Service Control Manager.</summary>
public sealed class WindowsServiceControlTests
{
    private readonly WindowsServiceControl _control = new();

    [Fact]
    public void List_Win32_ContainsWellKnownServices()
    {
        IReadOnlyList<ServiceInfo> services = _control.List(ServiceKind.Win32);
        Assert.NotEmpty(services);
        Assert.All(services, service => Assert.Equal("win32", service.Kind));
        Assert.Contains(services, service =>
            service.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase)
            || service.Name.Equals("RpcSs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void List_Drivers_IsNonEmptyAndKernelDriversPresent()
    {
        IReadOnlyList<ServiceInfo> drivers = _control.List(ServiceKind.Driver);
        Assert.NotEmpty(drivers);
        Assert.All(drivers, driver => Assert.Equal("driver", driver.Kind));
        Assert.Contains(drivers, driver => driver.ServiceType.Contains("KERNEL_DRIVER", StringComparison.Ordinal));
    }

    [Fact]
    public void Get_RpcSs_ReportsRunningServiceDetails()
    {
        ServiceInfo? info = _control.Get("RpcSs");
        Assert.NotNull(info);
        Assert.Equal("win32", info.Kind);
        Assert.Equal(ServiceStates.Running, info.State);
        Assert.Equal("auto", info.StartType);
        Assert.False(string.IsNullOrWhiteSpace(info.ImagePath));
        Assert.NotNull(info.Win32ExitCode);
    }

    [Fact]
    public void Get_UnknownService_ReturnsNull() =>
        Assert.Null(_control.Get("Vestigo-No-Such-Service-" + Guid.NewGuid().ToString("N")));
}
