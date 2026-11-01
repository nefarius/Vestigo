namespace Vestigo.SystemInfo;

/// <summary>
///     Settings for the System module, bound from <c>Vestigo:System</c>.
/// </summary>
public sealed class SystemOptions
{
    public const string SectionName = "System";

    /// <summary>
    ///     Enables <c>set_test_signing</c> and <c>set_whql_developer_test_mode</c>. Reads work regardless.
    /// </summary>
    public bool AllowBootConfigurationChanges { get; set; }

    public void Validate()
    {
    }
}
