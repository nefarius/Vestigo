namespace Vestigo.Devices;

/// <summary>Which class filter list a filter service is added to or removed from.</summary>
public enum ClassFilterPosition
{
    Upper,
    Lower
}

/// <summary>
///     Typed wrapper over the bundled nefcon CLI. Every method builds a fixed argument list; callers never
///     supply free-form arguments. All operations require the elevated service account and run one at a time.
///     Exit code 0 and 3010 (reboot required) both count as success. Callers confine file paths to
///     configured roots before calling.
/// </summary>
public interface INefconRunner
{
    /// <summary>
    ///     Without <paramref name="hardwareId" />: <c>--install-driver --inf-path</c>. With it: devcon-style
    ///     <c>install INF HWID</c>, which creates the ROOT device node and updates its driver. The duplicate
    ///     flags are only honoured by the devcon-style form (<paramref name="removeDuplicates" /> requires
    ///     <paramref name="noDuplicates" />).
    /// </summary>
    Task<NefconResult> InstallDriver(
        string infPath,
        string? hardwareId = null,
        bool noDuplicates = false,
        bool removeDuplicates = false,
        CancellationToken cancellationToken = default);

    Task<NefconResult> UninstallDriver(string infPath, CancellationToken cancellationToken = default);

    /// <summary>
    ///     <c>--create-device-node</c>. nefcon ignores <c>--remove-duplicates</c> for this switch, so only
    ///     <paramref name="noDuplicates" /> is offered.
    /// </summary>
    Task<NefconResult> CreateDeviceNode(
        string className,
        Guid classGuid,
        string hardwareId,
        bool noDuplicates = false,
        CancellationToken cancellationToken = default);

    Task<NefconResult> RemoveDeviceNode(string hardwareId, Guid classGuid, CancellationToken cancellationToken = default);

    Task<NefconResult> InstallFilterDriver(string infPath, CancellationToken cancellationToken = default);

    /// <summary>Removes the class filter and driver service; <paramref name="infPath" /> additionally purges the driver store package.</summary>
    Task<NefconResult> UninstallFilterDriver(
        Guid classGuid,
        string serviceName,
        ClassFilterPosition position,
        string? infPath = null,
        bool allVersions = false,
        CancellationToken cancellationToken = default);

    Task<NefconResult> AddClassFilter(
        Guid classGuid,
        string serviceName,
        ClassFilterPosition position,
        CancellationToken cancellationToken = default);

    Task<NefconResult> RemoveClassFilter(
        Guid classGuid,
        string serviceName,
        ClassFilterPosition position,
        CancellationToken cancellationToken = default);

    Task<NefconResult> InfDefaultInstall(string infPath, CancellationToken cancellationToken = default);

    Task<NefconResult> InfDefaultUninstall(string infPath, CancellationToken cancellationToken = default);

    Task<NefconResult> CreateDriverService(
        string serviceName,
        string displayName,
        string binPath,
        CancellationToken cancellationToken = default);

    Task<NefconResult> RemoveDriverService(string serviceName, CancellationToken cancellationToken = default);

    /// <summary>
    ///     <c>--remove-driver-store-package</c>: <paramref name="infName" /> (original INF base name) is
    ///     required by nefcon; the GUID and service name only narrow the match.
    /// </summary>
    Task<NefconResult> RemoveDriverStorePackage(
        string infName,
        Guid? classGuid = null,
        string? serviceName = null,
        CancellationToken cancellationToken = default);

    Task<NefconResult> DeleteFileOnReboot(string filePath, CancellationToken cancellationToken = default);
}