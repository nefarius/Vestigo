using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using Vestigo.Configuration;
using Vestigo.Hosting;
using Vestigo.Hosting.Components;

namespace Vestigo.Server.Tests;

public class ConfigurationQuantityTests
{
    [Theory]
    [InlineData("None", "None")]
    [InlineData("Read", "Read")]
    [InlineData("SystemDrive", "System Drive")]
    [InlineData("FixedVolumes", "Fixed Volumes")]
    [InlineData("AllVolumes", "All Volumes")]
    [InlineData("AllVolumesLive", "All Volumes Live")]
    [InlineData("AdministratorManaged", "Administrator Managed")]
    [InlineData("PinnedSha256", "Pinned Sha 256")]
    [InlineData("QueryPerformanceCounter", "Query Performance Counter")]
    public void EnumLabelsInsertSpacesAndKeepAcronymBoundaries(string name, string label) =>
        Assert.Equal(label, ConfigurationFieldMetadata.EnumDisplayLabel(name));

    [Fact]
    public void MetadataClassifiesBytesDurationsEnumsRootsAndDictionaries()
    {
        List<(string Path, Type Type, string Name)> properties = [];
        Collect(typeof(VestigoOptions), "", properties);
        Assert.Contains(properties, item => item.Path == "MaxInputBytes");
        Assert.Contains(properties, item => item.Path == "Diagnostics:MaxDumpBytes");
        Assert.Contains(properties, item => item.Path == "JobRetention");
        Assert.Contains(properties, item => item.Path == "Files:TicketLifetime");
        foreach ((string path, Type type, string name) in properties)
        {
            bool integral = type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
                            type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);
            bool byteCount = name.EndsWith("Bytes", StringComparison.OrdinalIgnoreCase) && integral;
            Assert.Equal(byteCount, ConfigurationFieldMetadata.IsByteCount(path, out Type integerType));
            if (byteCount) Assert.Equal(type, integerType);
            Assert.Equal(type == typeof(TimeSpan), ConfigurationFieldMetadata.IsDuration(path));
        }

