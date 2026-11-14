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
            // After a save, show the pending machine settings, including defaults for omitted properties.
            JsonObject combined = JsonSerializer.SerializeToNode(running, JsonOptions)!.AsObject();
            JsonObject? saved = Find(document, "Vestigo") as JsonObject;
            if (saved is not null) Merge(combined, saved);
            VestigoOptions options = Bind(new JsonObject { ["Vestigo"] = combined.DeepClone() });
            JsonObject values = JsonSerializer.SerializeToNode(options, JsonOptions)!.AsObject();
            if (saved is not null) CopyUnknown(values, saved);
            JsonObject moduleValues = Find(values, "Modules")!.AsObject();
            foreach (VestigoModule module in modules)
                if (Find(moduleValues, module.Name) is null)
                    moduleValues[module.Name] = new JsonObject { ["Enabled"] = module.IsEnabled(options) };
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
            Set(document, "Vestigo", snapshot.Values.DeepClone());
            VestigoOptions candidate;
            try
            {
                ValidateContainers(snapshot.Values, JsonSerializer.SerializeToNode(new VestigoOptions(), JsonOptions)!.AsObject());
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
            return new(snapshot.Values.DeepClone().AsObject(), Revision(output));
        }
    }

    private static VestigoOptions Bind(JsonObject document)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(document.ToJsonString()));
        IConfigurationRoot configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();
        return configuration.GetSection(VestigoOptions.SectionName).Get<VestigoOptions>() ?? new();
    }

    private static string ReadFile() => File.Exists(VestigoPaths.SettingsFilePath) ? File.ReadAllText(VestigoPaths.SettingsFilePath) : "{}";
    private static void ValidateContainers(JsonObject values, JsonObject defaults)
    {
        foreach ((string key, JsonNode? expected) in defaults)
        {
            JsonNode? actual = Find(values, key);
            if (expected is JsonObject child)
            {
                if (actual is not JsonObject container) throw new InvalidOperationException();
                ValidateContainers(container, child);
            }
            else if (expected is JsonArray && actual is not JsonArray)
                throw new InvalidOperationException();
        }
    }
    private static string Revision(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static JsonObject Parse(string text) => JsonNode.Parse(text, documentOptions: DocumentOptions)?.AsObject() ?? new();
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
    private static void CopyUnknown(JsonObject target, JsonObject source)
    {
        foreach ((string key, JsonNode? value) in source)
        {
            if (!target.Any(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                target[key] = value?.DeepClone();
            else if (Find(target, key) is JsonObject child && value is JsonObject saved)
                CopyUnknown(child, saved);
        }
    }
}
