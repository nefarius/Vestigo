using System.ComponentModel;

using ModelContextProtocol.Server;

using Vestigo.Abstractions;
using Vestigo.Configuration;
using Vestigo.Devices;
using Vestigo.Security;

namespace Vestigo.Tools;

/// <summary>Outcome of a nefcon-backed driver or device action.</summary>
/// <param name="ExitCode">nefcon exit code; -1 when it timed out.</param>
/// <param name="OutputTail">Tail of nefcon's combined output.</param>
/// <param name="RebootRequired">True for exit code 3010: the change worked but needs a restart.</param>
/// <param name="TimedOut">True when nefcon was killed after the configured timeout.</param>
public sealed record DriverActionResult(int ExitCode, string OutputTail, bool RebootRequired, bool TimedOut);

/// <summary>Outcome of a restart, enable or disable.</summary>
public sealed record DeviceStateResult(string InstanceId, string Action);

/// <summary>Devices exposing a device interface.</summary>
public sealed record InterfaceSearchResult(IReadOnlyList<DeviceInterfaceMatch> Devices, bool Truncated);

/// <summary>Instance IDs matching a hardware ID.</summary>
public sealed record HardwareIdSearchResult(IReadOnlyList<string> InstanceIds);

/// <summary>
///     MCP tools for driver installation and device state. Drivers run in the kernel and every tool here needs
///     the elevated service account; that is by design.
/// </summary>
[McpServerToolType]
public sealed class DeviceTools(INefconRunner nefcon, IDeviceControl devices, RootPolicy roots)
{
    internal const int MaxInterfaceResults = 256;
    internal const int MaxInstanceIdLength = 400;

    // ---- nefcon-backed driver actions ----

    [McpServerTool(Name = "install_driver")]
    [Description("Installs a driver from an INF inside a readable root. With hardwareId it creates the ROOT device node and installs the driver on it (devcon install); without it the package is only staged and installed. The result has exitCode, outputTail and rebootRequired.")]
    public Task<OperationResult<DriverActionResult>> InstallDriver(
        [Description("INF path inside an allowed root (name:relative\\path or absolute).")] string infPath,
        [Description("Optional hardware ID, for example Root\\MyDriver. Enables the duplicate options.")] string? hardwareId = null,
        [Description("Skip creating the node when a device with this hardware ID already exists. Needs hardwareId.")] bool noDuplicates = false,
        [Description("Remove duplicate device nodes. Needs hardwareId and noDuplicates.")] bool removeDuplicates = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveInf(infPath, out string inf, out OperationResult<DriverActionResult>? failure))
        {
            return Task.FromResult(failure!);
        }

        if (hardwareId is null && (noDuplicates || removeDuplicates))
        {
            return Invalid("noDuplicates and removeDuplicates need hardwareId.");
        }

        if (removeDuplicates && !noDuplicates)
        {
            return Invalid("removeDuplicates requires noDuplicates.");
        }

        if (hardwareId is not null && !IsValidText(hardwareId))
        {
            return Invalid("hardwareId is empty, starts with '-', or contains control characters.");
        }

