using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vestigo.Performance;

/// <summary>Allow-listed pktmon.exe argument lists and drop-counter parsing. Callers never pass free-form arguments.</summary>
public static class PktmonPlanner
{
    public const string MetadataFlags = "0x00A";
    private const uint RawPacketFlag = 0x010;

    public static IReadOnlyList<string> StartArguments(string etlPath) =>
        ["start", "--capture", "--type", "drop", "--comp", "all", "--flags", MetadataFlags, "--file-name", etlPath];

    public static IReadOnlyList<string> StopArguments() => ["stop"];

    public static IReadOnlyList<string> StatusArguments() => ["status"];

    public static IReadOnlyList<string> CountersArguments() => ["counters", "--type", "drop", "--json"];

    public static IReadOnlyList<string> FormatArguments(string etlPath, string textPath) =>
        ["format", etlPath, "-o", textPath];

    public static bool IsDropOnlyStart(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 8 || arguments[0] != "start" || !arguments.Contains("--capture"))
        {
            return false;
        }

        if (arguments.Contains("--drop-only") || !TryFlag(arguments, "--type", out string? type) ||
            !string.Equals(type, "drop", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TryFlag(arguments, "--pkt-size", out string? size) && size == "0")
        {
            return false;
        }

        return TryFlag(arguments, "--flags", out string? flags) && TryParseFlags(flags, out uint mask) &&
               (mask & RawPacketFlag) == 0;
    }

    public static bool IndicatesIdle(string output) =>
        output.Contains("not running", StringComparison.OrdinalIgnoreCase)
        || output.Contains("is not started", StringComparison.OrdinalIgnoreCase)
        || output.Contains("No packet monitor", StringComparison.OrdinalIgnoreCase);

    public static bool IndicatesActive(string output) =>
        output.Contains("running", StringComparison.OrdinalIgnoreCase)
        && !output.Contains("not running", StringComparison.OrdinalIgnoreCase);

    public static bool IndicatesSessionInUse(string output) =>
        output.Contains("already running", StringComparison.OrdinalIgnoreCase)
        || output.Contains("already started", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<PacketDropCounter> ParseCounters(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        string trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                return ParseCounterJson(trimmed);
            }
            catch (JsonException)
            {
            }
        }

        return ParseCounterTable(text);
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
            Match match = Regex.Match(
                line,
                @"DropReason\s*:?\s*(.+?)(?=\s*,\s*DropLocation\b|$)",
                RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            string reason = match.Groups[1].Value.Trim().TrimEnd(',');
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

    private static IReadOnlyList<PacketDropCounter> ParseCounterJson(string text)
    {
        JsonNode? root = JsonNode.Parse(text);
        List<PacketDropCounter> counters = [];
        CollectJson(root, counters, null);
        return counters;
    }

    private static void CollectJson(JsonNode? node, List<PacketDropCounter> counters, string? parentName)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (JsonNode? child in array)
                {
                    CollectJson(child, counters, parentName);
                }

                break;
            case JsonObject obj:
                string? name = String(obj, "Name") ?? String(obj, "Component") ?? parentName;
                if (obj["Components"] is JsonNode components)
                {
                    CollectJson(components, counters, name);
                }

                if (obj["Drop"] is JsonObject drop)
                {
                    AddDirection(counters, name, drop, "In", "Rx");
                    AddDirection(counters, name, drop, "Out", "Tx");
                    AddDirection(counters, name, drop, "Rx", "Rx");
                    AddDirection(counters, name, drop, "Tx", "Tx");
                }

                if (IsDropCounter(obj) && Packets(obj) is long packets)
                {
                    counters.Add(new PacketDropCounter(
                        name ?? "component",
                        String(obj, "Dir") ?? String(obj, "Direction"),
                        0,
                        packets,
                        String(obj, "Reason") ?? String(obj, "DropReason") ?? String(obj, "Drop reason")));
                }

                if (obj["Counters"] is JsonNode nested)
                {
                    CollectJson(nested, counters, name);
                }

                break;
        }
    }

    private static void AddDirection(List<PacketDropCounter> counters, string? name, JsonObject drop, string key, string direction)
    {
        if (drop[key] is not JsonObject row || Packets(row) is not long packets || packets <= 0)
        {
            return;
        }

        counters.Add(new PacketDropCounter(
            name ?? "component",
            direction,
            0,
            packets,
            String(row, "Reason") ?? String(row, "DropReason") ?? String(drop, "Reason")));
    }

    private static bool IsDropCounter(JsonObject obj)
    {
        string? type = String(obj, "Type") ?? String(obj, "CounterType");
        return type is not null && type.Contains("drop", StringComparison.OrdinalIgnoreCase);
    }

    private static long? Packets(JsonObject obj) =>
        Number(obj, "Packets") ?? Number(obj, "Dropped") ?? Number(obj, "Count");

    private static IReadOnlyList<PacketDropCounter> ParseCounterTable(string text)
    {
        List<PacketDropCounter> counters = [];
        foreach (string raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('-') || line.StartsWith("Id", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Name", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Component", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Logger", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Source", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Match match = Regex.Match(line, @"^(?:\d+\s+)?(.+?)\s+(Rx|Tx)\s+(\d+)(?:\s+(\d+))?(?:\s+(.+))?$", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                long dropped = long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                string? reason = match.Groups[5].Success ? match.Groups[5].Value.Trim() : null;
                if (string.IsNullOrWhiteSpace(reason))
                {
                    reason = null;
                }

                counters.Add(new PacketDropCounter(match.Groups[1].Value.Trim(), match.Groups[2].Value, 0, dropped, reason));
                continue;
            }

            string[] parts = Regex.Split(line, @"\s+");
            if (parts.Length < 2 || !TryLastNumbers(parts, out long tableDropped, out long received, out int numberCount))
            {
                continue;
            }

            string component = string.Join(' ', parts.Take(Math.Max(1, parts.Length - numberCount)));
            if (component.Length == 0 || component.Contains("Direction", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            counters.Add(new PacketDropCounter(component, null, received, tableDropped, null));
        }

        return counters;
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

    private static bool TryFlag(IReadOnlyList<string> arguments, string name, out string? value)
    {
        int index = arguments.ToList().IndexOf(name);
        if (index < 0 || index + 1 >= arguments.Count)
        {
            value = null;
            return false;
        }

        value = arguments[index + 1];
        return true;
    }

    private static bool TryParseFlags(string? text, out uint mask)
    {
        mask = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mask);
    }

    private static string? String(JsonObject obj, string name)
    {
        JsonNode? node = obj[name];
        if (node is null)
        {
            return null;
        }

        try
        {
            string? text = node.GetValue<string>();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (InvalidOperationException)
        {
            string text = node.ToString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }

    private static long? Number(JsonObject obj, string name)
    {
        JsonNode? node = obj[name];
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<long>();
        }
        catch (InvalidOperationException)
        {
            return long.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
                ? value
                : null;
        }
    }
}
