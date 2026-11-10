using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vestigo.Desktop;

public sealed record DisplayTargetIdentity(
    string? MonitorDevicePath,
    uint ConnectorInstance,
    string AdapterLuid,
    uint TargetId,
    uint SourceId)
{
    public string Key => string.Join('|', AdapterLuid, TargetId.ToString(), ConnectorInstance.ToString(), MonitorDevicePath);
}

public sealed record DisplayPathLayout(
    DisplayTargetIdentity Identity,
    string? GdiDeviceName,
    bool Primary,
    int X,
    int Y,
    int Width,
    int Height,
    uint RefreshNumerator,
    uint RefreshDenominator,
    string Rotation,
    string Scaling,
    string CoordinateSpace);

public sealed record DisplayLayoutSnapshot(
    DateTimeOffset CapturedAt,
    int SessionId,
    string CoordinateSpace,
    string Fingerprint,
    IReadOnlyList<DisplayPathLayout> Paths);

public sealed record DisplayTargetChange(
    string? MonitorDevicePath,
    uint? ConnectorInstance,
    string? AdapterLuid,
    uint? TargetId,
    int X,
    int Y,
    int Width,
    int Height,
    uint? RefreshNumerator,
    uint? RefreshDenominator,
    string? Rotation);

public sealed record DisplayLayoutRequest(IReadOnlyList<DisplayTargetChange> Targets, bool Persistent, bool ValidateOnly);

public sealed record DisplayLayoutValidation(bool Valid, IReadOnlyList<string> Errors);

public sealed record DisplayLayoutApplyResult(
    bool Applied,
    bool Validated,
    bool Persistent,
    int? NativeError,
    string? ErrorCode,
    string? Error,
    DisplayLayoutSnapshot? Before,
    DisplayLayoutSnapshot? After);

public sealed record DisplayLayoutTransaction(
    string Id,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? LeaseUntil,
    bool Persistent,
    DisplayLayoutSnapshot Original,
    IReadOnlyList<DisplayTargetChange> Requested,
    DisplayLayoutSnapshot? Current,
    bool RestorationVerified,
    IReadOnlyList<string> Differences,
    string? Error);

public sealed record DisplayLayoutObservation(
    string Id,
    bool Matched,
    bool Stable,
    int StableSamples,
    int RequiredStableSamples,
    bool TimedOut,
    DisplayPathLayout? Target,
    IReadOnlyList<string> Differences,
    DisplayLayoutSnapshot? Current);

public sealed record DisplayWait(TimeSpan Timeout, int StableSamples, DisplayTargetChange? Target);

/// <summary>Checks a requested layout against a snapshot before any display API is called.</summary>
public static class DisplayLayoutPlanner
{
    public static readonly string[] Rotations = ["IDENTITY", "ROTATE90", "ROTATE180", "ROTATE270"];

