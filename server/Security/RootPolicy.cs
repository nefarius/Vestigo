using System.Text.RegularExpressions;

using Vestigo.Configuration;

namespace Vestigo.Security;

/// <summary>
///     Resolves client paths against configured named roots and their access flags.
///     Accepts <c>root:relative\path</c> or an absolute path that lies inside a root with the required access.
/// </summary>
public sealed partial class RootPolicy(VestigoOptions options)
{
    public IReadOnlyList<RootDefinition> Roots => options.Roots;

    /// <summary>Resolves a path that must be reachable with <paramref name="needed" /> access.</summary>
    public PathCheck Resolve(string requestedPath, RootAccess needed)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            return PathCheck.Fail("invalid_path", "Path is required.");
        }

        if (options.Roots.Count == 0)
        {
            return PathCheck.Fail("roots_not_configured", "No roots are configured. Add entries to Vestigo:Roots.");
        }

        Match named = NamedRoot().Match(requestedPath);
        if (named.Success)
        {
            string name = named.Groups["name"].Value;
            RootDefinition? root = options.Roots.FirstOrDefault(
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
                combined = System.IO.Path.Combine(root.Path, relative);
            }
            catch (ArgumentException)
            {
                return PathCheck.Fail("invalid_path", "Path is not a valid filesystem path.");
            }

            return PathPolicy.Locate(combined, [root.Path], "root");
        }

        List<string> eligible = options.Roots
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

    [GeneratedRegex(@"^(?<name>[A-Za-z][A-Za-z0-9_-]+):(?<rest>.*)$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex NamedRoot();
}
