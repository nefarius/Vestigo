using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Vestigo.Configuration;

namespace Vestigo.Hosting;

internal sealed record ConfigurationFieldError(int Index, string Target, string Message);

internal sealed class NamedRootDraft
{
    public Guid UiKey { get; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public List<string> Access { get; set; } = [];
    public JsonObject Prototype { get; set; } = new();
    public bool Structured { get; set; } = true;
}

internal sealed class StringEntryDraft
{
    public Guid UiKey { get; } = Guid.NewGuid();
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Structured { get; set; } = true;
}

/// <summary>Converts the configuration page's quantity, flag, root, and dictionary editors to JSON values.</summary>
internal static class ConfigurationQuantity
{
    public const string WholeBytesError = "Enter a value that converts to a whole number of bytes.";
    public const string ByteFitError = "That size does not fit in this setting.";
    public const string PositiveSizeError = "Enter zero or a positive size.";
    public const string ByteNumberError = "Enter a whole number of bytes.";
    public const string NumberParseError = "Enter a number.";
    public const string PositiveDurationError = "Enter zero or a positive duration.";
    public const string DurationTickError = "Enter a duration that lands on a whole tick.";
    public const string DurationFitError = "That duration does not fit.";
    public const string DurationTextError = "Enter a duration.";
    public const string RootNameError = "Use at least two characters: start with a letter, then letters, digits, '_' or '-'.";
    public const string RootDuplicateError = "This name is already used.";
    public const string RootPathError = "Enter an absolute path.";
    public const string RootAccessError = "Choose at least one of Read, Write, or Execute.";
    public const string RootShapeError = "This entry is not an object. Remove it to continue.";
    public const string EntryNameError = "Enter a name.";
    public const string DuplicateNameError = "This name is already used.";
    public const string EntryValueError = "Enter a value.";
    public const string Sha256Error = "Enter a 64-digit SHA-256 hexadecimal digest.";
    public const string EntryShapeError = "This value must be text.";

    public static readonly string[] ByteUnits = ["B", "KiB", "MiB", "GiB"];
    public static readonly string[] DurationUnits = ["ms", "seconds", "minutes", "hours", "days"];

    private static readonly (string Unit, decimal Factor)[] ByteScales =
    [
        ("GiB", 1024m * 1024m * 1024m),
        ("MiB", 1024m * 1024m),
        ("KiB", 1024m),
        ("B", 1m)
    ];

    private static readonly (string Unit, long Ticks)[] DurationScales =
    [
        ("days", TimeSpan.TicksPerDay),
        ("hours", TimeSpan.TicksPerHour),
        ("minutes", TimeSpan.TicksPerMinute),
        ("seconds", TimeSpan.TicksPerSecond),
        ("ms", TimeSpan.TicksPerMillisecond)
    ];

    public static string DurationUnitLabel(string unit) => unit switch
    {
        "ms" => "Milliseconds",
        "seconds" => "Seconds",
        "minutes" => "Minutes",
        "hours" => "Hours",
        "days" => "Days",
        _ => unit
    };

    public static bool TryFormatByteCount(JsonNode? node, out decimal amount, out string unit)
    {
        amount = 0;
        unit = "B";
        if (!TryReadDecimal(node, out decimal bytes) || bytes != decimal.Truncate(bytes)) return false;
        decimal absolute = decimal.Abs(bytes);
        foreach ((string name, decimal factor) in ByteScales)
        {
            if (factor != 1 && (absolute < factor || absolute % factor != 0)) continue;
            amount = bytes / factor;
            unit = name;
            return true;
        }
        amount = bytes;
        return true;
    }

    public static bool TryConvertByteCount(decimal amount, string unit, Type integerType, out JsonValue? value, out string? error)
    {
        value = null;
        if (!TryByteFactor(unit, out decimal factor))
        {
            error = "Choose a size unit.";
            return false;
        }
        if (amount < 0)
        {
            error = PositiveSizeError;
            return false;
        }
        decimal bytes;
        try { bytes = amount * factor; }
        catch (OverflowException)
        {
            error = ByteFitError;
            return false;
        }
        if (bytes != decimal.Truncate(bytes))
        {
            error = WholeBytesError;
            return false;
        }
        if (!Fits(integerType, bytes))
        {
            error = ByteFitError;
            return false;
        }
        value = CreateInteger(bytes, integerType);
        error = null;
        return true;
    }

    public static decimal ReadNumber(JsonNode node) =>
        TryReadDecimal(node, out decimal value) ? value : throw new InvalidOperationException("Expected a JSON number.");

    public static string ByteSummary(decimal bytes) =>
        $"{bytes.ToString("N0", CultureInfo.CurrentCulture)} bytes. KiB, MiB, and GiB are powers of 1024.";

