using System.Globalization;

namespace Vestigo.EventLogs;

/// <summary>Filters that translate into an Event Log XPath selection.</summary>
public sealed record EventLogFilter(
    IReadOnlyList<string>? Providers = null,
    IReadOnlyList<string>? Levels = null,
    IReadOnlyList<int>? EventIds = null,
    DateTimeOffset? Since = null,
    DateTimeOffset? Until = null);

/// <summary>Builds the XPath query passed to the Windows Event Log API. Pure and side-effect free.</summary>
public static class EventLogQueryBuilder
{
    private static readonly Dictionary<string, int> LevelNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["critical"] = 1,
        ["error"] = 2,
        ["warning"] = 3,
        ["information"] = 4,
        ["informational"] = 4,
        ["verbose"] = 5
    };

    public static string LevelName(int? level) => level switch
    {
        0 => "logalways",
        1 => "critical",
        2 => "error",
        3 => "warning",
        4 => "information",
        5 => "verbose",
        null => "unknown",
        _ => level.Value.ToString(CultureInfo.InvariantCulture)
    };

    public static string Build(EventLogFilter filter)
    {
        List<string> conditions = [];

        if (filter.Providers is { Count: > 0 })
        {
            conditions.Add("(" + string.Join(" or ", filter.Providers.Select(name => $"Provider[@Name={Literal(name)}]")) + ")");
        }

        if (filter.Levels is { Count: > 0 })
        {
            IEnumerable<int> numbers = filter.Levels
                .Select(level => LevelNumbers.TryGetValue(level.Trim(), out int number)
                    ? number
                    : throw new ArgumentException(
                        $"Unknown level '{level}'. Use critical, error, warning, information, or verbose."))
                .Distinct()
                .Order();
            conditions.Add("(" + string.Join(" or ", numbers.Select(number => $"Level={number.ToString(CultureInfo.InvariantCulture)}")) + ")");
        }

        if (filter.EventIds is { Count: > 0 })
        {
            if (filter.EventIds.Any(id => id is < 0 or > 65535))
            {
                throw new ArgumentException("Event IDs must be between 0 and 65535.");
            }

            conditions.Add("(" + string.Join(" or ", filter.EventIds.Distinct().Select(id => $"EventID={id.ToString(CultureInfo.InvariantCulture)}")) + ")");
        }

        List<string> times = [];
        if (filter.Since is { } since)
        {
            times.Add($"@SystemTime>='{Timestamp(since)}'");
        }

        if (filter.Until is { } until)
        {
            times.Add($"@SystemTime<='{Timestamp(until)}'");
        }

        if (filter.Since is { } from && filter.Until is { } to && from > to)
        {
            throw new ArgumentException("since must not be later than until.");
        }

        if (times.Count > 0)
        {
            conditions.Add("TimeCreated[" + string.Join(" and ", times) + "]");
        }

        return conditions.Count == 0 ? "*" : "*[System[" + string.Join(" and ", conditions) + "]]";
    }

    /// <summary>XPath 1.0 has no escape syntax, so pick the quote style that does not occur in the value.</summary>
    public static string Literal(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Filter values cannot be blank.");
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("Filter values cannot contain control characters.");
        }

        bool single = value.Contains('\'', StringComparison.Ordinal);
        bool dbl = value.Contains('"', StringComparison.Ordinal);
        if (single && dbl)
        {
            throw new ArgumentException("Filter values cannot contain both single and double quotes.");
        }

        return single ? "\"" + value + "\"" : "'" + value + "'";
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
