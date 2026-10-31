using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;

using Microsoft.Win32;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Services;

namespace Vestigo.Services;

/// <summary>
///     <see cref="IServiceControl" /> over <see cref="ServiceController" />, the service registry keys,
///     and CsWin32 for status details and configuration changes.
/// </summary>
internal sealed class WindowsServiceControl : IServiceControl
{
    private const string RegistryRoot = @"SYSTEM\CurrentControlSet\Services\";

    private const int ErrorAccessDenied = 5;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorDependentServicesRunning = 1051;
    private const int ErrorServiceDisabled = 1058;
    private const int ErrorInvalidImageHash = 577;
    private const int ErrorDriverBlocked = 1275;

    public IReadOnlyList<ServiceInfo> List(ServiceKind kind)
    {
        List<ServiceInfo> result = [];
        try
        {
            if (kind is ServiceKind.All or ServiceKind.Win32)
            {
                foreach (ServiceController controller in ServiceController.GetServices())
                {
                    using (controller)
                    {
                        result.Add(Describe(controller, detailed: false));
                    }
                }
            }

            if (kind is ServiceKind.All or ServiceKind.Driver)
            {
                foreach (ServiceController controller in ServiceController.GetDevices())
                {
                    using (controller)
                    {
                        result.Add(Describe(controller, detailed: false));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw Map(ex, "*");
        }

        return result;
    }

    public ServiceInfo? Get(string name)
    {
        using ServiceController controller = new(name);
        try
        {
            _ = controller.Status;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception && NativeCode(ex) == ErrorServiceDoesNotExist)
        {
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw Map(ex, name);
        }

        try
        {
            return Describe(controller, detailed: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception && NativeCode(ex) == ErrorServiceDoesNotExist)
        {
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw Map(ex, name);
        }
    }

    public void Start(string name, IReadOnlyList<string> arguments)
    {
        using ServiceController controller = new(name);
        try
        {
            controller.Start(arguments.ToArray());
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw Map(ex, name);
        }
    }

    public void Stop(string name)
    {
        using ServiceController controller = new(name);
        try
        {
            controller.Stop();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw Map(ex, name);
        }
    }

    public IReadOnlyList<string> ActiveDependents(string name)
    {
        List<string> ordered = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase) { name };
        try
        {
            using ServiceController root = new(name);
            Collect(root);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            throw Map(ex, name);
        }

        return ordered;

        void Collect(ServiceController controller)
        {
            foreach (ServiceController dependent in controller.DependentServices)
            {
                using (dependent)
                {
                    if (!seen.Add(dependent.ServiceName))
                    {
                        continue;
                    }

                    Collect(dependent);
                    if (dependent.Status != ServiceControllerStatus.Stopped)
                    {
                        ordered.Add(dependent.ServiceName);
                    }
                }
            }
        }
    }

    public unsafe void SetStartType(string name, ServiceStartType startType)
    {
        using CloseServiceHandleSafeHandle manager = PInvoke.OpenSCManager(null, null, PInvoke.SC_MANAGER_CONNECT);
        if (manager.IsInvalid)
        {
            throw Map(new Win32Exception(Marshal.GetLastWin32Error()), name);
        }

        using CloseServiceHandleSafeHandle service = PInvoke.OpenService(manager, name, PInvoke.SERVICE_CHANGE_CONFIG);
        if (service.IsInvalid)
        {
            throw Map(new Win32Exception(Marshal.GetLastWin32Error()), name);
        }

        // lpdwTagId must be NULL when lpLoadOrderGroup is NULL, so use the pointer overload.
        bool changed = PInvoke.ChangeServiceConfig(
            (SC_HANDLE)service.DangerousGetHandle(),
            ENUM_SERVICE_TYPE.SERVICE_NO_CHANGE,
            (SERVICE_START_TYPE)startType,
            (SERVICE_ERROR)PInvoke.SERVICE_NO_CHANGE,
            null,
            null,
            (uint*)null,
            null,
            null,
            null,
            null);
        if (!changed)
        {
            throw Map(new Win32Exception(Marshal.GetLastWin32Error()), name);
        }
    }

    private static ServiceInfo Describe(ServiceController controller, bool detailed)
    {
        string name = controller.ServiceName;
        ServiceType type = controller.ServiceType;
        bool driver = (type & (ServiceType.KernelDriver | ServiceType.FileSystemDriver | ServiceType.Adapter | ServiceType.RecognizerDriver)) != 0;

        string? startType = null;
        string? imagePath = null;
        string? account = null;
        string? errorControl = null;
        List<string> dependencies = [];
        using (RegistryKey? key = OpenServiceKey(name))
        {
            if (key is not null)
            {
                if (key.GetValue("Start") is int start && Enum.IsDefined(typeof(ServiceStartType), start))
                {
                    startType = ((ServiceStartType)start).ToString().ToLowerInvariant();
                }

                imagePath = key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                account = key.GetValue("ObjectName") as string;
                errorControl = key.GetValue("ErrorControl") switch
                {
                    0 => "ignore",
                    1 => "normal",
                    2 => "severe",
                    3 => "critical",
                    _ => null
                };
                if (key.GetValue("DependOnService") is string[] depends)
                {
                    dependencies.AddRange(depends.Where(entry => !string.IsNullOrWhiteSpace(entry)));
                }
            }
        }

        ServiceInfo info = new(
            name,
            controller.DisplayName,
            driver ? "driver" : "win32",
            TypeNames(type),
            StateName(controller.Status),
            startType,
            imagePath,
            account,
            dependencies,
            errorControl);
        return detailed ? WithStatusDetails(info) : info;
    }

    /// <summary>Opens the service registry key read-only; null when missing or not readable by this account.</summary>
    private static RegistryKey? OpenServiceKey(string name)
    {
        try
        {
            return Registry.LocalMachine.OpenSubKey(RegistryRoot + name);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static unsafe ServiceInfo WithStatusDetails(ServiceInfo info)
    {
        using CloseServiceHandleSafeHandle manager = PInvoke.OpenSCManager(null, null, PInvoke.SC_MANAGER_CONNECT);
        if (manager.IsInvalid)
        {
            return info;
        }

        using CloseServiceHandleSafeHandle service = PInvoke.OpenService(manager, info.Name, PInvoke.SERVICE_QUERY_STATUS);
        if (service.IsInvalid)
        {
            return info;
        }

        SERVICE_STATUS_PROCESS status = default;
        Span<byte> buffer = new(&status, sizeof(SERVICE_STATUS_PROCESS));
        if (!PInvoke.QueryServiceStatusEx(service, SC_STATUS_TYPE.SC_STATUS_PROCESS_INFO, buffer, out _))
        {
            return info;
        }

        return info with
        {
            ProcessId = status.dwProcessId == 0 ? null : status.dwProcessId,
            Win32ExitCode = status.dwWin32ExitCode,
            ServiceExitCode = status.dwServiceSpecificExitCode
        };
    }

    private static string TypeNames(ServiceType type)
    {
        List<string> names = [];
        if (type.HasFlag(ServiceType.KernelDriver)) names.Add("KERNEL_DRIVER");
        if (type.HasFlag(ServiceType.FileSystemDriver)) names.Add("FILE_SYSTEM_DRIVER");
        if (type.HasFlag(ServiceType.Adapter)) names.Add("ADAPTER");
        if (type.HasFlag(ServiceType.RecognizerDriver)) names.Add("RECOGNIZER_DRIVER");
        if (type.HasFlag(ServiceType.Win32OwnProcess)) names.Add("WIN32_OWN_PROCESS");
        if (type.HasFlag(ServiceType.Win32ShareProcess)) names.Add("WIN32_SHARE_PROCESS");
        if (type.HasFlag(ServiceType.InteractiveProcess)) names.Add("INTERACTIVE_PROCESS");
        return names.Count == 0 ? "UNKNOWN" : string.Join('|', names);
    }

    private static string StateName(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Stopped => ServiceStates.Stopped,
        ServiceControllerStatus.StartPending => ServiceStates.StartPending,
        ServiceControllerStatus.StopPending => ServiceStates.StopPending,
        ServiceControllerStatus.Running => ServiceStates.Running,
        ServiceControllerStatus.ContinuePending => ServiceStates.ContinuePending,
        ServiceControllerStatus.PausePending => ServiceStates.PausePending,
        ServiceControllerStatus.Paused => ServiceStates.Paused,
        _ => status.ToString().ToLowerInvariant()
    };

    private static int NativeCode(Exception exception) =>
        exception is Win32Exception win32
            ? win32.NativeErrorCode
            : exception.InnerException is Win32Exception inner ? inner.NativeErrorCode : 0;

    private static ServiceControlException Map(Exception exception, string name)
    {
        int code = NativeCode(exception);
        return code switch
        {
            ErrorAccessDenied => new ServiceControlException(
                "access_denied", $"Access denied for service '{name}'. The Vestigo service account must be elevated.", exception),
            ErrorServiceDoesNotExist => new ServiceControlException("not_found", $"Service '{name}' does not exist.", exception),
            ErrorServiceAlreadyRunning => new ServiceControlException("already_running", $"Service '{name}' is already running.", exception),
            ErrorServiceNotActive => new ServiceControlException("not_running", $"Service '{name}' is not running.", exception),
            ErrorDependentServicesRunning => new ServiceControlException(
                "has_dependents", $"Service '{name}' has running dependent services. Retry with stopDependents.", exception),
            ErrorServiceDisabled => new ServiceControlException("disabled", $"Service '{name}' is disabled.", exception),
            ErrorInvalidImageHash => new ServiceControlException(
                "driver_signature",
                $"Windows rejected the binary of '{name}' (error 577, invalid signature). Unsigned drivers need test-signing mode or a valid signature.",
                exception),
            ErrorDriverBlocked => new ServiceControlException(
                "driver_blocked", $"Windows blocked the driver '{name}' from loading (error 1275).", exception),
            _ => new ServiceControlException(
                "service_error", $"Service control failed for '{name}': {exception.Message}", exception)
        };
    }
}
