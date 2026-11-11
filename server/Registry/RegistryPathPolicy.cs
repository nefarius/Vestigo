using System.Text;

namespace Vestigo.RegistryProbe;

/// <summary>A path that passed allow and deny checks.</summary>
/// <param name="Canonical">Short-hive path, such as <c>HKLM\SOFTWARE\Contoso</c>.</param>
/// <param name="Hive">Hive to open.</param>
/// <param name="SubKey">Path under the hive. Empty for the hive root.</param>
public sealed record RegistryPathDecision(string Canonical, RegistryHiveKind Hive, string SubKey);

/// <summary>
///     Canonicalizes registry paths and applies deny-wins prefix matching.
///     A prefix matches the path itself or a child after a backslash, so <c>Contoso</c> does not authorize <c>ContosoEvil</c>.
/// </summary>
public static class RegistryPathPolicy
{
    /// <summary>Largest symbolic-link target that will be allocated, in bytes.</summary>
    public const int MaxLinkTargetBytes = 4096;

    /// <summary>Maximum number of symbolic links followed for one open.</summary>
    public const int MaxLinkHops = 8;

    private const int MaxPathLength = 4096;

    /// <summary>True when <paramref name="prefix" /> starts with HKLM, HKU, or HKCU and has no empty, <c>.</c>, or <c>..</c> segments.</summary>
    public static bool IsWellFormedPrefix(string? prefix)
    {
        string[] segments = Segments(prefix);
        return segments.Length > 0 && TryHive(segments[0], out _, out _, out _);
    }

    /// <summary>True when the path uses the <c>HKCU</c> alias and needs the console user's SID.</summary>
    public static bool NeedsConsoleUser(string? path) =>
        FirstSegment(NormalizeSeparators(path)) is "HKCU" or "HKEY_CURRENT_USER";

    /// <summary>True when the path addresses <c>HKEY_USERS</c> directly.</summary>
    public static bool IsUsersHive(string? path) =>
        FirstSegment(NormalizeSeparators(path)) is "HKU" or "HKEY_USERS";

    /// <summary>Returns a canonical path, or a stable error. Denied prefixes win over allowed ones.</summary>
    public static OperationResult<RegistryPathDecision> Check(string? path, string? consoleSid, RegistryOptions options)
    {
        OperationResult<RegistryPathDecision> canonical = Canonicalize(path, consoleSid);
        if (!canonical.Success || canonical.Value is null)
        {
            return canonical;
        }

        IReadOnlyList<string> allowed = options.AllowedPrefixes.Count == 0
            ? RegistryOptions.BuiltInAllowedPrefixes
            : options.AllowedPrefixes;
        if (MatchesAny(canonical.Value.Canonical, options.DeniedPrefixes, consoleSid)
            || DeniedAsCurrentControlSet(canonical.Value.Canonical, options.DeniedPrefixes, consoleSid))
        {
            return OperationResult<RegistryPathDecision>.Fail(
                "path_denied",
                "Registry path is denied.");
        }

        if (!MatchesAny(canonical.Value.Canonical, allowed, consoleSid))
        {
            return OperationResult<RegistryPathDecision>.Fail(
                "path_not_allowed",
                "Registry path is outside the allowed prefixes.");
        }

        return canonical;
    }

