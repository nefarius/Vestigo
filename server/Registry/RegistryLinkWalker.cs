namespace Vestigo.RegistryProbe;

/// <summary>What one opened registry segment says about a symbolic link.</summary>
/// <param name="ErrorCode">Stable code when the segment could not be probed.</param>
/// <param name="Error">Caller-facing message when the segment could not be probed.</param>
/// <param name="IsLink">True when the segment is a <c>REG_LINK</c>.</param>
/// <param name="Target">Link target bytes, or null when <see cref="IsLink" /> is false.</param>
public readonly record struct RegistryLinkProbe(string? ErrorCode, string? Error, bool IsLink, byte[]? Target)
{
    /// <summary>A key that is not a symbolic link.</summary>
    public static RegistryLinkProbe NotALink() => new(null, null, false, null);

    /// <summary>A symbolic link whose target bytes were already bounded.</summary>
    public static RegistryLinkProbe Link(byte[] target) => new(null, null, true, target);

    /// <summary>The segment could not be opened.</summary>
    public static RegistryLinkProbe Fail(string code, string error) => new(code, error, false, null);
}

/// <summary>Follows registry symbolic links and stops when a target fails confinement.</summary>
public static class RegistryLinkWalker
{
    /// <summary>Resolves <paramref name="requested" /> to the key that should be read.</summary>
    public static OperationResult<RegistryPathDecision> Resolve(
        RegistryPathDecision requested,
        Func<RegistryHiveKind, string, RegistryLinkProbe> probe,
        RegistryLinkConfinement confine) =>
        Resolve(requested, probe, confine, RegistryPathPolicy.MaxLinkHops, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static OperationResult<RegistryPathDecision> Resolve(
        RegistryPathDecision requested,
        Func<RegistryHiveKind, string, RegistryLinkProbe> probe,
        RegistryLinkConfinement confine,
        int hopsLeft,
        HashSet<string> seen)
    {
        if (!seen.Add(requested.Canonical))
        {
            return Invalid("Registry symbolic link cycle.");
        }

        string[] segments = requested.SubKey.Length == 0 ? [] : requested.SubKey.Split('\\');
        string walkedSub = string.Empty;
        string walkedCanonical = requested.Hive == RegistryHiveKind.Users ? "HKU" : "HKLM";
        for (int index = 0; index < segments.Length; index++)
        {
            string sub = walkedSub.Length == 0 ? segments[index] : walkedSub + "\\" + segments[index];
            RegistryLinkProbe probed = probe(requested.Hive, sub);
            if (probed.ErrorCode is not null)
            {
                return OperationResult<RegistryPathDecision>.Fail(probed.ErrorCode, probed.Error!);
            }

            if (!probed.IsLink)
            {
                walkedSub = sub;
                walkedCanonical += "\\" + segments[index];
                continue;
            }

            if (hopsLeft <= 0)
            {
                return Invalid("Registry symbolic link chain is too long.");
            }

            if (probed.Target is null)
            {
                return Invalid("Registry symbolic link target is not a supported path.");
            }

            OperationResult<string> target = RegistryPathPolicy.ParseLinkTarget(probed.Target);
            if (!target.Success || target.Value is null)
            {
                return target.FailAs<RegistryPathDecision>();
            }

            string[] remaining = segments[(index + 1)..];
            string linkKey = walkedCanonical + "\\" + segments[index];
            string logical = remaining.Length == 0 ? linkKey : linkKey + "\\" + string.Join("\\", remaining);
            string candidate = remaining.Length == 0 ? target.Value : target.Value + "\\" + string.Join("\\", remaining);
            OperationResult<RegistryPathDecision> confined = confine(logical, candidate);
            if (!confined.Success || confined.Value is null)
            {
                return confined;
            }

            return Resolve(confined.Value, probe, confine, hopsLeft - 1, seen);
        }

        return OperationResult<RegistryPathDecision>.Ok(new RegistryPathDecision(walkedCanonical, requested.Hive, walkedSub));
    }

    private static OperationResult<RegistryPathDecision> Invalid(string message) =>
        OperationResult<RegistryPathDecision>.Fail("invalid_request", message);
}