    public static DisplayLayoutValidation Validate(DisplayLayoutSnapshot snapshot, IReadOnlyList<DisplayTargetChange> changes)
    {
        List<string> errors = [];
        if (changes.Count is < 1 or > 16)
        {
            errors.Add("A layout change needs 1 to 16 targets.");
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (DisplayTargetChange change in changes)
        {
            if (change.Width is < 1 or > 16_384 || change.Height is < 1 or > 16_384)
            {
                errors.Add("Width and height must be from 1 to 16384.");
            }

            if (change.RefreshNumerator is uint numerator)
            {
                uint denominator = change.RefreshDenominator ?? 0;
                double hz = denominator == 0 ? 0 : numerator / (double)denominator;
                if (hz is < 1 or > 1000)
                {
                    errors.Add("Refresh must be between 1 and 1000 Hz.");
                }
            }

            if (change.Rotation is not null && !Rotations.Contains(change.Rotation, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add("Rotation must be IDENTITY, ROTATE90, ROTATE180, or ROTATE270.");
            }

            if (change.AdapterLuid is null && change.TargetId is null && string.IsNullOrWhiteSpace(change.MonitorDevicePath))
            {
                errors.Add("A target needs an adapter LUID and target id, or a monitor device path.");
                continue;
            }

            DisplayPathLayout[] matches = snapshot.Paths.Where(path => Matches(path.Identity, change)).ToArray();
            if (matches.Length == 0)
            {
                errors.Add("A requested target is not in the active topology.");
            }
            else if (matches.Length > 1)
            {
                errors.Add("A requested target identity matches more than one active path.");
            }

            if (matches.Length == 1 && !seen.Add(matches[0].Identity.Key))
            {
                errors.Add("The same target was requested more than once.");
            }
        }

        return new DisplayLayoutValidation(errors.Count == 0, errors);
    }

    public static bool Matches(DisplayTargetIdentity identity, DisplayTargetChange change)
    {
        if (change.AdapterLuid is not null && !string.Equals(change.AdapterLuid, identity.AdapterLuid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (change.TargetId is uint target && target != identity.TargetId)
        {
            return false;
        }

        if (change.MonitorDevicePath is not null
            && !string.Equals(change.MonitorDevicePath, identity.MonitorDevicePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (change.ConnectorInstance is uint connector && connector != identity.ConnectorInstance)
        {
            return false;
        }

        return change.AdapterLuid is not null && change.TargetId is not null || !string.IsNullOrWhiteSpace(change.MonitorDevicePath);
    }

    public static string Fingerprint(IEnumerable<DisplayPathLayout> paths)
    {
        string canonical = string.Join(
            '\n',
            paths.OrderBy(path => path.Identity.Key, StringComparer.Ordinal).Select(path =>
                string.Join('|', path.Identity.Key, path.X, path.Y, path.Width, path.Height, path.RefreshNumerator, path.RefreshDenominator, path.Rotation, path.Scaling)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static IReadOnlyList<string> Compare(DisplayLayoutSnapshot expected, DisplayLayoutSnapshot actual)
    {
        List<string> differences = [];
        if (!string.Equals(expected.Fingerprint, actual.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add("topology fingerprint differs");
        }

        foreach (DisplayPathLayout path in expected.Paths)
        {
            DisplayPathLayout? found = actual.Paths.FirstOrDefault(candidate => candidate.Identity.Key == path.Identity.Key);
            if (found is null)
            {
                differences.Add(path.Identity.Key + " is not active");
                continue;
            }

            differences.AddRange(Fields(path, found));
        }

        return differences;
    }

    public static IReadOnlyList<string> Compare(DisplayTargetChange expected, DisplayLayoutSnapshot actual)
    {
        DisplayPathLayout? found = actual.Paths.FirstOrDefault(path => Matches(path.Identity, expected));
        if (found is null)
        {
            return ["target is not active"];
        }

        List<string> differences = [];
        if (expected.Width > 0 && expected.Height > 0)
        {
            if (found.X != expected.X || found.Y != expected.Y)
            {
                differences.Add($"position is {found.X},{found.Y}");
            }

            if (found.Width != expected.Width || found.Height != expected.Height)
            {
                differences.Add($"mode is {found.Width}x{found.Height}");
            }
        }

        if (expected.Rotation is not null && !string.Equals(found.Rotation, expected.Rotation, StringComparison.OrdinalIgnoreCase))
        {
            differences.Add($"rotation is {found.Rotation}");
        }

        if (expected.RefreshNumerator is uint numerator && expected.RefreshDenominator is uint denominator
            && (found.RefreshNumerator != numerator || found.RefreshDenominator != denominator))
        {
            differences.Add($"refresh is {found.RefreshNumerator}/{found.RefreshDenominator}");
        }

        return differences;
    }

    public static DisplayLayoutSnapshot WithFingerprint(DisplayLayoutSnapshot snapshot) =>
        snapshot with { Fingerprint = Fingerprint(snapshot.Paths) };

    public static string Canonical(DisplayLayoutRequest request) => JsonSerializer.Serialize(request);

    private static IEnumerable<string> Fields(DisplayPathLayout expected, DisplayPathLayout actual)
    {
        if (expected.X != actual.X || expected.Y != actual.Y || expected.Width != actual.Width || expected.Height != actual.Height)
        {
            yield return expected.Identity.Key + " geometry differs";
        }

        if (!string.Equals(expected.Rotation, actual.Rotation, StringComparison.OrdinalIgnoreCase)
            || expected.RefreshNumerator != actual.RefreshNumerator
            || expected.RefreshDenominator != actual.RefreshDenominator)
        {
            yield return expected.Identity.Key + " mode differs";
        }
    }
}
