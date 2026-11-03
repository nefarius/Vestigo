using System.Text.RegularExpressions;

using Vestigo.Configuration;

namespace Vestigo.Security;

/// <summary>
///     Resolves client paths against configured named roots and their access flags.
///     Accepts <c>root:relative\path</c> or an absolute path that lies inside a root with the required access.
///     Volume roots from <see cref="VolumeRootSource" /> are merged with custom <see cref="VestigoOptions.Roots" />.
/// </summary>
public sealed partial class RootPolicy(VestigoOptions options, VolumeRootSource? volumes = null)
{
    private readonly VolumeRootSource _volumes = volumes ?? new VolumeRootSource(options);

    public IReadOnlyList<RootDefinition> Roots
    {
        get
        {
            IReadOnlyList<RootDefinition> generated = _volumes.Current;
            bool traces = options.ShouldGenerateTracesRoot;
            if (generated.Count == 0 && !traces)
            {
                return options.Roots;
            }

            if (options.Roots.Count == 0 && generated.Count == 0 && traces)
            {
                return [BuiltInTracesRoot()];
            }

            if (options.Roots.Count == 0 && !traces)
            {
                return generated;
            }

            List<RootDefinition> merged = new(options.Roots.Count + generated.Count + (traces ? 1 : 0));
            merged.AddRange(options.Roots);
            if (traces)
            {
                merged.Add(BuiltInTracesRoot());
            }

            merged.AddRange(generated);
            return merged;
        }
    }

    internal static RootDefinition BuiltInTracesRoot() => new()
    {
        Name = PathPolicy.TracesRootName,
        Path = VestigoPaths.TraceDirectory,
        Access = RootAccess.Read | RootAccess.Write
    };

    /// <summary>Resolves a path that must be reachable with <paramref name="needed" /> access.</summary>
    public PathCheck Resolve(string requestedPath, RootAccess needed)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return PathCheck.Fail("invalid_path", "Path is required.");
        }

        IReadOnlyList<RootDefinition> roots = Roots;
        if (roots.Count == 0)
        {
            return PathCheck.Fail(
                "roots_not_configured",
                "No roots are configured. Add entries to Vestigo:Roots or set Vestigo:VolumeRoots.");
        }

        Match named = NamedRoot().Match(requestedPath);
        if (named.Success)
        {
            string name = named.Groups["name"].Value;
            RootDefinition? root = roots.FirstOrDefault(
                candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (root is null)
            {
                return PathCheck.Fail("unknown_root", $"Root '{name}' is not configured.");
            }

            if (!root.Access.HasFlag(needed))
            {
                return PathCheck.Fail("access_denied", $"Root '{root.Name}' does not allow {needed} access.");
            }

            string relative = named.Groups["rest"].Value.TrimStart('\\', '/');
            string combined;
            try
            {
                combined = Path.Combine(root.Path, relative);
            }
            catch (ArgumentException)
            {
                return PathCheck.Fail("invalid_path", "Path is not a valid filesystem path.");
            }

            return PathPolicy.Locate(combined, [root.Path], "root");
        }

        List<string> eligible = roots
            .Where(root => root.Access.HasFlag(needed))
            .Select(root => root.Path)
            .ToList();
        if (eligible.Count == 0)
        {
            return PathCheck.Fail("access_denied", $"No root allows {needed} access.");
        }

        return PathPolicy.Locate(requestedPath, eligible, "root");
    }

    /// <summary>Resolves an existing file or directory.</summary>
    public PathCheck ResolveExisting(string requestedPath, RootAccess needed)
    {
        PathCheck check = Resolve(requestedPath, needed);
        if (!check.Success)
        {
            return check;
        }

        return File.Exists(check.FullPath) || Directory.Exists(check.FullPath)
            ? check
            : PathCheck.Fail("not_found", $"Path does not exist: '{check.FullPath}'.");
    }

    /// <summary>
    ///     Describes a canonical path for disclosure. An alias is returned only when the resolved path lies in a
    ///     root with <see cref="RootAccess.Read" />. Paths outside every such root produce a null alias and
    ///     <c>outsideRoots: true</c>. A path that cannot be canonicalized produces neither.
    /// </summary>
    public (string? Alias, bool OutsideRoots) DescribeRead(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !PathPolicy.TryCanonicalize(fullPath, out string canonical))
        {
            return (null, false);
        }

        foreach (RootDefinition root in Roots)
        {
            if (!root.Access.HasFlag(RootAccess.Read) || !PathPolicy.TryCanonicalize(root.Path, out string rootPath))
            {
                continue;
            }

            if (!PathPolicy.IsInsideRoot(canonical, rootPath))
            {
                continue;
            }

            string relative = Path.GetRelativePath(rootPath, canonical);
            if (relative == ".")
            {
                relative = "";
            }

            return ($"{root.Name}:{relative}", false);
        }

        return (null, true);
    }

    [GeneratedRegex(@"^(?<name>[A-Za-z][A-Za-z0-9_-]+):(?<rest>.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex NamedRoot();
}
