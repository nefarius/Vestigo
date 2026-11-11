namespace Vestigo.RegistryProbe;

/// <summary>Explicit registry view. The service process bitness does not choose this.</summary>
public enum RegistryViewKind
{
    /// <summary>64-bit registry view.</summary>
    Registry64,

    /// <summary>32-bit registry view.</summary>
    Registry32
}

/// <summary>Hive selected after alias normalization.</summary>
public enum RegistryHiveKind
{
    /// <summary><c>HKEY_LOCAL_MACHINE</c>.</summary>
    LocalMachine,

    /// <summary><c>HKEY_USERS</c>.</summary>
    Users
}

/// <summary>One registry value returned by <c>read_registry_value</c>.</summary>
/// <param name="Path">Requested canonical key path. This is the path to pass to another call.</param>
/// <param name="Name">Value name. Null is the key's default value.</param>
/// <param name="View"><c>registry64</c> or <c>registry32</c>.</param>
/// <param name="Type">Registry type name, such as <c>REG_SZ</c> or <c>REG_DWORD</c>.</param>
/// <param name="ByteLength">Size reported by Windows, in bytes.</param>
/// <param name="Text">String value, or null for other types.</param>
/// <param name="Values">REG_MULTI_SZ entries, or null.</param>
/// <param name="Number">DWORD or QWORD, or null.</param>
/// <param name="Hex">REG_BINARY hex when <c>includeBinary</c> is true, or null.</param>
/// <param name="Sha256">Uppercase SHA-256 of a binary value that was read, or null.</param>
/// <param name="LastWriteUtc">Last write time of the key, or null when Windows did not report one.</param>
/// <param name="ResolvedPath">Key opened after a symbolic link, or null when <paramref name="Path" /> was opened directly.</param>
public sealed record RegistryValueResult(
    string Path,
    string? Name,
    string View,
    string Type,
    long ByteLength,
    string? Text,
    IReadOnlyList<string>? Values,
    ulong? Number,
    string? Hex,
    string? Sha256,
    DateTimeOffset? LastWriteUtc,
    string? ResolvedPath = null);

/// <summary>One value inside a list result. Binary and oversized strings omit <see cref="DisplayValue" />.</summary>
/// <param name="Name">Value name. An empty string is the default value.</param>
/// <param name="Type">Registry type name.</param>
/// <param name="DisplayValue">Short rendering, or null when the value is binary or longer than the string cap.</param>
/// <param name="ByteLength">Size reported by Windows, in bytes.</param>
public sealed record RegistryListedValue(string Name, string Type, string? DisplayValue, long ByteLength);

/// <summary>One immediate child key.</summary>
/// <param name="Name">Subkey name.</param>
/// <param name="ValueCount">Number of values, or null when the subkey could not be opened.</param>
public sealed record RegistryListedSubKey(string Name, int? ValueCount);

/// <summary>One key in a list result.</summary>
/// <param name="Path">Requested canonical key path. This is the path to pass to another call.</param>
/// <param name="LastWriteUtc">Last write time, or null.</param>
/// <param name="Values">Values that fit in the entry cap.</param>
/// <param name="SubKeys">Child names that fit in the entry cap and are not denied.</param>
/// <param name="ResolvedPath">Key opened after a symbolic link, or null when <paramref name="Path" /> was opened directly.</param>
public sealed record RegistryKeyListing(
    string Path,
    DateTimeOffset? LastWriteUtc,
    IReadOnlyList<RegistryListedValue> Values,
    IReadOnlyList<RegistryListedSubKey> SubKeys,
    string? ResolvedPath = null);

/// <summary>Result of <c>list_registry_values</c>.</summary>
/// <param name="Keys">Keys opened, in walk order.</param>
/// <param name="Total">Number of keys returned.</param>
/// <param name="Truncated">True when the entry cap or recursion cap stopped the walk.</param>
public sealed record RegistryListResult(IReadOnlyList<RegistryKeyListing> Keys, int Total, bool Truncated);

/// <summary>Result of <c>get_registry_metadata</c>. Values are not read.</summary>
/// <param name="Path">Canonical key path.</param>
/// <param name="View"><c>registry64</c> or <c>registry32</c>.</param>
/// <param name="LastWriteUtc">Last write time, or null.</param>
/// <param name="SubKeyCount">Number of subkeys.</param>
/// <param name="ValueCount">Number of values.</param>
/// <param name="ResolvedPath">Key opened after a symbolic link, or null when <paramref name="Path" /> was opened directly.</param>
public sealed record RegistryMetadata(
    string Path,
    string View,
    DateTimeOffset? LastWriteUtc,
    int SubKeyCount,
    int ValueCount,
    string? ResolvedPath = null);
