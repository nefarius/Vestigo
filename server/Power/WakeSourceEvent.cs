using System.Globalization;
using System.Xml.Linq;

namespace Vestigo.Power;

/// <summary>The newest <c>Microsoft-Windows-Power-Troubleshooter</c> event 1, or an empty result.</summary>
/// <param name="EventFound">False when the System log has no matching event. Other fields are then null.</param>
/// <param name="WakeTimeUtc">The event's <c>WakeTime</c>, or null when it is absent or not a timestamp.</param>
/// <param name="SleepTimeUtc">The event's <c>SleepTime</c>, or null when it is absent or not a timestamp.</param>
/// <param name="WakeSourceType">The event's <c>WakeSourceType</c> number. Windows does not publish a name for this number.</param>
/// <param name="WakeSourceText">The provider's wake-source text, bounded, or null when the event has none.</param>
/// <param name="WakeTimerOwner">The wake-timer owner, bounded, or null when the event has none.</param>
/// <param name="WakeTimerContext">The wake-timer context, bounded, or null when the event has none.</param>
/// <param name="EventRecordId">System log record id, or null.</param>
/// <param name="TextTruncated">True when a text field was longer than <see cref="WakeSourceEvent.MaxTextChars" />.</param>
public sealed record WakeSourceResult(
    bool EventFound,
    DateTimeOffset? WakeTimeUtc,
    DateTimeOffset? SleepTimeUtc,
    int? WakeSourceType,
    string? WakeSourceText,
    string? WakeTimerOwner,
    string? WakeTimerContext,
    long? EventRecordId,
    bool TextTruncated)
{
    /// <summary>No matching event was available.</summary>
    public static WakeSourceResult None() => new(false, null, null, null, null, null, null, null, false);
}

/// <summary>Reads structured fields from a rendered Power-Troubleshooter event. Unknown numbers stay numeric.</summary>
public static class WakeSourceEvent
{
    /// <summary>Maximum characters returned for one text field.</summary>
    public const int MaxTextChars = 512;

    private static readonly XNamespace EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    /// <summary>Parses one event XML document. Malformed XML and any other event produce <see cref="WakeSourceResult.None" />.</summary>
    public static WakeSourceResult Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return WakeSourceResult.None();
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return WakeSourceResult.None();
        }

        XElement? root = document.Root;
        XElement? system = root?.Element(EventNamespace + "System");
        string? provider = system?.Element(EventNamespace + "Provider")?.Attribute("Name")?.Value;
        string? eventId = system?.Element(EventNamespace + "EventID")?.Value?.Trim();
        if (root?.Name != EventNamespace + "Event"
            || !string.Equals(provider, "Microsoft-Windows-Power-Troubleshooter", StringComparison.Ordinal)
            || eventId != "1")
        {
            return WakeSourceResult.None();
        }

        Dictionary<string, string> data = new(StringComparer.Ordinal);
        foreach (XElement item in root.Element(EventNamespace + "EventData")?.Elements(EventNamespace + "Data") ?? [])
        {
            string? name = item.Attribute("Name")?.Value;
            if (!string.IsNullOrEmpty(name))
            {
                data[name] = item.Value;
            }
        }

        bool truncated = false;
        return new WakeSourceResult(
            true,
            Time(data, "WakeTime"),
            Time(data, "SleepTime"),
            Number(data, "WakeSourceType"),
            Text(data, "WakeSourceText", ref truncated),
            Text(data, "WakeTimerOwner", ref truncated),
            Text(data, "WakeTimerContext", ref truncated),
            Record(system),
            truncated);
    }

    private static DateTimeOffset? Time(Dictionary<string, string> data, string name)
    {
        if (!data.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed)
            ? parsed
            : null;
    }

    private static int? Number(Dictionary<string, string> data, string name)
    {
        if (!data.TryGetValue(name, out string? value))
        {
            return null;
        }

        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }

    private static string? Text(Dictionary<string, string> data, string name, ref bool truncated)
    {
        if (!data.TryGetValue(name, out string? value))
        {
            return null;
        }

        value = value.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (value.Length <= MaxTextChars)
        {
            return value;
        }

        truncated = true;
        return value[..MaxTextChars];
    }

    private static long? Record(XElement? system)
    {
        string? value = system?.Element(EventNamespace + "EventRecordID")?.Value;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : null;
    }
}
