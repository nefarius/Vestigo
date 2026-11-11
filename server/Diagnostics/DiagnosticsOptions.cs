namespace Vestigo.Diagnostics;

/// <summary>Settings for the Diagnostics module, bound from <c>Vestigo:Diagnostics</c>.</summary>
public sealed class DiagnosticsOptions
{
    public const string SectionName = "Diagnostics";

    /// <summary>Allows online revocation for <c>verify_file_signature</c> and for <c>test_endpoint</c> TLS.</summary>
    public bool AllowOnlineRevocation { get; set; }

    /// <summary>Allows <c>probe_capture_support</c>. The tool stays disabled until this is true.</summary>
    public bool AllowCaptureProbes { get; set; }

    /// <summary>Allows <c>test_endpoint</c>. Destinations must also be listed in <see cref="AllowedProbeDestinations"/>.</summary>
    public bool AllowNetworkProbes { get; set; }

    /// <summary>Allows <c>probe_audio_format</c> to render a synthetic tone. Format checks stay available without it.</summary>
    public bool AllowAudioPlayback { get; set; }

    /// <summary>Allows <c>capture_process_dump</c>. The tool stays disabled until this is true.</summary>
    public bool AllowProcessDumps { get; set; }

    /// <summary>Hosts, or <c>host:port</c> entries, that <c>test_endpoint</c> may contact. Empty allows none.</summary>
    public string[] AllowedProbeDestinations { get; set; } = [];

    /// <summary>Upper bound for <c>list_process_modules</c> <c>limit</c>.</summary>
    public int MaxProcessModules { get; set; } = 500;

    /// <summary>Maximum bytes read from SetupAPI logs for one install-history request.</summary>
    public long MaxInstallHistoryBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>Upper bound for one capture probe.</summary>
    public TimeSpan MaxProbeDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum file size accepted by <c>inspect_binary</c>.</summary>
    public long MaxInspectBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>Upper bound for <c>list_process_connections</c> <c>limit</c>.</summary>
    public int MaxProcessConnections { get; set; } = 500;

    /// <summary>Upper bound for one <c>test_endpoint</c> call.</summary>
    public TimeSpan MaxEndpointProbeDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum bytes in one <c>test_endpoint</c> UDP datagram, including a STUN binding request. 1 to 64.</summary>
    public int MaxUdpPayloadBytes { get; set; } = 64;

    /// <summary>Upper bound for a <c>test_endpoint</c> UDP send or STUN wait. Minimum 00:00:00.100, maximum 00:00:05.</summary>
    public TimeSpan MaxUdpProbeDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound for <c>list_video_encoders</c> <c>limit</c>.</summary>
    public int MaxVideoEncoders { get; set; } = 200;

    /// <summary>Upper bound for <c>list_camera_formats</c> <c>limit</c>.</summary>
    public int MaxCameraFormats { get; set; } = 64;

    /// <summary>Upper bound for one audible <c>probe_audio_format</c> call.</summary>
    public TimeSpan MaxAudioProbeDuration { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound for <c>list_crash_reports</c> <c>limit</c>.</summary>
    public int MaxCrashReports { get; set; } = 200;

    /// <summary>Maximum bytes read from one <c>Report.wer</c>.</summary>
    public long MaxCrashReportFileBytes { get; set; } = 256 * 1024;

    /// <summary>Maximum dump file size. A larger file is deleted.</summary>
    public long MaxDumpBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>Free bytes that must remain on the output volume after reserving <see cref="MaxDumpBytes"/>.</summary>
    public long MinDumpFreeBytes { get; set; } = 1024L * 1024 * 1024;

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

        if (MaxProcessConnections is < 1 or > 5000)
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxProcessConnections must be between 1 and 5000.");
        }

        if (MaxEndpointProbeDuration < TimeSpan.FromMilliseconds(100) || MaxEndpointProbeDuration > TimeSpan.FromSeconds(30))
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxEndpointProbeDuration must be between 00:00:00.100 and 00:00:30.");
        }

        if (MaxUdpPayloadBytes is < 1 or > 64)
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxUdpPayloadBytes must be between 1 and 64.");
        }

        if (MaxUdpProbeDuration < TimeSpan.FromMilliseconds(100) || MaxUdpProbeDuration > TimeSpan.FromSeconds(5))
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxUdpProbeDuration must be between 00:00:00.100 and 00:00:05.");
        }

        if (MaxVideoEncoders is < 1 or > 1000)
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxVideoEncoders must be between 1 and 1000.");
        }

        if (MaxCameraFormats is < 1 or > 500)
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxCameraFormats must be between 1 and 500.");
        }

        if (MaxAudioProbeDuration < TimeSpan.FromMilliseconds(100) || MaxAudioProbeDuration > TimeSpan.FromSeconds(5))
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxAudioProbeDuration must be between 00:00:00.100 and 00:00:05.");
        }

        if (MaxCrashReports is < 1 or > 2000)
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxCrashReports must be between 1 and 2000.");
        }

        if (MaxCrashReportFileBytes is < 4096 or > 1_048_576)
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxCrashReportFileBytes must be between 4096 and 1048576.");
        }

        if (MaxDumpBytes is < 16L * 1024 * 1024 or > 4L * 1024 * 1024 * 1024)
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MaxDumpBytes must be between 16777216 and 4294967296.");
        }

        if (MinDumpFreeBytes is < 64L * 1024 * 1024 or > 16L * 1024 * 1024 * 1024)
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:MinDumpFreeBytes must be between 67108864 and 17179869184.");
        }

        if (AllowedProbeDestinations is null || AllowedProbeDestinations.Length > 64)
        {
            throw new InvalidOperationException("Vestigo:Diagnostics:AllowedProbeDestinations must contain at most 64 entries.");
        }

        foreach (string entry in AllowedProbeDestinations)
        {
            if (!ProbeDestinationPolicy.IsValidEntry(entry))
            {
                throw new InvalidOperationException("Vestigo:Diagnostics:AllowedProbeDestinations entries must be a host or host:port, without wildcards or ranges.");
            }
        }
    }
}
