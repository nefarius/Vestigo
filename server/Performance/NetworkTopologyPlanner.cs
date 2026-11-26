namespace Vestigo.Performance;

/// <summary>Adapter matching and TCP template mapping for <see cref="WindowsNetworkTopology"/>.</summary>
public static class NetworkTopologyPlanner
{
    public static string? TimestampPolicy(int? value, string? text) =>
        value switch
        {
            0 => "disabled",
            1 => "enabled",
            2 => "allowed",
            _ => string.IsNullOrWhiteSpace(text) ? null : text
        };

    public static string NormalizeGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().Trim('{', '}');
    }

    public static T? MatchAdapter<T>(
        string nicId,
        string nicName,
        string nicDescription,
        IReadOnlyDictionary<string, T> byGuid,
        IReadOnlyDictionary<string, T> byName,
        IReadOnlyDictionary<string, T> byDescription)
        where T : class
    {
        string id = NormalizeGuid(nicId);
        if (id.Length > 0 && byGuid.TryGetValue(id, out T? row))
        {
            return row;
        }

        if (nicName.Length > 0 && byName.TryGetValue(nicName, out row))
        {
            return row;
        }

        if (nicDescription.Length > 0 && byDescription.TryGetValue(nicDescription, out row))
        {
            return row;
        }

        return null;
    }

    public static string EscapeWqlString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