    public static bool TryFormatDuration(string? text, out decimal amount, out string unit)
    {
        amount = 0;
        unit = "seconds";
        if (string.IsNullOrWhiteSpace(text) || !TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan value))
            return false;
        long ticks = value.Ticks;
        if (ticks == 0) return true;
        foreach ((string name, long per) in DurationScales)
        {
            if (ticks % per != 0) continue;
            long scaled = ticks / per;
            if (scaled == 0) continue;
            amount = scaled;
            unit = name;
            return true;
        }
        amount = ticks / (decimal)TimeSpan.TicksPerMillisecond;
        unit = "ms";
        return true;
    }

    public static bool TryConvertDuration(decimal amount, string unit, out string? canonical, out string? error)
    {
        canonical = null;
        if (!TryDurationFactor(unit, out decimal ticksPerUnit))
        {
            error = "Choose a duration unit.";
            return false;
        }
        if (amount < 0)
        {
            error = PositiveDurationError;
            return false;
        }
        decimal ticks;
        try { ticks = amount * ticksPerUnit; }
        catch (OverflowException)
        {
            error = DurationFitError;
            return false;
        }
        if (ticks != decimal.Truncate(ticks))
        {
            error = DurationTickError;
            return false;
        }
        if (ticks > long.MaxValue || ticks < long.MinValue)
        {
            error = DurationFitError;
            return false;
        }
        canonical = ToCanonical(TimeSpan.FromTicks((long)ticks));
        error = null;
        return true;
    }

    public static string ToCanonical(TimeSpan value)
    {
        JsonNode? node = JsonSerializer.SerializeToNode(value, ConfigurationEditor.JsonOptions);
        return node is JsonValue json && json.TryGetValue<string>(out string? text) && text is not null
            ? text
            : value.ToString("c", CultureInfo.InvariantCulture);
    }

    public static IReadOnlyList<string> FlagChoices(Type enumType) => Enum.GetNames(enumType)
        .Where(name => SingleBit(FlagValue(enumType, name)))
        .ToArray();

