using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Reflection;
using Vestigo.Configuration;
using Vestigo.Modules;

namespace Vestigo.Hosting;

internal sealed record ConfigurationSnapshot(JsonObject Values, string Revision);

/// <summary>Edits machine settings without mutating the startup options or losing unrelated sections.</summary>
internal sealed class ConfigurationEditor(VestigoOptions running, IReadOnlyList<VestigoModule> modules)
{
    private readonly object _gate = new();
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        TypeInfoResolver = CreateResolver(),
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    private static DefaultJsonTypeInfoResolver CreateResolver()
    {
        DefaultJsonTypeInfoResolver resolver = new();
        resolver.Modifiers.Add(info =>
        {
            // System.Text.Json includes read-only collection getters even with IgnoreReadOnlyProperties.
            foreach (JsonPropertyInfo property in info.Properties.ToArray())
                if (property.AttributeProvider is PropertyInfo reflection && !reflection.CanWrite)
                    info.Properties.Remove(property);
        });
        return resolver;
    }

    public ConfigurationSnapshot Load()
    {
        lock (_gate)
        {
            string text = ReadFile();
            JsonObject document = Parse(text);
            ValidateDocument(document);
            // After a save, show the pending machine settings, including defaults for omitted properties.
            JsonObject combined = JsonSerializer.SerializeToNode(running, JsonOptions)!.AsObject();
            JsonObject? saved = Find(document, "Vestigo") as JsonObject;
            if (saved is not null)
            {
                NormalizeSavedSymbolServer(saved);
                Merge(combined, saved);
            }
            VestigoOptions options = Bind(new JsonObject { ["Vestigo"] = combined.DeepClone() });
            JsonObject values = JsonSerializer.SerializeToNode(options, JsonOptions)!.AsObject();
            JsonObject moduleValues = Find(values, "Modules")!.AsObject();
            foreach (VestigoModule module in modules)
                if (Find(moduleValues, module.Name) is null)
                    moduleValues[module.Name] = new JsonObject { ["Enabled"] = module.IsEnabled(options) };
            if (saved is not null) CopyUnknown(values, saved);
            return new(values, Revision(text));
        }
    }

    public ConfigurationSnapshot Save(ConfigurationSnapshot snapshot)
    {
        lock (_gate)
        {
            string original = ReadFile();
            if (Revision(original) != snapshot.Revision)
                throw new InvalidOperationException("Settings changed on disk. Reload the page before saving again.");
            JsonObject document = Parse(original);
            JsonObject values;
            VestigoOptions candidate;
            try
            {
                ValidateDocument(new JsonObject { ["Vestigo"] = snapshot.Values.DeepClone() });
                values = NormalizePaths(snapshot.Values)!.AsObject();
                Set(document, "Vestigo", values.DeepClone());
                ValidateDocument(document);
                ValidateContainers(values, JsonSerializer.SerializeToNode(new VestigoOptions(), JsonOptions)!.AsObject());
                candidate = Bind(document);
                candidate.Validate();
                ModuleRegistry.ValidateConfiguration(candidate, modules);
                _ = ModuleRegistry.Enabled(candidate, modules);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or NullReferenceException or FormatException or OverflowException)
            {
                // Binder/validator exception messages can contain supplied secrets. Never echo them.
                throw new InvalidOperationException("Invalid configuration. Check field types, positive limits, paths, module dependencies and security gates.");
            }
            string output = document.ToJsonString(JsonOptions) + "\n";
            string temporary = Path.Combine(VestigoPaths.SettingsDirectory, $".config-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, output, new UTF8Encoding(false));
                // Replace preserves the destination ACL. The temporary file inherits the secured state directory ACL.
                if (File.Exists(VestigoPaths.SettingsFilePath)) File.Replace(temporary, VestigoPaths.SettingsFilePath, null);
                else File.Move(temporary, VestigoPaths.SettingsFilePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return new(values.DeepClone().AsObject(), Revision(output));
        }
    }

    private static VestigoOptions Bind(JsonObject document)
    {
        // Strict binding catches conversions that the default binder silently drops from collections.
        // Filter only the binding copy so unknown plugin fields remain intact in the saved document.
        JsonObject known = new() { ["Vestigo"] = KnownValues(Find(document, "Vestigo"), typeof(VestigoOptions)) };
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(known.ToJsonString()));
        IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        return configuration.GetSection(VestigoOptions.SectionName)
            .Get<VestigoOptions>(options => options.ErrorOnUnknownConfiguration = true) ?? new();
    }

    private static void NormalizeSavedSymbolServer(JsonObject saved)
    {
        if (Find(saved, "SymbolServer")?.GetValue<string>() is not { } url ||
            !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.UserInfo)) return;
        VestigoOptions options = Bind(new JsonObject { ["Vestigo"] = saved.DeepClone() });
        options.NormalizeSymbolServer();
        Set(saved, "SymbolServer", JsonValue.Create(options.SymbolServer));
        JsonObject auth = Find(saved, "SymbolServerAuth") as JsonObject ?? new();
        Merge(auth, JsonSerializer.SerializeToNode(options.SymbolServerAuth, JsonOptions)!.AsObject());
        if (auth.Parent is null) Set(saved, "SymbolServerAuth", auth);
    }

