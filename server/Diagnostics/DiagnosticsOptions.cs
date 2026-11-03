namespace Vestigo.Diagnostics;

/// <summary>Settings for the Diagnostics module, bound from <c>Vestigo:Diagnostics</c>.</summary>
public sealed class DiagnosticsOptions
{
    public const string SectionName = "Diagnostics";

    /// <summary>Allows <c>verify_file_signature</c> to use <c>revocation: online</c>, which may contact the network.</summary>
    public bool AllowOnlineRevocation { get; set; }

    /// <summary>Allows <c>probe_capture_support</c>. The tool is absent until graphics probes are registered.</summary>
    public bool AllowCaptureProbes { get; set; }

    /// <summary>Upper bound for <c>list_process_modules</c> <c>limit</c>.</summary>
    public int MaxProcessModules { get; set; } = 500;

    /// <summary>Maximum bytes read from SetupAPI logs for one install-history request.</summary>
    public long MaxInstallHistoryBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>Upper bound for one capture probe.</summary>
    public TimeSpan MaxProbeDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum file size accepted by <c>inspect_binary</c>.</summary>
    public long MaxInspectBytes { get; set; } = 256L * 1024 * 1024;

    public void Validate()
    {
        if (MaxProcessModules is < 1 or > 5000)
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxProcessModules must be between 1 and 5000.");
        }

        if (MaxInstallHistoryBytes is < 65_536 or > 256L * 1024 * 1024)
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxInstallHistoryBytes must be between 65536 and 268435456.");
        }

        if (MaxProbeDuration < TimeSpan.FromMilliseconds(100) || MaxProbeDuration > TimeSpan.FromSeconds(30))
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxProbeDuration must be between 00:00:00.100 and 00:00:30.");
        }

        if (MaxInspectBytes is < 1_048_576 or > 2L * 1024 * 1024 * 1024)
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxInspectBytes must be between 1048576 and 2147483648.");
        }
    }
}