        return RunNefcon(() => nefcon.InstallDriver(inf, hardwareId, noDuplicates, removeDuplicates, cancellationToken));
    }

    [McpServerTool(Name = "uninstall_driver")]
    [Description("Uninstalls the driver described by an INF inside a readable root (nefcon --uninstall-driver).")]
    public Task<OperationResult<DriverActionResult>> UninstallDriver(
        [Description("INF path inside an allowed root.")] string infPath,
        CancellationToken cancellationToken = default) =>
        TryResolveInf(infPath, out string inf, out OperationResult<DriverActionResult>? failure)
            ? RunNefcon(() => nefcon.UninstallDriver(inf, cancellationToken))
            : Task.FromResult(failure!);

    [McpServerTool(Name = "create_device_node")]
    [Description("Creates a ROOT-enumerated device node with a hardware ID and device class. Install a driver on it afterwards with install_driver.")]
    public Task<OperationResult<DriverActionResult>> CreateDeviceNode(
        [Description("Device class name, for example System.")] string className,
        [Description("Device class GUID.")] string classGuid,
        [Description("Hardware ID of the new node, for example Root\\MyDriver.")] string hardwareId,
        [Description("Do nothing when a node with this hardware ID already exists.")] bool noDuplicates = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseGuid(classGuid, nameof(classGuid), out Guid guid, out OperationResult<DriverActionResult>? failure))
        {
            return Task.FromResult(failure!);
        }

        if (!IsValidText(className) || !IsValidText(hardwareId))
        {
            return Invalid("className and hardwareId must be non-empty, must not start with '-', and must not contain control characters.");
        }

        return RunNefcon(() => nefcon.CreateDeviceNode(className, guid, hardwareId, noDuplicates, cancellationToken));
    }

    [McpServerTool(Name = "remove_device_node")]
    [Description("Removes a device node by hardware ID and class GUID (nefcon --remove-device-node).")]
    public Task<OperationResult<DriverActionResult>> RemoveDeviceNode(
        [Description("Hardware ID of the node.")] string hardwareId,
        [Description("Device class GUID.")] string classGuid,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseGuid(classGuid, nameof(classGuid), out Guid guid, out OperationResult<DriverActionResult>? failure))
        {
            return Task.FromResult(failure!);
        }

        return IsValidText(hardwareId)
            ? RunNefcon(() => nefcon.RemoveDeviceNode(hardwareId, guid, cancellationToken))
            : Invalid("hardwareId is empty, starts with '-', or contains control characters.");
    }

    [McpServerTool(Name = "install_filter_driver")]
    [Description("Installs a filter driver from an INF inside a readable root and registers it as a class filter (nefcon --install-filter-driver).")]
    public Task<OperationResult<DriverActionResult>> InstallFilterDriver(
        [Description("INF path inside an allowed root.")] string infPath,
        CancellationToken cancellationToken = default) =>
        TryResolveInf(infPath, out string inf, out OperationResult<DriverActionResult>? failure)
            ? RunNefcon(() => nefcon.InstallFilterDriver(inf, cancellationToken))
            : Task.FromResult(failure!);

    [McpServerTool(Name = "uninstall_filter_driver")]
    [Description("Removes a class filter and its driver service. With infPath it also purges that driver store package; allVersions then purges every version of it.")]
    public Task<OperationResult<DriverActionResult>> UninstallFilterDriver(
        [Description("Device class GUID the filter is registered in.")] string classGuid,
        [Description("Filter driver service name.")] string serviceName,
        [Description("upper or lower.")] string position,
        [Description("Optional INF path inside an allowed root whose driver store package is also removed.")] string? infPath = null,
        [Description("Remove all versions of the driver store package. Needs infPath.")] bool allVersions = false,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseFilter(classGuid, serviceName, position, out Guid guid, out ClassFilterPosition where, out OperationResult<DriverActionResult>? failure))
        {
            return Task.FromResult(failure!);
        }

        string? inf = null;
        if (infPath is not null && !TryResolveInf(infPath, out inf, out failure))
        {
            return Task.FromResult(failure!);
        }

        return infPath is null && allVersions
            ? Invalid("allVersions needs infPath.")
            : RunNefcon(() => nefcon.UninstallFilterDriver(guid, serviceName, where, inf, allVersions, cancellationToken));
    }

    [McpServerTool(Name = "add_class_filter")]
    [Description("Adds an existing driver service to a device class's upper or lower filter list.")]
    public Task<OperationResult<DriverActionResult>> AddClassFilter(
        [Description("Device class GUID.")] string classGuid,
        [Description("Filter driver service name.")] string serviceName,
        [Description("upper or lower.")] string position,
        CancellationToken cancellationToken = default) =>
        TryParseFilter(classGuid, serviceName, position, out Guid guid, out ClassFilterPosition where, out OperationResult<DriverActionResult>? failure)
            ? RunNefcon(() => nefcon.AddClassFilter(guid, serviceName, where, cancellationToken))
            : Task.FromResult(failure!);

    [McpServerTool(Name = "remove_class_filter")]
    [Description("Removes a driver service from a device class's upper or lower filter list.")]
    public Task<OperationResult<DriverActionResult>> RemoveClassFilter(
        [Description("Device class GUID.")] string classGuid,
        [Description("Filter driver service name.")] string serviceName,
        [Description("upper or lower.")] string position,
        CancellationToken cancellationToken = default) =>
        TryParseFilter(classGuid, serviceName, position, out Guid guid, out ClassFilterPosition where, out OperationResult<DriverActionResult>? failure)
            ? RunNefcon(() => nefcon.RemoveClassFilter(guid, serviceName, where, cancellationToken))
            : Task.FromResult(failure!);

    [McpServerTool(Name = "inf_default_install")]
    [Description("Runs the [DefaultInstall] section of an INF inside a readable root (nefcon --inf-default-install).")]
    public Task<OperationResult<DriverActionResult>> InfDefaultInstall(
        [Description("INF path inside an allowed root.")] string infPath,
        CancellationToken cancellationToken = default) =>
        TryResolveInf(infPath, out string inf, out OperationResult<DriverActionResult>? failure)
            ? RunNefcon(() => nefcon.InfDefaultInstall(inf, cancellationToken))
            : Task.FromResult(failure!);

    [McpServerTool(Name = "inf_default_uninstall")]
    [Description("Runs the [DefaultUninstall] section of an INF inside a readable root (nefcon --inf-default-uninstall).")]
    public Task<OperationResult<DriverActionResult>> InfDefaultUninstall(
        [Description("INF path inside an allowed root.")] string infPath,
        CancellationToken cancellationToken = default) =>
        TryResolveInf(infPath, out string inf, out OperationResult<DriverActionResult>? failure)
            ? RunNefcon(() => nefcon.InfDefaultUninstall(inf, cancellationToken))
            : Task.FromResult(failure!);

    [McpServerTool(Name = "remove_driver_store_package")]
    [Description("Removes driver store packages by original INF file name (for example mydriver.inf). Optional class GUID and service name narrow the match. nefcon offers no all-versions switch here.")]
    public Task<OperationResult<DriverActionResult>> RemoveDriverStorePackage(
        [Description("Original INF base name, no directory, for example mydriver.inf.")] string infName,
        [Description("Optional device class GUID to narrow the match.")] string? classGuid = null,
        [Description("Optional driver service name to narrow the match.")] string? serviceName = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidText(infName) || infName.Contains('\\') || infName.Contains('/') || infName.Contains(',') ||
            !infName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("infName must be a single INF file name ending in .inf, without a directory.");
        }

        Guid? guid = null;
        if (classGuid is not null)
        {
            if (!TryParseGuid(classGuid, nameof(classGuid), out Guid parsed, out OperationResult<DriverActionResult>? failure))
            {
                return Task.FromResult(failure!);
            }

            guid = parsed;
        }

        if (serviceName is not null && !IsValidText(serviceName))
        {
            return Invalid("serviceName is empty, starts with '-', or contains control characters.");
        }

        return RunNefcon(() => nefcon.RemoveDriverStorePackage(infName, guid, serviceName, cancellationToken));
    }

    // ---- Nefarius.Utilities.DeviceManagement-backed state changes and queries ----

    [McpServerTool(Name = "restart_device")]
    [Description("Restarts a device (remove and re-enumerate) by instance ID, for example after installing a filter driver.")]
    public OperationResult<DeviceStateResult> RestartDevice([Description("Device instance ID, for example ROOT\\SYSTEM\\0001.")] string instanceId) =>
        ChangeState(instanceId, "restart", devices.Restart);

    [McpServerTool(Name = "enable_device")]
    [Description("Enables a device by instance ID.")]
    public OperationResult<DeviceStateResult> EnableDevice([Description("Device instance ID.")] string instanceId) =>
        ChangeState(instanceId, "enable", devices.Enable);

    [McpServerTool(Name = "disable_device")]
    [Description("Disables a device by instance ID.")]
    public OperationResult<DeviceStateResult> DisableDevice([Description("Device instance ID.")] string instanceId) =>
        ChangeState(instanceId, "disable", devices.Disable);

    [McpServerTool(Name = "find_devices_by_interface")]
    [Description("Finds devices that expose a device interface GUID and returns their instance IDs and device paths.")]
    public OperationResult<InterfaceSearchResult> FindDevicesByInterface(
        [Description("Device interface GUID.")] string interfaceGuid,
        [Description("Only currently present devices. False also returns phantom devices.")] bool presentOnly = true)
    {
        if (!TryParseGuid(interfaceGuid, nameof(interfaceGuid), out Guid guid, out OperationResult<InterfaceSearchResult>? failure))
        {
            return failure!;
        }

        try
        {
            IReadOnlyList<DeviceInterfaceMatch> found = devices.FindByInterface(guid, presentOnly, MaxInterfaceResults);
            return OperationResult<InterfaceSearchResult>.Ok(new InterfaceSearchResult(found, found.Count >= MaxInterfaceResults));
        }
        catch (DeviceControlException ex)
        {
            return OperationResult<InterfaceSearchResult>.Fail(ex.Code, ex.Message);
        }
    }

    [McpServerTool(Name = "find_devices_by_hardware_id")]
    [Description("Finds devices in a device class whose hardware IDs match, and returns their instance IDs.")]
    public OperationResult<HardwareIdSearchResult> FindDevicesByHardwareId(
        [Description("Device class GUID to search in.")] string classGuid,
        [Description("Hardware ID to match.")] string hardwareId,
        [Description("Only currently present devices.")] bool presentOnly = true,
        [Description("Match substrings instead of the exact ID.")] bool allowPartial = false)
    {
        if (!TryParseGuid(classGuid, nameof(classGuid), out Guid guid, out OperationResult<HardwareIdSearchResult>? failure))
        {
            return failure!;
        }

        if (!IsValidText(hardwareId))
        {
            return OperationResult<HardwareIdSearchResult>.Fail("invalid_request", "hardwareId is empty, starts with '-', or contains control characters.");
        }

        try
        {
            return OperationResult<HardwareIdSearchResult>.Ok(new HardwareIdSearchResult(devices.FindByHardwareId(guid, hardwareId, presentOnly, allowPartial)));
        }
        catch (DeviceControlException ex)
        {
            return OperationResult<HardwareIdSearchResult>.Fail(ex.Code, ex.Message);
        }
    }

    // ---- helpers ----

    private OperationResult<DeviceStateResult> ChangeState(string instanceId, string action, Action<string> operation)
    {
        if (!IsValidInstanceId(instanceId))
        {
            return OperationResult<DeviceStateResult>.Fail(
                "invalid_instance_id",
                $"Instance ID must be 1-{MaxInstanceIdLength} characters, contain a '\\', and have no control characters or leading '-'.");
        }

        try
        {
            operation(instanceId);
            return OperationResult<DeviceStateResult>.Ok(new DeviceStateResult(instanceId, action));
        }
        catch (DeviceControlException ex)
        {
            return OperationResult<DeviceStateResult>.Fail(ex.Code, ex.Message);
        }
    }

    private static async Task<OperationResult<DriverActionResult>> RunNefcon(Func<Task<NefconResult>> run)
    {
        try
        {
            return Map(await run().ConfigureAwait(false));
        }
        catch (FileNotFoundException ex)
        {
            return OperationResult<DriverActionResult>.Fail("nefcon_not_found", ex.Message);
        }
        catch (ArgumentException ex)
        {
            return OperationResult<DriverActionResult>.Fail("invalid_request", $"{ex.ParamName}: {ex.Message}");
        }
    }

    /// <summary>Exit codes 0 and 3010 are success; anything else keeps the full result next to the error.</summary>
    internal static OperationResult<DriverActionResult> Map(NefconResult result)
    {
        DriverActionResult value = new(result.ExitCode, result.Output, result.RebootRequired, result.TimedOut);
        if (result.Succeeded)
        {
            return OperationResult<DriverActionResult>.Ok(value);
        }

        return result.TimedOut
            ? new OperationResult<DriverActionResult>(false, value, "nefcon_timeout", "nefcon did not finish in time and was terminated.")
            : new OperationResult<DriverActionResult>(false, value, "nefcon_failed", $"nefcon exited with code {result.ExitCode}. Exit code 1 usually means the service is not elevated or an argument was rejected.");
    }

    private static Task<OperationResult<DriverActionResult>> Invalid(string message) =>
        Task.FromResult(OperationResult<DriverActionResult>.Fail("invalid_request", message));

    private bool TryResolveInf(string infPath, out string inf, out OperationResult<DriverActionResult>? failure)
    {
        inf = "";
        failure = null;
        PathCheck check = roots.ResolveExisting(infPath ?? "", RootAccess.Read);
        if (!check.Success)
        {
            failure = OperationResult<DriverActionResult>.Fail(check.ErrorCode!, check.Error!);
            return false;
        }

        if (!File.Exists(check.FullPath) || !string.Equals(Path.GetExtension(check.FullPath), ".inf", StringComparison.OrdinalIgnoreCase))
        {
            failure = OperationResult<DriverActionResult>.Fail("invalid_path", "infPath must be an existing .inf file.");
            return false;
        }

        inf = check.FullPath!;
        return true;
    }

    private static bool TryParseFilter(
        string classGuid,
        string serviceName,
        string position,
        out Guid guid,
        out ClassFilterPosition where,
        out OperationResult<DriverActionResult>? failure)
    {
        where = ClassFilterPosition.Upper;
        failure = null;
        if (!TryParseGuid(classGuid, nameof(classGuid), out guid, out failure))
        {
            return false;
        }

        if (!IsValidText(serviceName))
        {
            failure = OperationResult<DriverActionResult>.Fail("invalid_request", "serviceName is empty, starts with '-', or contains control characters.");
            return false;
        }

        if (string.Equals(position, "upper", StringComparison.OrdinalIgnoreCase))
        {
            where = ClassFilterPosition.Upper;
            return true;
        }

        if (string.Equals(position, "lower", StringComparison.OrdinalIgnoreCase))
        {
            where = ClassFilterPosition.Lower;
            return true;
        }

        failure = OperationResult<DriverActionResult>.Fail("invalid_request", "position must be 'upper' or 'lower'.");
        return false;
    }

    internal static bool TryParseGuid<T>(string? value, string name, out Guid guid, out OperationResult<T>? failure)
    {
        failure = null;
        if (Guid.TryParse(value, out guid) && guid != Guid.Empty)
        {
            return true;
        }

        failure = OperationResult<T>.Fail("invalid_guid", $"{name} is not a valid non-empty GUID.");
        return false;
    }

    internal static bool IsValidText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !value.StartsWith('-') && !value.Any(char.IsControl);

    internal static bool IsValidInstanceId(string? value) =>
        IsValidText(value) && value!.Length <= MaxInstanceIdLength && value.Contains('\\');
}