    private static JsonNode? KnownValues(JsonNode? node, Type type)
    {
        Type? element = CollectionElement(type);
        if (node is JsonArray array)
        {
            return element is null ? node.DeepClone() : new JsonArray(array.Select(item => KnownValues(item, element)).ToArray());
        }
        if (node is not JsonObject source) return node?.DeepClone();
        JsonObject result = new();
        foreach ((string key, JsonNode? value) in source)
        {
            Type? valueType = type;
            foreach (string segment in key.Split(ConfigurationPath.KeyDelimiter))
            {
                Type? dictionary = valueType?.GetInterfaces().FirstOrDefault(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IDictionary<,>));
                valueType = dictionary?.GetGenericArguments()[1] ?? (valueType is null ? null : CollectionElement(valueType)) ?? valueType?
                    .GetProperty(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.PropertyType;
            }
            if (valueType is not null) result[key] = KnownValues(value, valueType);
        }
        return result;
    }

    private static Type? CollectionElement(Type type) => type.IsArray ? type.GetElementType() : type == typeof(string) ? null : type.GetInterfaces()
        .FirstOrDefault(item => item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];

    private static string ReadFile() => File.Exists(VestigoPaths.SettingsFilePath) ? File.ReadAllText(VestigoPaths.SettingsFilePath) : "{}";
    private static void ValidateContainers(JsonObject values, JsonObject defaults)
    {
        foreach ((string key, JsonNode? expected) in defaults)
        {
            JsonNode? actual = Find(values, key);
            if (expected is JsonObject child)
            {
                JsonObject? container = actual as JsonObject;
                if (container is null)
                {
                    JsonObject descendants = Descendants(values, key);
                    if (descendants.Count == 0) throw new InvalidOperationException();
                    container = NormalizePaths(descendants)!.AsObject();
                }
                ValidateContainers(container, child);
            }
            else if (expected is JsonArray && actual is not JsonArray && Descendants(values, key).Count == 0)
                throw new InvalidOperationException();
        }
    }

