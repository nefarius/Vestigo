using System.Globalization;
using System.Text.RegularExpressions;

namespace Vestigo.Performance;

/// <summary>Allow-listed pktmon.exe argument lists and drop-counter parsing. Callers never pass free-form arguments.</summary>
public static class PktmonPlanner
{
    public static IReadOnlyList<string> StartArguments(string etlPath) =>
        ["start", "--capture", "--pkt-size", "0", "--drop-only", "--comp", "all", "-f", etlPath];

    public static IReadOnlyList<string> StopArguments() => ["stop"];

    public static IReadOnlyList<string> CountersArguments() => ["counters"];

    public static IReadOnlyList<string> FormatArguments(string etlPath, string textPath) =>
        ["format", etlPath, "-o", textPath];

    public static bool IsDropOnlyStart(IReadOnlyList<string> arguments) =>
        arguments.Count >= 8
        && arguments[0] == "start"
        && arguments.Contains("--drop-only")
        && arguments.Contains("--pkt-size")
        && arguments[arguments.ToList().IndexOf("--pkt-size") + 1] == "0";

    public static IReadOnlyList<PacketDropCounter> ParseCounters(string text)
    {
        List<PacketDropCounter> counters = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return counters;
        }

        foreach (string raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('-') || line.StartsWith("Name", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Component", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Source", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Id", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Logger", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = Regex.Split(line, @"\s+");
            if (parts.Length < 2)
            {
                continue;
            }

            if (!TryLastNumbers(parts, out long dropped, out long received, out int numberCount))
            {
                continue;
            }

            string component = string.Join(' ', parts.Take(Math.Max(1, parts.Length - numberCount)));
            if (component.Length == 0)
            {
                continue;
            }

            counters.Add(new PacketDropCounter(component, null, received, dropped, null));
        }

        return counters;
    }

    public static IReadOnlyList<PacketDropCounter> ParseDropReasons(string text)
    {
        Dictionary<string, long> counts = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        foreach (string raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            int index = line.IndexOf("Drop Reason", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                index = line.IndexOf("Drop:", StringComparison.OrdinalIgnoreCase);
            }

            if (index < 0)
            {
                continue;
            }

            string reason = line[(index + (line.IndexOf("Drop Reason", StringComparison.OrdinalIgnoreCase) >= 0 ? "Drop Reason".Length : "Drop:".Length))..];
            reason = reason.Trim(' ', ':', '\t');
            if (reason.Length == 0)
            {
                continue;
            }

            counts.TryGetValue(reason, out long count);
            counts[reason] = count + 1;
        }

        return counts.Select(pair => new PacketDropCounter("drop-reason", "reason", 0, pair.Value, pair.Key)).ToList();
    }

    public static PacketDropResult Combine(int durationMs, IReadOnlyList<PacketDropCounter> counters, IReadOnlyList<PacketDropCounter> reasons, bool truncated, bool partial, string? error)
    {
        List<PacketDropCounter> all = [.. counters, .. reasons];
        long dropped = counters.Count > 0
            ? counters.Sum(item => item.Dropped)
            : reasons.Sum(item => item.Dropped);
        return new PacketDropResult(durationMs, all, dropped, truncated, partial, error);
    }

    private static bool TryLastNumbers(string[] parts, out long dropped, out long received, out int numberCount)
    {
        dropped = 0;
        received = 0;
        numberCount = 0;
        List<long> numbers = [];
        for (int index = parts.Length - 1; index >= 0; index--)
        {
            if (!long.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            {
                break;
            }

            numbers.Insert(0, value);
        }

        if (numbers.Count < 1)
        {
            return false;
        }

        numberCount = numbers.Count;
        if (numbers.Count == 1)
        {
            dropped = numbers[0];
            return true;
        }

        received = numbers[^2];
        dropped = numbers[^1];
        return true;
    }
}
