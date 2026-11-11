using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Vestigo.Extensibility;
using Vestigo.Configuration;

namespace Vestigo.Plugins;

internal sealed record PluginManifest(string Id, string Version, string HostApiVersion, string EntryAssembly, string EntryType);
internal sealed record PluginDiagnostic(string Plugin, string Status, string Detail);

internal sealed class PluginManager
{
    public List<PluginDiagnostic> Diagnostics { get; } = [];
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    public IReadOnlyList<Modules.VestigoModule> LoadModules(VestigoOptions options, IConfiguration configuration)
    {
        options.Plugins.Validate();
        if (!options.Plugins.Enabled)
        {
            Diagnostics.Add(new("*", "disabled", "External plugin loading is disabled."));
            return [];
        }
        string root = options.Plugins.EffectiveRoot;
        if (!Directory.Exists(root))
        {
            Diagnostics.Add(new("*", "absent", "No plugin root is installed."));
            return [];
        }
        PluginDeployment.ValidateRoot(root);
        List<(string Directory, PluginManifest Manifest)> candidates = [];
        foreach (string directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            try
            {
                PluginDeployment.RequireSafePath(directory);
                string manifestPath = Path.Combine(directory, "plugin.json");
                PluginDeployment.RequireSafePath(manifestPath);
                if (new FileInfo(manifestPath).Length > 64 * 1024)
                    throw new PluginValidationException("plugin.json exceeds 64 KiB.");
                string content = File.ReadAllText(manifestPath);
                using (JsonDocument document = JsonDocument.Parse(content))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object ||
                        document.RootElement.EnumerateObject().GroupBy(property => property.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
                        throw new PluginValidationException("plugin.json must be an object without duplicate fields.");
                }
                PluginManifest manifest = JsonSerializer.Deserialize<PluginManifest>(content, Json)
                    ?? throw new PluginValidationException("plugin.json must be an object.");
                ValidateManifest(manifest);
                candidates.Add((directory, manifest));
            }
            catch (Exception ex)
            {
                Diagnostics.Add(new(Path.GetFileName(directory), "rejected", Describe(ex)));
            }
        }

        HashSet<string> duplicates = candidates.GroupBy(candidate => candidate.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<Modules.VestigoModule> modules = [];
        foreach ((string directory, PluginManifest manifest) in candidates)
        {
            bool executing = false;
            try
            {
                if (duplicates.Contains(manifest.Id))
                    throw new PluginValidationException("Duplicate plugin id; all deployments with this id are rejected.");
                IReadOnlyList<string> files = PluginDeployment.ValidateTree(directory);
                if (options.Plugins.TrustMode == PluginTrustMode.PinnedSha256 &&
                    (!options.Plugins.PackageHashes.TryGetValue(manifest.Id, out string? expected) ||
                     !string.Equals(expected, PluginDeployment.ComputeHash(directory, files), StringComparison.OrdinalIgnoreCase)))
                    throw new PluginValidationException("PackageHashes has no matching SHA-256 pin for this deployment.");
                string entry = Path.Combine(directory, manifest.EntryAssembly);
                if (!File.Exists(entry)) throw new PluginValidationException("entryAssembly does not exist.");
                if (!File.Exists(Path.ChangeExtension(entry, ".deps.json")))
                    throw new PluginValidationException("entryAssembly requires a published .deps.json (EnableDynamicLoading=true).");
                VestigoPluginLoadContext context = new(entry);
                Assembly assembly = context.LoadFromAssemblyPath(entry);
                context.ValidateReferences(assembly);
                Type type = assembly.GetType(manifest.EntryType, throwOnError: true)!;
                if (type.Assembly != assembly || !type.IsPublic || type.IsAbstract || type.ContainsGenericParameters ||
                    !typeof(IVestigoPlugin).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
                    throw new PluginValidationException("entryType must be a public concrete IVestigoPlugin with a public parameterless constructor in entryAssembly.");
                executing = true;
                IVestigoPlugin plugin = (IVestigoPlugin)Activator.CreateInstance(type)!;
                Extensibility.VestigoModule[] contributed = plugin.Modules.ToArray();
                if (contributed.Length == 0 || contributed.Any(module => module is null))
                    throw new PluginValidationException("Modules must contain at least one non-null module.");
                Modules.VestigoModule[] adapted = contributed.Select(module => (Modules.VestigoModule)new ExternalModuleAdapter(
                    module, manifest.Id, new PluginContext(configuration.GetSection($"Vestigo:Modules:{module.Name}"), directory, options.AuthenticationEnabled))).ToArray();
                // Publish modules only after the entire entry point has succeeded.
                modules.AddRange(adapted);
                Diagnostics.Add(new(manifest.Id, "loaded", $"Version {manifest.Version}; API {manifest.HostApiVersion}; modules: {string.Join(", ", adapted.Select(module => module.Name))}."));
            }
            catch (Exception ex)
            {
                Diagnostics.Add(new(manifest.Id, executing ? "failed" : "rejected", Describe(ex)));
            }
        }
        return modules;
    }

    private static string Describe(Exception exception)
    {
        Exception cause = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
        if (cause is FileLoadException { InnerException: PluginValidationException validation })
            cause = validation;
        // Plugin-controlled exception messages may contain secrets. Do not log them.
        return cause switch
        {
            PluginValidationException => cause.Message,
            JsonException json => $"Invalid plugin.json schema at {json.Path ?? "$"}; required fields are id, version, hostApiVersion, entryAssembly and entryType.",
            FileNotFoundException => "Missing plugin.json, entry assembly or required dependency. Check the published deployment.",
            TypeLoadException => "entryType was not found or its dependencies/shared contracts are incompatible.",
            _ => $"{cause.GetType().Name}; check manifest fields, deployment dependencies and shared contract versions."
        };
    }

    private static void ValidateManifest(PluginManifest manifest)
    {
        if (manifest.Id is null || !Regex.IsMatch(manifest.Id, "^[a-z][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant))
            throw new PluginValidationException("id must contain lowercase letters, digits or hyphens and start with a letter (1–64 characters).");
        if (string.IsNullOrWhiteSpace(manifest.Version) || !System.Version.TryParse(manifest.Version, out _))
            throw new PluginValidationException("version must be a numeric .NET version.");
        if (!System.Version.TryParse(manifest.HostApiVersion, out Version? required) || !PluginApi.IsCompatible(required))
            throw new PluginValidationException($"hostApiVersion must be a supported major.minor API requirement; host API is {PluginApi.Version}.");
        if (string.IsNullOrWhiteSpace(manifest.EntryAssembly) || Path.GetFileName(manifest.EntryAssembly) != manifest.EntryAssembly ||
            manifest.EntryAssembly.IndexOfAny(['/', '\\', ':']) >= 0 || !manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new PluginValidationException("entryAssembly must be a DLL filename directly inside the plugin directory.");
        if (string.IsNullOrWhiteSpace(manifest.EntryType))
            throw new PluginValidationException("entryType is required.");
    }

}