    private static JsonObject Descendants(JsonObject values, string key) => new(values
        .Where(pair => pair.Key.StartsWith(key + ConfigurationPath.KeyDelimiter, StringComparison.OrdinalIgnoreCase))
        .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key[(key.Length + 1)..], pair.Value?.DeepClone())));
    private static string Revision(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static JsonObject Parse(string text)
    {
        JsonObject raw = JsonNode.Parse(text, documentOptions: DocumentOptions)?.AsObject() ?? new();
        try { ValidateDocument(raw); }
        catch (FormatException) { throw new InvalidOperationException("Configuration contains conflicting or invalid paths."); }
        return NormalizePaths(raw)!.AsObject();
    }

    private static void ValidateDocument(JsonObject document)
    {
        // Include unknown plugin and non-Vestigo sections: they must also be valid provider input.
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(document.ToJsonString()));
        _ = new ConfigurationBuilder().AddJsonStream(stream).Build();
    }

    private static JsonNode? NormalizePaths(JsonNode? node)
    {
        if (node is JsonArray array) return new JsonArray(array.Select(NormalizePaths).ToArray());
        if (node is not JsonObject source) return node?.DeepClone();
        JsonObject result = new();
        foreach ((string key, JsonNode? value) in source)
        {
            JsonNode? path = NormalizePaths(value);
            foreach (string segment in key.Split(ConfigurationPath.KeyDelimiter).Reverse())
                path = new JsonObject { [segment] = path };
            result = MergePaths(result, path).AsObject();
        }
        return result;
    }

    // Array indexes are configuration path segments too. Numeric objects keep sparse indexes
    // intact when a delimited key adds a sibling to an existing array.
    private static JsonObject AsPathObject(JsonNode? node) => node switch
    {
        JsonObject obj => obj,
        JsonArray array => new JsonObject(array.Select((value, index) =>
            new KeyValuePair<string, JsonNode?>(index.ToString(System.Globalization.CultureInfo.InvariantCulture), value?.DeepClone()))),
        _ => throw new InvalidOperationException("Configuration contains conflicting paths.")
    };
    private static JsonNode MergePaths(JsonNode? target, JsonNode? source)
    {
        JsonObject result = AsPathObject(target);
        void Add(string key, JsonNode? value)
        {
            string? existing = result.Select(pair => pair.Key).FirstOrDefault(item => item.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (existing is null) result[key] = value?.DeepClone();
            else if (result[existing] is JsonObject or JsonArray && value is JsonObject or JsonArray)
                result[existing] = MergePaths(result[existing], value);
            else if (value is JsonObject or JsonArray)
            {
                // IConfiguration permits a scalar and descendant values at the same time.
                // Keep descendants flat at this boundary because JSON cannot nest under a scalar.
                foreach ((string childKey, JsonNode? child) in AsPathObject(value))
                    Add(existing + ConfigurationPath.KeyDelimiter + childKey, child);
            }
            else if (result[existing] is JsonObject or JsonArray)
            {
                JsonObject children = AsPathObject(result[existing]);
                result[existing] = value?.DeepClone();
                foreach ((string childKey, JsonNode? child) in children)
                    Add(existing + ConfigurationPath.KeyDelimiter + childKey, child);
            }
            else throw new InvalidOperationException("Configuration contains conflicting paths.");
        }
        foreach ((string key, JsonNode? value) in AsPathObject(source)) Add(key, value);
        return result;
    }
    internal static JsonNode? Find(JsonObject source, string name) => source.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
    private static void Set(JsonObject target, string name, JsonNode? value)
    {
        string key = target.Select(pair => pair.Key).FirstOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
        target[key] = value;
    }
    private static void Merge(JsonObject target, JsonObject source)
    {
        foreach ((string key, JsonNode? value) in source)
        {
            if (Find(target, key) is JsonObject child && value is JsonObject saved) Merge(child, saved);
            else Set(target, key, value?.DeepClone());
        }
    }
    private static void CopyUnknown(JsonNode target, JsonNode source)
    {
        if (target is JsonArray targetArray)
        {
            JsonArray? savedArray = source as JsonArray;
            if (source is JsonObject indexed)
                savedArray = new JsonArray(indexed.OrderBy(pair => pair.Key, ConfigurationKeyComparer.Instance)
                    .Select(pair => pair.Value?.DeepClone()).ToArray());
            if (savedArray is null) return;
            for (int index = 0; index < Math.Min(targetArray.Count, savedArray.Count); index++)
                if (targetArray[index] is { } child && savedArray[index] is { } saved)
                    CopyUnknown(child, saved);
            return;
        }
        if (target is not JsonObject targetObject || source is not JsonObject sourceObject) return;
        foreach ((string key, JsonNode? value) in sourceObject)
        {
            if (!targetObject.Any(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                targetObject[key] = value?.DeepClone();
            else if (value is not (JsonObject or JsonArray) && Find(targetObject, key) is JsonObject or JsonArray)
            {
                JsonObject children = AsPathObject(Find(targetObject, key));
                Set(targetObject, key, value?.DeepClone());
                foreach ((string childKey, JsonNode? child) in children)
                    Set(targetObject, key + ConfigurationPath.KeyDelimiter + childKey, child?.DeepClone());
            }
            else if (Find(targetObject, key) is { } child && value is { } saved)
                CopyUnknown(child, saved);
        }
    }
}
