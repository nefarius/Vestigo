using System.ComponentModel;
using System.Text.RegularExpressions;

using ModelContextProtocol.Server;

using Vestigo.Configuration;
using Vestigo.Devices;
using Vestigo.Security;

namespace Vestigo.Services;

/// <summary>
///     MCP tools for inspecting and controlling Windows services and kernel drivers. Mutating tools refuse
///     names in <c>Vestigo:Services:ProtectedNames</c>.
/// </summary>
[McpServerToolType]
public sealed partial class ServiceTools(
    IServiceControl control,
    INefconRunner nefcon,
    RootPolicy roots,
    VestigoOptions options)
{
    private const int MaxWaitSeconds = 300;
    private const int MaxListLimit = 1000;

    /// <summary>Interval between state polls while waiting. Tests shorten it.</summary>
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    [McpServerTool(Name = "list_services")]
    [Description("Lists Win32 services and kernel/file-system drivers with state, start type, image path, account, and dependencies, sorted by name.")]
    public OperationResult<ServiceList> ListServices(
        [Description("win32, driver or all.")] string kind = "all",
        [Description("Case-insensitive substring of the service name or display name.")] string? nameContains = null,
        [Description("Only services in this state: stopped, running, paused, start_pending, stop_pending, continue_pending or pause_pending.")] string? state = null,
        [Description("Maximum entries to return, 1 to 1000.")] int limit = 200)
    {
        if (!Enum.TryParse(kind, ignoreCase: true, out ServiceKind parsedKind) || !Enum.IsDefined(parsedKind))
        {
            return OperationResult<ServiceList>.Fail("invalid_kind", "kind must be win32, driver or all.");
        }

        string? stateFilter = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            stateFilter = state.Trim().ToLowerInvariant();
            if (!ServiceStates.All.Contains(stateFilter))
            {
                return OperationResult<ServiceList>.Fail("invalid_state", $"state must be one of {string.Join(", ", ServiceStates.All)}.");
            }
        }

        IReadOnlyList<ServiceInfo> all;
        try
        {
            all = control.List(parsedKind);
        }
        catch (ServiceControlException ex)
        {
            return OperationResult<ServiceList>.Fail(ex.Code, ex.Message);
        }

        List<ServiceInfo> matches = all
            .Where(service => stateFilter is null || service.State == stateFilter)
            .Where(service => string.IsNullOrWhiteSpace(nameContains)
                || service.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)
                || service.DisplayName.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int cap = Math.Clamp(limit, 1, MaxListLimit);
        List<ServiceInfo> page = matches.Take(cap).ToList();
        return OperationResult<ServiceList>.Ok(new ServiceList(matches.Count, page.Count, matches.Count > page.Count, page));
    }

    [McpServerTool(Name = "get_service")]
    [Description("Returns state, start type, binary path, type (including KERNEL_DRIVER), error control, dependencies, exit codes, and the process id while running.")]
    public OperationResult<ServiceInfo> GetService([Description("Service name (not the display name).")] string name)
    {
        OperationResult<ServiceInfo>? invalid = ValidateName<ServiceInfo>(name, mutating: false);
        if (invalid is not null)
        {
            return invalid;
        }

        return Run(() => control.Get(name) is { } info
            ? OperationResult<ServiceInfo>.Ok(info)
            : NotFound<ServiceInfo>(name));
    }

    [McpServerTool(Name = "start_service")]
    [Description("Starts a service or driver and waits until it is running. Starting an unsigned driver may fail unless test-signing is enabled.")]
    public async Task<OperationResult<ServiceInfo>> StartService(
        [Description("Service name.")] string name,
        [Description("Optional start arguments passed to the service.")] string[]? arguments = null,
        [Description("Seconds to wait for Running, 0 to 300. 0 returns without waiting.")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        OperationResult<ServiceInfo>? invalid = ValidateName<ServiceInfo>(name, mutating: true);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            ServiceInfo? current = control.Get(name);
            if (current is null)
            {
                return NotFound<ServiceInfo>(name);
            }

            if (current.State == ServiceStates.Running)
            {
                return OperationResult<ServiceInfo>.Ok(current);
            }

            control.Start(name, arguments ?? []);
            return await WaitFor(name, ServiceStates.Running, timeoutSeconds, cancellationToken);
        }
        catch (ServiceControlException ex)
        {
            return OperationResult<ServiceInfo>.Fail(ex.Code, ex.Message);
        }
    }

    [McpServerTool(Name = "stop_service")]
    [Description("Stops a service or driver and waits until it is stopped.")]
    public async Task<OperationResult<ServiceInfo>> StopService(
        [Description("Service name.")] string name,
        [Description("Also stop running dependent services first.")] bool stopDependents = false,
        [Description("Seconds to wait for Stopped, 0 to 300. 0 returns without waiting.")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        OperationResult<ServiceInfo>? invalid = ValidateName<ServiceInfo>(name, mutating: true);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            return await StopCore(name, stopDependents, timeoutSeconds, cancellationToken);
        }
        catch (ServiceControlException ex)
        {
            return OperationResult<ServiceInfo>.Fail(ex.Code, ex.Message);
        }
    }

    [McpServerTool(Name = "restart_service")]
    [Description("Stops a service (waiting for Stopped) and starts it again (waiting for Running). Dependents stopped by stopDependents are not restarted.")]
    public async Task<OperationResult<ServiceInfo>> RestartService(
        [Description("Service name.")] string name,
        [Description("Also stop running dependent services first.")] bool stopDependents = false,
        [Description("Seconds to wait for each phase, 1 to 300.")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        OperationResult<ServiceInfo>? invalid = ValidateName<ServiceInfo>(name, mutating: true);
        if (invalid is not null)
        {
            return invalid;
        }

        int timeout = Math.Clamp(timeoutSeconds, 1, MaxWaitSeconds);
        try
        {
            OperationResult<ServiceInfo> stopped = await StopCore(name, stopDependents, timeout, cancellationToken);
            if (!stopped.Success)
            {
                return stopped;
            }

            control.Start(name, []);
            return await WaitFor(name, ServiceStates.Running, timeout, cancellationToken);
        }
        catch (ServiceControlException ex)
        {
            return OperationResult<ServiceInfo>.Fail(ex.Code, ex.Message);
        }
    }

    [McpServerTool(Name = "set_service_start_type")]
    [Description("Sets the start type: boot, system, auto, demand or disabled. boot and system are only valid for drivers.")]
    public OperationResult<ServiceInfo> SetServiceStartType(
        [Description("Service name.")] string name,
        [Description("boot, system, auto, demand or disabled.")] string startType)
    {
        OperationResult<ServiceInfo>? invalid = ValidateName<ServiceInfo>(name, mutating: true);
        if (invalid is not null)
        {
            return invalid;
        }

        if (!Enum.TryParse(startType, ignoreCase: true, out ServiceStartType parsed) || !Enum.IsDefined(parsed))
        {
            return OperationResult<ServiceInfo>.Fail("invalid_start_type", "startType must be boot, system, auto, demand or disabled.");
        }

        return Run(() =>
        {
            ServiceInfo? current = control.Get(name);
            if (current is null)
            {
                return NotFound<ServiceInfo>(name);
            }

            if (parsed is ServiceStartType.Boot or ServiceStartType.System && current.Kind != "driver")
            {
                return OperationResult<ServiceInfo>.Fail(
                    "invalid_start_type", "boot and system start types are only valid for drivers.");
            }

            control.SetStartType(name, parsed);
            return control.Get(name) is { } updated ? OperationResult<ServiceInfo>.Ok(updated) : NotFound<ServiceInfo>(name);
        });
    }

    [McpServerTool(Name = "create_driver_service")]
    [Description("Creates a kernel driver service through nefcon. The .sys binary must be an existing file inside a configured root that allows Execute. The service starts on demand; use start_service afterwards.")]
    public async Task<OperationResult<ServiceChangeResult>> CreateDriverService(
        [Description("New service name.")] string serviceName,
        [Description("Driver .sys path: root:relative\\path or an absolute path inside a root that allows Execute.")] string binaryPath,
        [Description("Display name. Defaults to the service name.")] string? displayName = null,
        CancellationToken cancellationToken = default)
    {
        OperationResult<ServiceChangeResult>? invalid = ValidateName<ServiceChangeResult>(serviceName, mutating: true);
        if (invalid is not null)
        {
            return invalid;
        }

        PathCheck path = roots.ResolveExisting(binaryPath, RootAccess.Execute);
        if (!path.Success)
        {
            return OperationResult<ServiceChangeResult>.Fail(path.ErrorCode!, path.Error!);
        }

        if (!File.Exists(path.FullPath) || !string.Equals(Path.GetExtension(path.FullPath), ".sys", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<ServiceChangeResult>.Fail("unsupported_extension", "Driver binaries must have the .sys extension.");
        }

        string display = string.IsNullOrWhiteSpace(displayName) ? serviceName : displayName.Trim();
        if (display.StartsWith('-') || display.Length > 256 || display.Any(char.IsControl))
        {
            return OperationResult<ServiceChangeResult>.Fail("invalid_display_name", "displayName is invalid.");
        }

        try
        {
            if (control.Get(serviceName) is not null)
            {
                return OperationResult<ServiceChangeResult>.Fail("already_exists", $"Service '{serviceName}' already exists.");
            }

            NefconResult result = await nefcon.CreateDriverService(serviceName, display, path.FullPath!, cancellationToken);
            return MapNefcon(result, serviceName, "create");
        }
        catch (ServiceControlException ex)
        {
            return OperationResult<ServiceChangeResult>.Fail(ex.Code, ex.Message);
        }
    }

    [McpServerTool(Name = "delete_service")]
    [Description("Deletes a kernel driver service through nefcon. Win32 services are refused. Stop the driver first.")]
    public async Task<OperationResult<ServiceChangeResult>> DeleteService(
        [Description("Driver service name (not a path).")] string name,
        CancellationToken cancellationToken = default)
    {
        OperationResult<ServiceChangeResult>? invalid = ValidateName<ServiceChangeResult>(name, mutating: true);
        if (invalid is not null)
        {
            return invalid;
        }

        try
        {
            ServiceInfo? current = control.Get(name);
            if (current is null)
            {
                return NotFound<ServiceChangeResult>(name);
            }

            if (!current.ServiceType.Contains("KERNEL_DRIVER", StringComparison.Ordinal))
            {
                return OperationResult<ServiceChangeResult>.Fail(
                    "not_a_driver", $"Service '{name}' is not a kernel driver. Only KERNEL_DRIVER services can be deleted.");
            }

            NefconResult result = await nefcon.RemoveDriverService(name, cancellationToken);
            return MapNefcon(result, name, "delete");
        }
        catch (ServiceControlException ex)
        {
            return OperationResult<ServiceChangeResult>.Fail(ex.Code, ex.Message);
        }
    }

    private async Task<OperationResult<ServiceInfo>> StopCore(
        string name,
        bool stopDependents,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        ServiceInfo? current = control.Get(name);
        if (current is null)
        {
            return NotFound<ServiceInfo>(name);
        }

        if (current.State == ServiceStates.Stopped)
        {
            return OperationResult<ServiceInfo>.Ok(current);
        }

        if (stopDependents)
        {
            IReadOnlyList<string> dependents = control.ActiveDependents(name);
            string? protectedDependent = dependents.FirstOrDefault(IsProtected);
            if (protectedDependent is not null)
            {
                return OperationResult<ServiceInfo>.Fail(
                    "protected_service", $"Dependent service '{protectedDependent}' is protected and cannot be stopped.");
            }

            foreach (string dependent in dependents)
            {
                try
                {
                    control.Stop(dependent);
                }
                catch (ServiceControlException ex) when (ex.Code == "not_running")
                {
                    continue;
                }

                OperationResult<ServiceInfo> waited = await WaitFor(dependent, ServiceStates.Stopped, Math.Max(timeoutSeconds, 1), cancellationToken);
                if (!waited.Success)
                {
                    return waited;
                }
            }
        }

        control.Stop(name);
        return await WaitFor(name, ServiceStates.Stopped, timeoutSeconds, cancellationToken);
    }

    private async Task<OperationResult<ServiceInfo>> WaitFor(
        string name,
        string target,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 0, MaxWaitSeconds));
        DateTime deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ServiceInfo? info = control.Get(name);
            if (info is null)
            {
                return NotFound<ServiceInfo>(name);
            }

            if (info.State == target)
            {
                return OperationResult<ServiceInfo>.Ok(info);
            }

            if (timeout == TimeSpan.Zero)
            {
                return OperationResult<ServiceInfo>.Ok(info);
            }

            if (DateTime.UtcNow >= deadline)
            {
                return OperationResult<ServiceInfo>.Fail(
                    "timeout",
                    $"Service '{name}' did not reach '{target}' within {timeout.TotalSeconds:0} s (last state '{info.State}', win32 exit code {info.Win32ExitCode}).");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static OperationResult<ServiceChangeResult> MapNefcon(NefconResult result, string name, string verb)
    {
        if (result.TimedOut)
        {
            return OperationResult<ServiceChangeResult>.Fail("nefcon_timeout", $"nefcon timed out while trying to {verb} '{name}'.");
        }

        if (!result.Succeeded)
        {
            return OperationResult<ServiceChangeResult>.Fail(
                "nefcon_failed", $"nefcon failed to {verb} '{name}' with exit code {result.ExitCode}: {result.Output}");
        }

        return OperationResult<ServiceChangeResult>.Ok(new ServiceChangeResult(name, result.RebootRequired, result.Output, null));
    }

    private bool IsProtected(string name) => options.Services.IsProtected(name);

    private OperationResult<T>? ValidateName<T>(string name, bool mutating)
    {
        if (string.IsNullOrWhiteSpace(name) || !ValidName().IsMatch(name))
        {
            return OperationResult<T>.Fail("invalid_name", "Service name is required and may only contain letters, digits, and _ . $ # @ space -.");
        }

        if (mutating && IsProtected(name))
        {
            return OperationResult<T>.Fail("protected_service", $"Service '{name}' is protected by Vestigo:Services:ProtectedNames.");
        }

        return null;
    }

    private static OperationResult<T> NotFound<T>(string name) =>
        OperationResult<T>.Fail("not_found", $"Service '{name}' does not exist.");

    private static OperationResult<T> Run<T>(Func<OperationResult<T>> action)
    {
        try
        {
            return action();
        }
        catch (ServiceControlException ex)
        {
            return OperationResult<T>.Fail(ex.Code, ex.Message);
        }
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_](?:[A-Za-z0-9_.$#@ -]{0,254}[A-Za-z0-9_.$#@-])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();
}
