using System.Globalization;
using System.Text.Json;

namespace Vestigo.Etws;

/// <summary>Reads one UTF-8 JSON event emitted by the ETW library.</summary>
public static class PresentationEventParser
{
    private static readonly string[] ModeNames = ["PresentationMode", "PresentMode", "FlipMode"];

    private static readonly string[] WindowNames = ["hWnd", "Hwnd", "Window", "WindowHandle"];

    private static readonly HashSet<string> ModeValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "composed", "windowed", "composed-windowed", "composed_flip",
        "independent-flip", "independent_flip", "independentflip",
        "exclusive", "exclusive-fullscreen", "exclusive_fullscreen", "hardware_legacy_flip"
    };

    public static bool TryParse(ReadOnlySpan<byte> utf8, out PresentationSample sample)
    {
        sample = null!;
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8.ToArray());
            Collected collected = new();
            Walk(document.RootElement, collected, insideProperties: false);
            sample = new PresentationSample(
                collected.Provider,
                collected.EventId,
                collected.Task,
                collected.ProcessId,
                collected.AttributedProcessId,
                collected.WindowHandle,
                Normalize(collected.ExplicitMode),
                collected.Dropped);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Walk(JsonElement element, Collected collected, bool insideProperties)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("Name", out JsonElement nameElement)
                    && nameElement.ValueKind == JsonValueKind.String
                    && element.TryGetProperty("Value", out JsonElement valueElement))
                {
                    Assign(nameElement.GetString()!, valueElement, collected);
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    bool nested = insideProperties || property.NameEquals("Properties");
                    if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        Walk(property.Value, collected, nested);
                    }
                    else if (!insideProperties || property.Name is "Name" or "Value")
                    {
                        Assign(property.Name, property.Value, collected);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Walk(item, collected, insideProperties);
                }

                break;
        }
    }

    private static void Assign(string name, JsonElement value, Collected collected)
    {
        if (name.Equals("ProcessId", StringComparison.OrdinalIgnoreCase))
        {
            collected.ProcessId ??= ReadInt(value);
        }
        else if (name.Equals("AttributedProcessId", StringComparison.OrdinalIgnoreCase))
        {
            collected.AttributedProcessId ??= ReadInt(value);
        }
        else if (name.Equals("Id", StringComparison.OrdinalIgnoreCase) || name.Equals("EventId", StringComparison.OrdinalIgnoreCase))
        {
            collected.EventId ??= ReadInt(value);
        }
        else if (name.Equals("Task", StringComparison.OrdinalIgnoreCase) || name.Equals("TaskName", StringComparison.OrdinalIgnoreCase))
        {
            string? task = ReadString(value);
            collected.Task ??= task;
            collected.Dropped |= IsLossMarker(task);
        }
        else if (name.Equals("Provider", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("ProviderName", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("ProviderGuid", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("GuidName", StringComparison.OrdinalIgnoreCase))
        {
            collected.Provider ??= ReadString(value);
        }
        else if (name.Equals("Name", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("Opcode", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("OpcodeName", StringComparison.OrdinalIgnoreCase))
        {
            collected.Dropped |= IsLossMarker(ReadString(value));
        }
        else if (name.Equals("EventsLost", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("LostEvents", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("BuffersLost", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("RealTimeBuffersLost", StringComparison.OrdinalIgnoreCase))
        {
            collected.Dropped |= ReadInt(value) is > 0;
        }
        else if (ModeNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            collected.ExplicitMode ??= ReadString(value);
        }
        else if (WindowNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            collected.WindowHandle ??= ReadHandle(value);
        }
    }

    private static string? Normalize(string? mode)
    {
        if (mode is null || !ModeValues.Contains(mode))
        {
            return null;
        }

        if (mode.Contains("independent", StringComparison.OrdinalIgnoreCase))
        {
            return PresentationProviders.IndependentFlip;
        }

        if (mode.Contains("exclusive", StringComparison.OrdinalIgnoreCase)
            || mode.Equals("hardware_legacy_flip", StringComparison.OrdinalIgnoreCase))
        {
            return PresentationProviders.Exclusive;
        }

        return PresentationProviders.Composed;
    }

    private static int? ReadInt(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out int number) => number,
        JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
        _ => null
    };

    private static long? ReadHandle(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
        {
            return number;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string text = value.GetString() ?? "";
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
        {
            return hex;
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : null;
    }

    private static bool IsLossMarker(string? text) =>
        text is not null
        && (text.Contains("RTLostEvent", StringComparison.OrdinalIgnoreCase)
            || text.Contains("RTLostBuffer", StringComparison.OrdinalIgnoreCase));

    private static string? ReadString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };

    private sealed class Collected
    {
        public string? Provider { get; set; }

        public int? EventId { get; set; }

        public string? Task { get; set; }

        public int? ProcessId { get; set; }

        public int? AttributedProcessId { get; set; }

        public long? WindowHandle { get; set; }

        public string? ExplicitMode { get; set; }

        public bool Dropped { get; set; }
    }
}