    public static bool TryReadFlags(Type enumType, string? text, out IReadOnlyList<string> selected, out string? error)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            selected = [];
            error = null;
            return true;
        }
        if (!Enum.TryParse(enumType, text, ignoreCase: true, out object? parsed))
        {
            selected = [];
            error = "Choose one or more listed values.";
            return false;
        }
        long bits = Convert.ToInt64(parsed, CultureInfo.InvariantCulture);
        selected = FlagChoices(enumType).Where(name =>
        {
            long bit = FlagValue(enumType, name);
            return bit != 0 && (bits & bit) == bit;
        }).ToArray();
        error = null;
        return true;
    }

    public static string FormatFlags(Type enumType, IEnumerable<string> selected)
    {
        HashSet<string> chosen = new(selected, StringComparer.OrdinalIgnoreCase);
        string[] names = FlagChoices(enumType).Where(chosen.Contains).ToArray();
        if (names.Length > 0) return string.Join(", ", names);
        return Enum.GetName(enumType, Enum.ToObject(enumType, 0)) ?? "None";
    }

    public static List<NamedRootDraft> ReadNamedRoots(JsonArray roots)
    {
        List<NamedRootDraft> drafts = [];
        foreach (JsonNode? item in roots)
        {
            if (item is not JsonObject source)
            {
                drafts.Add(new NamedRootDraft { Structured = false });
                continue;
            }
            JsonObject prototype = source.DeepClone().AsObject();
            TryReadFlags(typeof(RootAccess), ReadString(prototype["Access"]), out IReadOnlyList<string> access, out _);
            drafts.Add(new NamedRootDraft
            {
                Name = ReadString(prototype["Name"]),
                Path = ReadString(prototype["Path"]),
                Access = access.ToList(),
                Prototype = prototype
            });
        }
        return drafts;
    }

    public static bool TryCreateNamedRoots(IReadOnlyList<NamedRootDraft> drafts, out JsonArray? roots, out IReadOnlyList<ConfigurationFieldError> errors)
    {
        List<ConfigurationFieldError> found = [];
        JsonArray array = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < drafts.Count; index++)
        {
            NamedRootDraft draft = drafts[index];
            if (!draft.Structured)
            {
                found.Add(new(index, "entry", RootShapeError));
                continue;
            }
            if (!Regex.IsMatch(draft.Name, "^[A-Za-z][A-Za-z0-9_-]+$"))
                found.Add(new(index, "name", RootNameError));
            else if (!names.Add(draft.Name))
                found.Add(new(index, "name", RootDuplicateError));
            if (string.IsNullOrWhiteSpace(draft.Path) || !Path.IsPathFullyQualified(draft.Path))
                found.Add(new(index, "path", RootPathError));
            if (draft.Access.Count == 0)
                found.Add(new(index, "access", RootAccessError));
            JsonObject clone = draft.Prototype.DeepClone().AsObject();
            clone["Name"] = draft.Name;
            clone["Path"] = draft.Path;
            clone["Access"] = FormatFlags(typeof(RootAccess), draft.Access);
            array.Add(clone);
        }
        errors = found;
        roots = found.Count == 0 ? array : null;
        return found.Count == 0;
    }

    public static List<StringEntryDraft> ReadStringDictionary(JsonObject source)
    {
        List<StringEntryDraft> entries = [];
        foreach ((string key, JsonNode? node) in source)
        {
            string text;
            bool structured;
            if (node is JsonValue json && TryGetString(json, out string? read))
            {
                structured = true;
                text = read ?? "";
            }
            else
            {
                structured = false;
                text = node?.ToJsonString() ?? "";
            }
            entries.Add(new StringEntryDraft { Key = key, Value = text, Structured = structured });
        }
        return entries;
    }

    public static bool TryCreateStringDictionary(IReadOnlyList<StringEntryDraft> entries, bool requireSha256, out JsonObject? value, out IReadOnlyList<ConfigurationFieldError> errors)
    {
        List<ConfigurationFieldError> found = [];
        JsonObject result = new();
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < entries.Count; index++)
        {
            StringEntryDraft entry = entries[index];
            if (string.IsNullOrWhiteSpace(entry.Key))
                found.Add(new(index, "key", EntryNameError));
            else if (!names.Add(entry.Key))
                found.Add(new(index, "key", DuplicateNameError));
            if (!entry.Structured)
                found.Add(new(index, "value", EntryShapeError));
            else if (requireSha256)
            {
                string hash = entry.Value.Trim();
                if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                    found.Add(new(index, "value", Sha256Error));
                else entry.Value = hash;
            }
            else if (string.IsNullOrEmpty(entry.Value))
                found.Add(new(index, "value", EntryValueError));
            result[entry.Key] = entry.Value;
        }
        errors = found;
        value = found.Count == 0 ? result : null;
        return found.Count == 0;
    }

    private static bool TryByteFactor(string unit, out decimal factor)
    {
        foreach ((string name, decimal scale) in ByteScales)
        {
            if (!string.Equals(name, unit, StringComparison.Ordinal)) continue;
            factor = scale;
            return true;
        }
        factor = 0;
        return false;
    }

    private static bool TryDurationFactor(string unit, out decimal ticks)
    {
        foreach ((string name, long per) in DurationScales)
        {
            if (!string.Equals(name, unit, StringComparison.Ordinal)) continue;
            ticks = per;
            return true;
        }
        ticks = 0;
        return false;
    }

    private static bool Fits(Type type, decimal value)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(byte)) return value <= byte.MaxValue;
        if (type == typeof(sbyte)) return value <= sbyte.MaxValue;
        if (type == typeof(short)) return value <= short.MaxValue;
        if (type == typeof(ushort)) return value <= ushort.MaxValue;
        if (type == typeof(int)) return value <= int.MaxValue;
        if (type == typeof(uint)) return value <= uint.MaxValue;
        if (type == typeof(long)) return value <= long.MaxValue;
        if (type == typeof(ulong)) return value <= ulong.MaxValue;
        return false;
    }

    private static JsonValue CreateInteger(decimal value, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        checked
        {
            if (type == typeof(byte)) return JsonValue.Create((byte)value)!;
            if (type == typeof(sbyte)) return JsonValue.Create((sbyte)value)!;
            if (type == typeof(short)) return JsonValue.Create((short)value)!;
            if (type == typeof(ushort)) return JsonValue.Create((ushort)value)!;
            if (type == typeof(int)) return JsonValue.Create((int)value)!;
            if (type == typeof(uint)) return JsonValue.Create((uint)value)!;
            if (type == typeof(long)) return JsonValue.Create((long)value)!;
            if (type == typeof(ulong)) return JsonValue.Create((ulong)value)!;
        }
        throw new ArgumentOutOfRangeException(nameof(type), type, "Expected an integer byte-count type.");
    }

    private static bool TryReadDecimal(JsonNode? node, out decimal value)
    {
        value = 0;
        if (node is not JsonValue json || json.GetValueKind() != JsonValueKind.Number) return false;
        if (decimal.TryParse(json.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        if (TryGet(json, out long signed)) { value = signed; return true; }
        if (TryGet(json, out ulong unsigned)) { value = unsigned; return true; }
        return false;
    }

    private static bool TryGet<T>(JsonValue json, out T value) where T : struct
    {
        try { return json.TryGetValue(out value!); }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
        {
            value = default;
            return false;
        }
    }

    private static bool TryGetString(JsonValue json, out string? value)
    {
        try { return json.TryGetValue(out value); }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException)
        {
            value = null;
            return false;
        }
    }

    private static string ReadString(JsonNode? node) =>
        node is JsonValue value && TryGetString(value, out string? text) ? text ?? "" : "";

    private static long FlagValue(Type enumType, string name) =>
        Convert.ToInt64(Enum.Parse(enumType, name), CultureInfo.InvariantCulture);

    private static bool SingleBit(long bit) => bit > 0 && (bit & (bit - 1)) == 0;
}
