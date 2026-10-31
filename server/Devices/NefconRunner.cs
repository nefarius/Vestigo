using System.Text.RegularExpressions;

using Vestigo.Configuration;

namespace Vestigo.Devices;

/// <summary>
///     Default <see cref="INefconRunner" />.
/// </summary>
/// <remarks>
///     Facts taken from nefcon v1.21.0 (src/NefConUtil.cpp):
///     every command except --find-hwid and the bluetooth switches calls IsAdmin first and returns
///     EXIT_FAILURE (1) when not elevated; missing or invalid arguments, including an unparsable class GUID
///     and a position other than upper|lower, also return 1; operation failures return the Win32 error code;
///     success returns 0, or ERROR_SUCCESS_REBOOT_REQUIRED (3010) when a restart is needed. Arguments are
///     parsed by argh, where the listed value options consume the following token, so values starting with
///     '-' are rejected here. The devcon-style <c>install INF HWID</c> and <c>remove HWID</c> forms are
///     positional and are recognised before the switches.
///     Switch names: --install-driver, --uninstall-driver (--inf-path); --create-device-node
///     (--hardware-id --class-name --class-guid [--no-duplicates]); --remove-device-node (--hardware-id
///     --class-guid); --install-filter-driver (--inf-path); --uninstall-filter-driver (--position
///     --service-name --class-guid [--inf-path] [--all-versions]); --add-class-filter and
///     --remove-class-filter (--position --service-name --class-guid); --inf-default-install and
///     --inf-default-uninstall (--inf-path); --create-driver-service (--bin-path --service-name
///     --display-name); --remove-driver-service (--service-name); --remove-driver-store-package (--inf-name
///     [--class-guid] [--service-name]); --delete-file-on-reboot (--file-path). Optional restart and timeout
///     switches are not used, so nefcon's own defaults apply.
/// </remarks>
internal sealed partial class NefconRunner(
    VestigoOptions options,
    INefconProcess process,
    ILogger<NefconRunner> logger) : INefconRunner, IDisposable
{
    private const string BundledRelativePath = @"tools\nefconc.exe";

    private readonly SemaphoreSlim _gate = new(1, 1);

    [GeneratedRegex(@"^[A-Za-z0-9_.][A-Za-z0-9_.\- ]{0,255}$")]
    private static partial Regex NamePattern();

    public void Dispose() => _gate.Dispose();

    public string ExecutablePath => string.IsNullOrWhiteSpace(options.Devices.NefconPath)
        ? Path.Combine(AppContext.BaseDirectory, BundledRelativePath)
        : options.Devices.NefconPath;

    public Task<NefconResult> InstallDriver(
        string infPath,
        string? hardwareId = null,
        bool noDuplicates = false,
        bool removeDuplicates = false,
        CancellationToken cancellationToken = default)
    {
        string inf = RequireFilePath(infPath, nameof(infPath));
        if (hardwareId is null)
        {
            if (noDuplicates || removeDuplicates)
            {
                throw new ArgumentException("Duplicate handling needs a hardware ID.", nameof(noDuplicates));
            }

            return RunAsync(["--install-driver", "--inf-path", inf], cancellationToken);
        }

        List<string> arguments = ["install", inf, RequireValue(hardwareId, nameof(hardwareId))];
        if (removeDuplicates && !noDuplicates)
        {
            throw new ArgumentException("removeDuplicates requires noDuplicates.", nameof(removeDuplicates));
        }

        if (noDuplicates)
        {
            arguments.Add("--no-duplicates");
        }

        if (removeDuplicates)
        {
            arguments.Add("--remove-duplicates");
        }

        return RunAsync(arguments, cancellationToken);
    }

    public Task<NefconResult> UninstallDriver(string infPath, CancellationToken cancellationToken = default) =>
        RunAsync(["--uninstall-driver", "--inf-path", RequireFilePath(infPath, nameof(infPath))], cancellationToken);

    public Task<NefconResult> CreateDeviceNode(
        string className,
        Guid classGuid,
        string hardwareId,
        bool noDuplicates = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments =
        [
            "--create-device-node",
            "--hardware-id", RequireValue(hardwareId, nameof(hardwareId)),
            "--class-name", RequireName(className, nameof(className)),
            "--class-guid", FormatGuid(classGuid, nameof(classGuid))
        ];
        if (noDuplicates)
        {
            arguments.Add("--no-duplicates");
        }

        return RunAsync(arguments, cancellationToken);
    }

    public Task<NefconResult> RemoveDeviceNode(string hardwareId, Guid classGuid, CancellationToken cancellationToken = default) =>
        RunAsync(
        [
            "--remove-device-node",
            "--hardware-id", RequireValue(hardwareId, nameof(hardwareId)),
            "--class-guid", FormatGuid(classGuid, nameof(classGuid))
        ], cancellationToken);

    public Task<NefconResult> InstallFilterDriver(string infPath, CancellationToken cancellationToken = default) =>
        RunAsync(["--install-filter-driver", "--inf-path", RequireFilePath(infPath, nameof(infPath))], cancellationToken);

    public Task<NefconResult> UninstallFilterDriver(
        Guid classGuid,
        string serviceName,
        ClassFilterPosition position,
        string? infPath = null,
        bool allVersions = false,
        CancellationToken cancellationToken = default)
    {
        List<string> arguments =
        [
            "--uninstall-filter-driver",
            "--position", FormatPosition(position),
            "--service-name", RequireName(serviceName, nameof(serviceName)),
            "--class-guid", FormatGuid(classGuid, nameof(classGuid))
        ];
        if (infPath is not null)
        {
            arguments.Add("--inf-path");
            arguments.Add(RequireFilePath(infPath, nameof(infPath)));
            if (allVersions)
            {
                arguments.Add("--all-versions");
            }
        }
        else if (allVersions)
        {
            throw new ArgumentException("allVersions needs an INF path.", nameof(allVersions));
        }

        return RunAsync(arguments, cancellationToken);
    }

    public Task<NefconResult> AddClassFilter(
        Guid classGuid,
        string serviceName,
        ClassFilterPosition position,
        CancellationToken cancellationToken = default) =>
        RunAsync(ClassFilterArguments("--add-class-filter", classGuid, serviceName, position), cancellationToken);

    public Task<NefconResult> RemoveClassFilter(
        Guid classGuid,
        string serviceName,
        ClassFilterPosition position,
        CancellationToken cancellationToken = default) =>
        RunAsync(ClassFilterArguments("--remove-class-filter", classGuid, serviceName, position), cancellationToken);

    public Task<NefconResult> InfDefaultInstall(string infPath, CancellationToken cancellationToken = default) =>
        RunAsync(["--inf-default-install", "--inf-path", RequireFilePath(infPath, nameof(infPath))], cancellationToken);

    public Task<NefconResult> InfDefaultUninstall(string infPath, CancellationToken cancellationToken = default) =>
        RunAsync(["--inf-default-uninstall", "--inf-path", RequireFilePath(infPath, nameof(infPath))], cancellationToken);

    public Task<NefconResult> CreateDriverService(
        string serviceName,
        string displayName,
        string binPath,
        CancellationToken cancellationToken = default) =>
        RunAsync(
        [
            "--create-driver-service",
            "--bin-path", RequireFilePath(binPath, nameof(binPath)),
            "--service-name", RequireName(serviceName, nameof(serviceName)),
            "--display-name", RequireValue(displayName, nameof(displayName))
        ], cancellationToken);

    public Task<NefconResult> RemoveDriverService(string serviceName, CancellationToken cancellationToken = default) =>
        RunAsync(["--remove-driver-service", "--service-name", RequireName(serviceName, nameof(serviceName))], cancellationToken);

    public Task<NefconResult> RemoveDriverStorePackage(
        string infName,
        Guid? classGuid = null,
        string? serviceName = null,
        CancellationToken cancellationToken = default)
    {
        string name = RequireName(infName, nameof(infName));
        if (name.Contains(',', StringComparison.Ordinal))
        {
            throw new ArgumentException("Pass exactly one INF name.", nameof(infName));
        }

        List<string> arguments = ["--remove-driver-store-package", "--inf-name", name];
        if (classGuid is { } guid)
        {
            arguments.Add("--class-guid");
            arguments.Add(FormatGuid(guid, nameof(classGuid)));
        }

        if (serviceName is not null)
        {
            arguments.Add("--service-name");
            arguments.Add(RequireName(serviceName, nameof(serviceName)));
        }

        return RunAsync(arguments, cancellationToken);
    }

    public Task<NefconResult> DeleteFileOnReboot(string filePath, CancellationToken cancellationToken = default) =>
        RunAsync(["--delete-file-on-reboot", "--file-path", RequireFilePath(filePath, nameof(filePath))], cancellationToken);

    private static List<string> ClassFilterArguments(string command, Guid classGuid, string serviceName, ClassFilterPosition position) =>
    [
        command,
        "--position", FormatPosition(position),
        "--service-name", RequireName(serviceName, nameof(serviceName)),
        "--class-guid", FormatGuid(classGuid, nameof(classGuid))
    ];

    private async Task<NefconResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        string executable = ExecutablePath;
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException($"nefcon was not found at '{executable}'. Reinstall Vestigo or set Vestigo:Devices:NefconPath.", executable);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NefconProcessOutput output = await process
                .RunAsync(executable, arguments, options.Devices.NefconTimeout, cancellationToken)
                .ConfigureAwait(false);
            NefconResult result = new(output.ExitCode, output.Output, output.TimedOut);
            logger.LogInformation(
                "nefcon {Command} finished with exit code {ExitCode} (reboot required: {RebootRequired}, timed out: {TimedOut})",
                arguments[0],
                result.ExitCode,
                result.RebootRequired,
                result.TimedOut);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string FormatPosition(ClassFilterPosition position) => position switch
    {
        ClassFilterPosition.Upper => "upper",
        ClassFilterPosition.Lower => "lower",
        _ => throw new ArgumentOutOfRangeException(nameof(position))
    };

    private static string FormatGuid(Guid guid, string parameter) =>
        guid == Guid.Empty
            ? throw new ArgumentException("The GUID cannot be empty.", parameter)
            : guid.ToString("B", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();

    private static string RequireName(string value, string parameter)
    {
        RequireValue(value, parameter);
        return NamePattern().IsMatch(value)
            ? value
            : throw new ArgumentException("The value contains characters nefcon names do not allow.", parameter);
    }

    private static string RequireFilePath(string value, string parameter)
    {
        RequireValue(value, parameter);
        return Path.IsPathFullyQualified(value)
            ? value
            : throw new ArgumentException("The path must be absolute.", parameter);
    }

    private static string RequireValue(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('-') || value.Any(char.IsControl))
        {
            throw new ArgumentException("The value is empty, starts with '-', or contains control characters.", parameter);
        }

        return value;
    }
}