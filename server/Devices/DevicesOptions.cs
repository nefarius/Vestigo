namespace Vestigo.Devices;

/// <summary>
///     Settings for device and driver management, bound from <c>Vestigo:Devices</c>.
/// </summary>
public sealed class DevicesOptions
{
    public const string SectionName = "Devices";

    /// <summary>
    ///     Absolute path of <c>nefconc.exe</c>. Empty uses <c>tools\nefconc.exe</c> next to Vestigo.exe.
    /// </summary>
    public string? NefconPath { get; set; }

    /// <summary>Upper bound for one nefcon invocation.</summary>
    public TimeSpan NefconTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public void Validate()
    {
        if (!string.IsNullOrWhiteSpace(NefconPath) && !System.IO.Path.IsPathFullyQualified(NefconPath))
        {
            throw new InvalidOperationException("Vestigo:Devices:NefconPath must be an absolute path.");
        }

        if (NefconTimeout <= TimeSpan.Zero || NefconTimeout.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new InvalidOperationException("Vestigo:Devices:NefconTimeout must be positive and at most 4294967294 milliseconds.");
        }
    }
}