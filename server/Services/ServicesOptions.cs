namespace Vestigo.Services;

/// <summary>
///     Settings for the Services module, bound from <c>Vestigo:Services</c>.
/// </summary>
public sealed class ServicesOptions
{
    public const string SectionName = "Services";

    /// <summary>
    ///     Service names (case-insensitive) that mutating tools refuse to touch. Configuration entries are
    ///     added to these defaults because the configuration binder appends to existing lists.
    /// </summary>
    public List<string> ProtectedNames { get; set; } =
        ["Vestigo", "RpcSs", "DcomLaunch", "PlugPlay", "Power", "LSM", "SamSs"];

    public void Validate()
    {
        foreach (string name in ProtectedNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException("Vestigo:Services:ProtectedNames must not contain empty names.");
            }
        }
    }

    public bool IsProtected(string name) =>
        ProtectedNames.Any(protectedName => string.Equals(protectedName.Trim(), name, StringComparison.OrdinalIgnoreCase));
}
