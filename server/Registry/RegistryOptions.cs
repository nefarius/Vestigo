namespace Vestigo.RegistryProbe;

/// <summary>Settings for the Registry module, bound from <c>Vestigo:Registry</c>.</summary>
public sealed class RegistryOptions
{
    public const string SectionName = "Registry";

    /// <summary>
    ///     Key prefixes that may be read. Empty uses the built-in Windows locations. A non-empty list replaces
    ///     that set. Matching is case-insensitive and stops at a path boundary.
    /// </summary>
    public List<string> AllowedPrefixes { get; set; } = [];

    /// <summary>
    ///     Key prefixes that are never read. Entries are added to the built-in deny list because the configuration
    ///     binder appends to existing lists, and a denied prefix wins over an allowed one.
    /// </summary>
    public List<string> DeniedPrefixes { get; set; } =
    [
        @"HKLM\SAM",
        @"HKLM\SECURITY",
        @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa",
        @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    ];

    /// <summary>Most keys, values, and subkey names returned by one list call.</summary>
    public int MaxEntries { get; set; } = 500;

    /// <summary>Deepest nested key <c>list_registry_values</c> will open. Zero lists only the requested key.</summary>
    public int MaxRecursionDepth { get; set; } = 4;

    /// <summary>Largest REG_BINARY value returned or hashed. A larger value fails before it is allocated.</summary>
    public int MaxBinaryBytes { get; set; } = 4096;

    /// <summary>Longest string value returned, in characters.</summary>
    public int MaxValueStringLength { get; set; } = 4096;

    /// <summary>Built-in allow list used when <see cref="AllowedPrefixes" /> is empty.</summary>
    public static IReadOnlyList<string> BuiltInAllowedPrefixes { get; } =
    [
        @"HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard",
        @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager",
        @"HKLM\SYSTEM\CurrentControlSet\Control\Class",
        @"HKLM\SYSTEM\CurrentControlSet\Services",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    public void Validate()
    {
        if (MaxEntries < 1 || MaxEntries > 10000)
        {
            throw new InvalidOperationException("Vestigo:Registry:MaxEntries must be from 1 through 10000.");
        }

        if (MaxRecursionDepth < 0 || MaxRecursionDepth > 32)
        {
            throw new InvalidOperationException("Vestigo:Registry:MaxRecursionDepth must be from 0 through 32.");
        }

        if (MaxBinaryBytes < 1 || MaxBinaryBytes > 1048576)
        {
            throw new InvalidOperationException("Vestigo:Registry:MaxBinaryBytes must be from 1 through 1048576.");
        }

        if (MaxValueStringLength < 1 || MaxValueStringLength > 65536)
        {
            throw new InvalidOperationException("Vestigo:Registry:MaxValueStringLength must be from 1 through 65536.");
        }

        if (AllowedPrefixes.Concat(DeniedPrefixes).Any(prefix => !RegistryPathPolicy.IsWellFormedPrefix(prefix)))
        {
            throw new InvalidOperationException("Vestigo:Registry prefixes must be HKLM, HKU, or HKCU paths.");
        }
    }
}
