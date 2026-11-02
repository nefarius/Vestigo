namespace Vestigo.Updates;

/// <summary>
///     Settings for the Updates module, bound from <c>Vestigo:Updates</c>.
/// </summary>
public sealed class UpdatesOptions
{
    public const string SectionName = "Updates";

    /// <summary>Enables <c>start_self_update</c>. Checking the signed feed works regardless.</summary>
    public bool AllowSelfUpdate { get; set; }

    public void Validate()
    {
    }
}