    /// <summary>Canonicalizes a path without applying allow or deny prefixes.</summary>
    public static OperationResult<RegistryPathDecision> Canonicalize(string? path, string? consoleSid)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            return Invalid("Registry path is empty.");
        }

        if (path.Length > MaxPathLength)
        {
            return Invalid($"Registry path is longer than {MaxPathLength} characters.");
        }

        string[] segments = Segments(path);
        if (segments.Length == 0)
        {
            return Invalid("Registry path is empty.");
        }

        if (!TryHive(segments[0], out string? hiveName, out RegistryHiveKind hive, out bool currentUser))
        {
            return Invalid("Hive is not supported. Use HKLM, HKU, or HKCU.");
        }

        string[] rest = segments.Skip(1).ToArray();
        if (currentUser)
        {
            if (string.IsNullOrWhiteSpace(consoleSid))
            {
                return OperationResult<RegistryPathDecision>.Fail(
                    "no_interactive_session",
                    "No user is logged on at the console.");
            }

            hiveName = "HKU";
            hive = RegistryHiveKind.Users;
            rest = [consoleSid.Trim(), .. rest];
        }

        string canonical = hiveName + (rest.Length == 0 ? string.Empty : "\\" + string.Join("\\", rest));
        string subKey = string.Join("\\", rest);
        return OperationResult<RegistryPathDecision>.Ok(new RegistryPathDecision(canonical, hive, subKey));
    }

    /// <summary>
    ///     Applies deny-wins to a link target. <c>HKLM\SYSTEM\CurrentControlSet</c> may resolve to
    ///     <c>HKLM\SYSTEM\ControlSetNNN</c> and still matches a CurrentControlSet prefix. No other link gets that alias.
    /// </summary>
    public static OperationResult<RegistryPathDecision> CheckLink(
        string logicalPath,
        string targetPath,
        string? consoleSid,
        RegistryOptions options)
    {
        OperationResult<RegistryPathDecision> target = Check(targetPath, consoleSid, options);
        if (target.Success || target.ErrorCode != "path_not_allowed" || !SameCurrentControlSet(logicalPath, targetPath))
        {
            return target;
        }

        OperationResult<RegistryPathDecision> logical = Check(logicalPath, consoleSid, options);
        return logical.Success ? Canonicalize(targetPath, consoleSid) : logical;
    }

    /// <summary>True when <paramref name="target" /> is the <c>ControlSetNNN</c> image of a <c>CurrentControlSet</c> path.</summary>
    public static bool SameCurrentControlSet(string logical, string target)
    {
        const string Current = @"HKLM\SYSTEM\CurrentControlSet";
        const string Numbered = @"HKLM\SYSTEM\ControlSet";
        if (!logical.Equals(Current, StringComparison.OrdinalIgnoreCase)
            && !logical.StartsWith(Current + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!target.StartsWith(Numbered, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string logicalRest = logical.Length == Current.Length ? string.Empty : logical[(Current.Length + 1)..];
        string after = target[Numbered.Length..];
        int slash = after.IndexOf('\\');
        string number = slash < 0 ? after : after[..slash];
        string targetRest = slash < 0 ? string.Empty : after[(slash + 1)..];
        return number.Length == 3
            && number.All(char.IsAsciiDigit)
            && logicalRest.Equals(targetRest, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     True when <paramref name="canonical" /> is <c>ControlSetNNN</c> and that same path under
    ///     <c>CurrentControlSet</c> is denied. Allow matching is unchanged.
    /// </summary>
    private static bool DeniedAsCurrentControlSet(string canonical, IEnumerable<string> denied, string? consoleSid)
    {
        const string numbered = @"HKLM\SYSTEM\ControlSet";
        if (!canonical.StartsWith(numbered, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string after = canonical[numbered.Length..];
        int slash = after.IndexOf('\\');
        string number = slash < 0 ? after : after[..slash];
        if (number.Length != 3 || !number.All(char.IsAsciiDigit))
        {
            return false;
        }

        string rest = slash < 0 ? string.Empty : after[(slash + 1)..];
        string image = @"HKLM\SYSTEM\CurrentControlSet" + (rest.Length == 0 ? string.Empty : "\\" + rest);
        return MatchesAny(image, denied, consoleSid);
    }

    /// <summary>Accepts a reported <c>REG_LINK</c> size before a buffer is allocated.</summary>
    public static OperationResult<int> LinkTargetLength(uint reportedBytes)
    {
        if (reportedBytes == 0 || reportedBytes > MaxLinkTargetBytes || (reportedBytes & 1) != 0)
        {
            return OperationResult<int>.Fail("invalid_request", "Registry symbolic link target is not a supported path.");
        }

        return OperationResult<int>.Ok(checked((int)reportedBytes));
    }

    /// <summary>Parses a <c>REG_LINK</c> value. Windows stores a UTF-16 NT path, with or without a terminator.</summary>
    public static OperationResult<string> ParseLinkTarget(ReadOnlySpan<byte> data)
    {
        OperationResult<int> length = LinkTargetLength((uint)data.Length);
        if (!length.Success)
        {
            return OperationResult<string>.Fail(length.ErrorCode!, length.Error!);
        }

        string text = Encoding.Unicode.GetString(data);
        int terminator = text.IndexOf('\0');
        if (terminator >= 0)
        {
            if (text[(terminator + 1)..].Any(character => character != '\0'))
            {
                return OperationResult<string>.Fail("invalid_request", "Registry symbolic link target is not a supported path.");
            }

            text = text[..terminator];
        }

        if (!text.StartsWith('\\'))
        {
            return OperationResult<string>.Fail("invalid_request", "Registry symbolic link target is not a supported path.");
        }

        string[] parts = text.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("Registry", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<string>.Fail("invalid_request", "Registry symbolic link target is not a supported path.");
        }

        string? hive = parts[1].ToUpperInvariant() switch
        {
            "MACHINE" => "HKLM",
            "USER" => "HKU",
            _ => null
        };
        if (hive is null)
        {
            return OperationResult<string>.Fail("invalid_request", "Registry symbolic link target is not a supported path.");
        }

        string rest = string.Join('\\', parts.Skip(2));
        return OperationResult<string>.Ok(hive + (rest.Length == 0 ? string.Empty : "\\" + rest));
    }

    private static bool MatchesAny(string canonical, IEnumerable<string> prefixes, string? consoleSid)
    {
        foreach (string prefix in prefixes)
        {
            if (TryCanonicalPrefix(prefix, consoleSid, out string? canonicalPrefix) && Matches(canonical, canonicalPrefix))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryCanonicalPrefix(string prefix, string? consoleSid, out string canonical)
    {
        canonical = string.Empty;
        string[] segments = Segments(prefix);
        if (segments.Length == 0 || !TryHive(segments[0], out string? hiveName, out _, out bool currentUser))
        {
            return false;
        }

        string[] rest = segments.Skip(1).ToArray();
        if (currentUser)
        {
            if (string.IsNullOrWhiteSpace(consoleSid))
            {
                return false;
            }

            hiveName = "HKU";
            rest = [consoleSid.Trim(), .. rest];
        }

        canonical = hiveName + (rest.Length == 0 ? string.Empty : "\\" + string.Join("\\", rest));
        return true;
    }

    private static bool Matches(string canonical, string prefix) =>
        canonical.Equals(prefix, StringComparison.OrdinalIgnoreCase)
        || canonical.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool TryHive(string segment, out string hiveName, out RegistryHiveKind hive, out bool currentUser)
    {
        currentUser = false;
        switch (segment.ToUpperInvariant())
        {
            case "HKLM":
            case "HKEY_LOCAL_MACHINE":
                hiveName = "HKLM";
                hive = RegistryHiveKind.LocalMachine;
                return true;
            case "HKU":
            case "HKEY_USERS":
                hiveName = "HKU";
                hive = RegistryHiveKind.Users;
                return true;
            case "HKCU":
            case "HKEY_CURRENT_USER":
                hiveName = "HKCU";
                hive = RegistryHiveKind.Users;
                currentUser = true;
                return true;
            default:
                hiveName = string.Empty;
                hive = default;
                return false;
        }
    }

    private static string[] Segments(string? path)
    {
        string normalized = NormalizeSeparators(path).Trim('\\');
        if (normalized.Length == 0)
        {
            return [];
        }

        string[] parts = normalized.Split('\\');
        if (parts.Any(part => part.Length == 0 || part == "." || part == ".."))
        {
            return [];
        }

        return parts;
    }

    private static string? FirstSegment(string normalized)
    {
        string trimmed = normalized.Trim('\\');
        if (trimmed.Length == 0)
        {
            return null;
        }

        int split = trimmed.IndexOf('\\');
        string first = split < 0 ? trimmed : trimmed[..split];
        return first.ToUpperInvariant();
    }

    private static string NormalizeSeparators(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : string.Join("\\", path.Trim().Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries));

    private static OperationResult<RegistryPathDecision> Invalid(string message) =>
        OperationResult<RegistryPathDecision>.Fail("invalid_request", message);
}
