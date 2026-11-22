using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

using Vestigo.Extensibility;

namespace Vestigo.Plugins;

internal sealed class VestigoPluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _directory;
    // Only plugin contracts and SDK contracts share host package identity. Framework assemblies
    // are allowlisted by the names that ship in the runtime directories, including when the host
    // has replaced one with a newer package copy outside those directories.
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(IVestigoPlugin).Assembly.GetName().Name!,
        "ModelContextProtocol", "ModelContextProtocol.Core", "ModelContextProtocol.AspNetCore",
        "Microsoft.Extensions.AI.Abstractions"
    };
    private static readonly HashSet<string> Framework = GetFrameworkNames();

    public VestigoPluginLoadContext(string entry) : base($"Vestigo plugin: {entry}", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(entry);
        _directory = Path.GetDirectoryName(entry)!;
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (Shared.Contains(name.Name!) || Framework.Contains(name.Name!))
        {
            Assembly assembly = Default.LoadFromAssemblyName(name);
            AssemblyName actual = assembly.GetName();
            if (!string.Equals(actual.Name, name.Name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actual.CultureName ?? "", name.CultureName ?? "", StringComparison.OrdinalIgnoreCase) ||
                !(actual.GetPublicKeyToken() ?? []).SequenceEqual(name.GetPublicKeyToken() ?? []) ||
                (name.Version is { } requested && (actual.Version! < requested ||
                    (Shared.Contains(name.Name!) && actual.Version!.Major != requested.Major))))
                throw new PluginValidationException($"Incompatible shared assembly '{name.Name}': requested {name.Version}, host {actual.Version}; check culture and public key identity as well as version.");
            return assembly;
        }

        string? path = _resolver.ResolveAssemblyToPath(name);
        // Never fall back to arbitrary host packages for a private dependency.
        if (path is null)
            throw new PluginValidationException($"Plugin private dependency '{name.Name}' is missing from its published deployment; publish all dependencies beside entryAssembly.");
        RequirePrivatePath(path);
        return LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string name)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(name);
        if (path is null)
        {
            // Permit OS imports only from System32, never from cwd or PATH.
            if (OperatingSystem.IsWindows() && Path.GetFileName(name) == name &&
                NativeLibrary.TryLoad(name, typeof(VestigoPluginLoadContext).Assembly, DllImportSearchPath.System32, out IntPtr system))
                return system;
            throw new DllNotFoundException($"Plugin native dependency '{name}' is missing from its published deployment or System32.");
        }
        RequirePrivatePath(path);
        return LoadUnmanagedDllFromPath(path);
    }

    private void RequirePrivatePath(string path)
    {
        if (!Security.PathPolicy.IsInsideRoot(Path.GetFullPath(path), _directory))
            throw new PluginValidationException("A plugin dependency resolved outside its deployment directory.");
        PluginDeployment.RequireSafePath(path);
    }

    internal static HashSet<string> GetFrameworkNames()
    {
        // Intersect the trusted platform assemblies with the files that ship in the runtime and
        // ASP.NET Core directories. A newer Microsoft.Extensions package is loaded from the
        // application directory, so its trusted path is no longer the shared-framework file, but
        // the assembly name still belongs to the framework allowlist. Plugins then share the
        // host's loaded version instead of expecting a private copy beside the plugin.
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string aspnet = Path.GetDirectoryName(typeof(WebApplication).Assembly.Location)!;
        HashSet<string> shipped = new(StringComparer.OrdinalIgnoreCase);
        AddShipped(runtime);
        AddShipped(aspnet);
        return ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => shipped.Contains(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        void AddShipped(string directory)
        {
            if (!Directory.Exists(directory))
                return;
            foreach (string path in Directory.EnumerateFiles(directory, "*.dll"))
                shipped.Add(Path.GetFileNameWithoutExtension(path));
        }
    }

    internal void ValidateReferences(Assembly entry)
    {
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);
        Visit(entry);
        void Visit(Assembly assembly)
        {
            if (!visited.Add(assembly.FullName!)) return;
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                Assembly dependency = LoadFromAssemblyName(reference);
                if (GetLoadContext(dependency) == this) Visit(dependency);
            }
        }
    }
}
