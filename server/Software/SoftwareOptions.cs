namespace Vestigo.Software;

/// <summary>
///     Settings for the Software module, bound from <c>Vestigo:Software</c>.
/// </summary>
public sealed class SoftwareOptions
{
    public const string SectionName = "Software";

    /// <summary>Enables <c>uninstall_software</c>. Listing works regardless.</summary>
    public bool AllowUninstall { get; set; }

    /// <summary>
    ///     Case-insensitive substrings of the display name that uninstall refuses. Configuration entries are
    ///     added to these defaults because the configuration binder appends to existing lists.
    /// </summary>
    public List<string> ProtectedNames { get; set; } = ["Vestigo"];

    /// <summary>Upper bound for one uninstall run; the process tree is killed afterwards.</summary>
    public TimeSpan MaxUninstallDuration { get; set; } = TimeSpan.FromMinutes(30);

    public void Validate()
    {
        if (ProtectedNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Vestigo:Software:ProtectedNames must not contain empty names.");
        }

        if (MaxUninstallDuration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Vestigo:Software:MaxUninstallDuration must be positive.");
        }
    }

    public bool IsProtected(string displayName) =>
        ProtectedNames.Any(name => displayName.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase));
}
