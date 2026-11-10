namespace Vestigo.RegistryProbe;

/// <summary>Console user lookup used to resolve an <c>HKCU</c> path.</summary>
/// <param name="Sid">Account SID, or null when lookup failed.</param>
/// <param name="ErrorCode">Stable error code when <paramref name="Sid" /> is null.</param>
/// <param name="Error">Caller-facing message when <paramref name="Sid" /> is null.</param>
public sealed record ConsoleUserLookup(string? Sid, string? ErrorCode, string? Error);

/// <summary>A page of registry names, stopped at the caller's limit.</summary>
/// <param name="Names">Names that fit in the limit.</param>
/// <param name="Truncated">True when another name existed past the limit.</param>
public sealed record RegistryNamePage(IReadOnlyList<string> Names, bool Truncated);

/// <summary>Bytes of one registry value, or a size that was not allocated.</summary>
/// <param name="Found">False when the value is absent.</param>
/// <param name="ErrorCode">Stable code when the read failed.</param>
/// <param name="Error">Caller-facing message when the read failed.</param>
/// <param name="Type">Registry type name.</param>
/// <param name="ByteLength">Size reported by Windows.</param>
/// <param name="Data">Value bytes, or null when the value is absent or larger than the cap.</param>
/// <param name="TooLarge">True when the size was above the cap and <paramref name="Data" /> was not allocated.</param>
public sealed record RegistryRawValue(
    bool Found,
    string? ErrorCode,
    string? Error,
    string Type,
    int ByteLength,
    byte[]? Data,
    bool TooLarge);

/// <summary>One opened registry key.</summary>
public interface IRegistryKey : IDisposable
{
    /// <summary>Last write time of the key, or null.</summary>
    DateTimeOffset? LastWriteUtc { get; }

    /// <summary>Number of subkeys.</summary>
    int SubKeyCount { get; }

    /// <summary>Number of values.</summary>
    int ValueCount { get; }

    /// <summary>Reads up to <paramref name="limit" /> subkey names.</summary>
    RegistryNamePage SubKeyNames(int limit);

    /// <summary>Reads up to <paramref name="limit" /> value names. An empty name is the default value.</summary>
    RegistryNamePage ValueNames(int limit);

    /// <summary>Value count of a child, or null when the child cannot be opened.</summary>
    int? SubKeyValueCount(string name);

    /// <summary>Reads one value, refusing to allocate data above the matching cap.</summary>
    RegistryRawValue Read(string? name, int maxBinaryBytes, int maxStringBytes);
}

/// <summary>Result of opening a key.</summary>
/// <param name="Key">Opened key, or null.</param>
/// <param name="ErrorCode">Stable code when <paramref name="Key" /> is null.</param>
/// <param name="Error">Caller-facing message when <paramref name="Key" /> is null.</param>
public sealed record RegistryOpen(IRegistryKey? Key, string? ErrorCode, string? Error);

/// <summary>Reads registry keys. Tests supply a fake.</summary>
public interface IRegistryStore
{
    /// <summary>Resolves the user logged on at the console.</summary>
    ConsoleUserLookup LookupConsoleUser();

    /// <summary>Opens a key in an explicit view. <paramref name="subKey" /> is relative to the hive.</summary>
    RegistryOpen Open(RegistryViewKind view, RegistryHiveKind hive, string subKey);
}