        Assert.False(ConfigurationFieldMetadata.IsByteCount("BufferSizeKb", out _));
        Assert.True(ConfigurationFieldMetadata.IsNamedRoots("Roots"));
        Assert.True(ConfigurationFieldMetadata.IsStringDictionary("Plugins:PackageHashes"));
        Assert.False(ConfigurationFieldMetadata.IsStringDictionary("Modules"));
        Assert.True(ConfigurationFieldMetadata.TryGetEnum("VolumeRoots", out Type volumeRoots, out bool volumeFlags));
        Assert.Equal(typeof(VolumeRootMode), volumeRoots);
        Assert.False(volumeFlags);
        Assert.True(ConfigurationFieldMetadata.TryGetEnum("VolumeRootAccess", out Type access, out bool accessFlags));
        Assert.Equal(typeof(RootAccess), access);
        Assert.True(accessFlags);
    }

    [Fact]
    public void UnknownStringFieldIsNotAnEnumOrDuration()
    {
        const string path = "ExamplePlugin:Note";
        Assert.Null(ConfigurationFieldMetadata.TypeFor(path));
        Assert.False(ConfigurationFieldMetadata.IsDuration(path));
        Assert.False(ConfigurationFieldMetadata.TryGetEnum(path, out _, out _));
        Assert.False(ConfigQuantityInput.TryAcceptParsedAmount(conversionFailed: true, converted: 0m, out _));
        Assert.False(ConfigQuantityInput.TryAcceptParsedAmount(conversionFailed: false, converted: null, out _));
        Assert.True(ConfigQuantityInput.TryAcceptParsedAmount(conversionFailed: false, converted: 1.5m, out decimal amount));
        Assert.Equal(1.5m, amount);
        Assert.True(ConfigQuantityInput.NotifyUnchangedExplicitValue(wasRejected: true, requested: 5m, current: 5m));
        Assert.False(ConfigQuantityInput.NotifyUnchangedExplicitValue(wasRejected: true, requested: 6m, current: 5m));
        Assert.False(ConfigQuantityInput.NotifyUnchangedExplicitValue(wasRejected: false, requested: 5m, current: 5m));
    }

    [Theory]
    [InlineData(0, "0", "B")]
    [InlineData(64, "64", "B")]
    [InlineData(1024, "1", "KiB")]
    [InlineData(65536, "64", "KiB")]
    [InlineData(67108864, "64", "MiB")]
    [InlineData(536870912, "512", "MiB")]
    [InlineData(4294967296, "4", "GiB")]
    [InlineData(17179869184, "16", "GiB")]
    public void ByteCountsUseTheLargestExactBinaryUnit(long bytes, string amount, string unit)
    {
        JsonNode? node = JsonSerializer.SerializeToNode(bytes);
        Assert.True(ConfigurationQuantity.TryFormatByteCount(node, out decimal formatted, out string formattedUnit));
        Assert.Equal(decimal.Parse(amount, CultureInfo.InvariantCulture), formatted);
        Assert.Equal(unit, formattedUnit);
        Assert.True(ConfigurationQuantity.TryConvertByteCount(formatted, formattedUnit, typeof(long), out JsonValue? converted, out string? error), error);
        Assert.Equal((decimal)bytes, ConfigurationQuantity.ReadNumber(converted!));
    }

    [Fact]
    public void ByteConversionRejectsFractionsNegativesAndOverflow()
    {
        Assert.True(ConfigurationQuantity.TryConvertByteCount(1.5m, "MiB", typeof(long), out JsonValue? value, out string? error), error);
        Assert.Equal(1_572_864m, ConfigurationQuantity.ReadNumber(value!));
        Assert.False(ConfigurationQuantity.TryConvertByteCount(0.1m, "KiB", typeof(long), out _, out error));
        Assert.Equal(ConfigurationQuantity.WholeBytesError, error);
        Assert.False(ConfigurationQuantity.TryConvertByteCount(-1, "B", typeof(long), out _, out error));
        Assert.Equal(ConfigurationQuantity.PositiveSizeError, error);
        Assert.False(ConfigurationQuantity.TryConvertByteCount(3, "GiB", typeof(int), out _, out error));
        Assert.Equal(ConfigurationQuantity.ByteFitError, error);
        Assert.True(ConfigurationQuantity.TryConvertByteCount(3, "GiB", typeof(long), out _, out error), error);
        Assert.False(ConfigurationQuantity.TryFormatByteCount(JsonValue.Create(1.5m), out _, out _));
        Assert.True(ConfigurationQuantity.TryFormatByteCount(JsonValue.Create(64 * 1024), out decimal amount, out string unit));
        Assert.Equal(64m, amount);
        Assert.Equal("KiB", unit);
    }

    [Fact]
    public void DurationsUseExactTicksAndCanonicalText()
    {
        AssertFormat(TimeSpan.Zero, 0, "seconds");
        AssertFormat(TimeSpan.FromSeconds(30), 30, "seconds");
        AssertFormat(TimeSpan.FromMinutes(15), 15, "minutes");
        AssertFormat(TimeSpan.FromHours(1), 1, "hours");
        AssertFormat(TimeSpan.FromHours(24), 1, "days");
        AssertFormat(TimeSpan.FromMilliseconds(100), 100, "ms");
        AssertFormat(TimeSpan.FromTicks(1), 0.0001m, "ms");

        Assert.True(ConfigurationQuantity.TryConvertDuration(1.5m, "hours", out string? hourAndHalf, out string? error), error);
        Assert.Equal(54_000_000_000L, TimeSpan.Parse(hourAndHalf!, CultureInfo.InvariantCulture).Ticks);
        Assert.Equal(ConfigurationQuantity.ToCanonical(TimeSpan.FromTicks(54_000_000_000)), hourAndHalf);
        Assert.True(ConfigurationQuantity.TryFormatDuration(hourAndHalf, out decimal amount, out string unit));
        Assert.True(ConfigurationQuantity.TryConvertDuration(amount, unit, out string? again, out error), error);
        Assert.Equal(hourAndHalf, again);

        Assert.True(ConfigurationQuantity.TryConvertDuration(2.5m, "days", out string? days, out error), error);
        Assert.Equal((long)(2.5m * TimeSpan.TicksPerDay), TimeSpan.Parse(days!, CultureInfo.InvariantCulture).Ticks);
        Assert.True(ConfigurationQuantity.TryConvertDuration(0.0001m, "ms", out string? tick, out error), error);
        Assert.Equal(1, TimeSpan.Parse(tick!, CultureInfo.InvariantCulture).Ticks);
        Assert.False(ConfigurationQuantity.TryConvertDuration(0.00015m, "ms", out _, out error));
        Assert.Equal(ConfigurationQuantity.DurationTickError, error);
        Assert.False(ConfigurationQuantity.TryConvertDuration(-1, "hours", out _, out error));
        Assert.Equal(ConfigurationQuantity.PositiveDurationError, error);
        Assert.False(ConfigurationQuantity.TryConvertDuration(10_675_200m, "days", out _, out error));
        Assert.Equal(ConfigurationQuantity.DurationFitError, error);
        Assert.False(ConfigurationQuantity.TryConvertDuration(decimal.MaxValue, "days", out _, out error));
        Assert.Equal(ConfigurationQuantity.DurationFitError, error);
        Assert.False(ConfigurationQuantity.TryFormatDuration("forever", out _, out _));
        Assert.Equal("Days", ConfigurationQuantity.DurationUnitLabel("days"));
    }

    [Fact]
    public void FlagsKeepEnumOrderAndNamedRootsPreserveUnknownProperties()
    {
        Assert.Equal(["Read", "Write", "Execute"], ConfigurationQuantity.FlagChoices(typeof(RootAccess)));
        Assert.True(ConfigurationQuantity.TryReadFlags(typeof(RootAccess), "Write, Read", out IReadOnlyList<string> selected, out string? error), error);
        Assert.Equal(["Read", "Write"], selected);
        Assert.Equal("Read, Execute", ConfigurationQuantity.FormatFlags(typeof(RootAccess), ["Execute", "Read"]));
        Assert.Equal("None", ConfigurationQuantity.FormatFlags(typeof(RootAccess), []));
        Assert.False(ConfigurationQuantity.TryReadFlags(typeof(RootAccess), "Sideways", out _, out error));
        Assert.Equal("Choose one or more listed values.", error);

        NamedRootDraft root = new()
        {
            Name = "work",
            Path = Path.GetFullPath("."),
            Access = ["Write", "Read"],
            Prototype = new JsonObject { ["PluginHint"] = "kept", ["Name"] = "old" }
        };
        Assert.True(ConfigurationQuantity.TryCreateNamedRoots([root], out JsonArray? roots, out IReadOnlyList<ConfigurationFieldError> errors),
            string.Join("; ", errors.Select(item => item.Message)));
        Assert.Equal("kept", roots![0]!["PluginHint"]!.GetValue<string>());
        Assert.Equal("Read, Write", roots[0]!["Access"]!.GetValue<string>());
        List<NamedRootDraft> read = ConfigurationQuantity.ReadNamedRoots(roots);
        Assert.Equal(["Read", "Write"], read[0].Access);
        Assert.Equal("kept", read[0].Prototype["PluginHint"]!.GetValue<string>());

        root.Name = "1bad";
        Assert.False(ConfigurationQuantity.TryCreateNamedRoots([root], out _, out errors));
        Assert.Equal(ConfigurationQuantity.RootNameError, errors[0].Message);
        root.Name = "work";
        root.Path = "relative";
        Assert.False(ConfigurationQuantity.TryCreateNamedRoots([root], out _, out errors));
        Assert.Contains(errors, item => item.Message == ConfigurationQuantity.RootPathError);
        root.Path = Path.GetFullPath(".");
        root.Access = [];
        Assert.False(ConfigurationQuantity.TryCreateNamedRoots([root], out _, out errors));
        Assert.Contains(errors, item => item.Message == ConfigurationQuantity.RootAccessError);
        NamedRootDraft duplicate = new() { Name = "Work", Path = Path.GetFullPath("."), Access = ["Read"] };
        root.Access = ["Read"];
        Assert.False(ConfigurationQuantity.TryCreateNamedRoots([root, duplicate], out _, out errors));
        Assert.Contains(errors, item => item.Message == ConfigurationQuantity.RootDuplicateError);
    }

    [Fact]
    public void StringDictionariesValidateHashesAndRejectDuplicates()
    {
        string hash = new('A', 64);
        StringEntryDraft entry = new() { Key = "Playwright", Value = " " + hash + "\n" };
        Assert.True(ConfigurationQuantity.TryCreateStringDictionary([entry], true, out JsonObject? hashes, out IReadOnlyList<ConfigurationFieldError> errors),
            string.Join("; ", errors.Select(item => item.Message)));
        Assert.Equal(hash, hashes!["Playwright"]!.GetValue<string>());
        Assert.Equal(hash, entry.Value);
        entry.Value = "zz";
        Assert.False(ConfigurationQuantity.TryCreateStringDictionary([entry], true, out _, out errors));
        Assert.Equal(ConfigurationQuantity.Sha256Error, errors[0].Message);
        StringEntryDraft[] duplicates =
        [
            new() { Key = "Plugin", Value = hash },
            new() { Key = "plugin", Value = hash }
        ];
        Assert.False(ConfigurationQuantity.TryCreateStringDictionary(duplicates, true, out _, out errors));
        Assert.Equal(ConfigurationQuantity.DuplicateNameError, Assert.Single(errors).Message);
        JsonObject source = new() { ["plugin"] = hash.ToLowerInvariant(), ["broken"] = new JsonObject() };
        List<StringEntryDraft> read = ConfigurationQuantity.ReadStringDictionary(source);
        Assert.True(read[0].Structured);
        Assert.False(read[1].Structured);
        Assert.False(ConfigurationQuantity.TryCreateStringDictionary(read, true, out _, out errors));
        Assert.Contains(errors, item => item.Message == ConfigurationQuantity.EntryShapeError);
    }

    private static void AssertFormat(TimeSpan value, decimal amount, string unit)
    {
        string text = ConfigurationQuantity.ToCanonical(value);
        Assert.True(ConfigurationQuantity.TryFormatDuration(text, out decimal formatted, out string formattedUnit));
        Assert.Equal(amount, formatted);
        Assert.Equal(unit, formattedUnit);
        Assert.True(ConfigurationQuantity.TryConvertDuration(formatted, formattedUnit, out string? canonical, out string? error), error);
        Assert.Equal(value.Ticks, TimeSpan.Parse(canonical!, CultureInfo.InvariantCulture).Ticks);
    }

    private static void Collect(Type type, string prefix, List<(string Path, Type Type, string Name)> result)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length > 0) continue;
            string path = prefix.Length == 0 ? property.Name : prefix + ":" + property.Name;
            Type leaf = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            result.Add((path, leaf, property.Name));
            if (leaf.IsPrimitive || leaf.IsEnum || leaf == typeof(string) || leaf == typeof(decimal) || leaf == typeof(TimeSpan) ||
                typeof(System.Collections.IEnumerable).IsAssignableFrom(leaf))
                continue;
            if (leaf.Namespace?.StartsWith("Vestigo", StringComparison.Ordinal) == true)
                Collect(leaf, path, result);
        }
    }
}
