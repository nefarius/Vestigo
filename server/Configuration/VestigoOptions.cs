using System.Net;
using System.Net.Sockets;

namespace Vestigo.Configuration;

/// <summary>
///     Host, authorization, path, and capture limits loaded from configuration.
/// </summary>
public sealed class VestigoOptions
{
    public const string SectionName = "Vestigo";

    public string ListenAddress { get; set; } = "127.0.0.1";

    public int ListenPort { get; set; } = 5098;

    public List<string> ApiTokens { get; set; } = [];

    public List<string> AllowedInputRoots { get; set; } = [];

    public List<string> AllowedOutputRoots { get; set; } = [];

    public string? SymbolServer { get; set; }

    public string? SymbolCache { get; set; }

    public long MaxInputBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    public int MaxEvents { get; set; } = 1_000_000;

    public long MaxOutputBytes { get; set; } = 512L * 1024 * 1024;

    public TimeSpan MaxCaptureDuration { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan SpoolRetention { get; set; } = TimeSpan.FromHours(24);

    public int MaxConcurrentCaptures { get; set; } = 4;

    public int MaxReadBatch { get; set; } = 200;

    public uint BufferSizeKb { get; set; } = 64;

    public uint FlushTimerSeconds { get; set; } = 1;

    public string ClockResolution { get; set; } = "QueryPerformanceCounter";

    public bool AuthenticationEnabled => ApiTokens.Count > 0;

    /// <summary>
    ///     True when <see cref="ListenAddress" /> is an IP address other than loopback, such as 0.0.0.0 or ::.
    /// </summary>
    public bool IsBoundBeyondLoopback =>
        IPAddress.TryParse(ListenAddress, out IPAddress? address) && !IPAddress.IsLoopback(address);

    /// <summary>
    ///     Returns a startup warning when the server is reachable from the network without any API token, otherwise null.
    ///     The message never contains token values.
    /// </summary>
    public string? GetExposureWarning() =>
        IsBoundBeyondLoopback && !AuthenticationEnabled
            ? $"SECURITY WARNING: Vestigo is listening on non-loopback address {ListenAddress}:{ListenPort} and no Vestigo:ApiTokens are configured. " +
              "Anyone who can reach this port can use /mcp to capture traces and read files within the allowed roots. " +
              "Configure Vestigo:ApiTokens or bind to a loopback address."
            : null;

    public void Validate()
    {
        if (!IPAddress.TryParse(ListenAddress, out IPAddress? address) || address.AddressFamily == AddressFamily.Unspecified)
        {
            throw new InvalidOperationException($"Vestigo:ListenAddress '{ListenAddress}' is not an IP address.");
        }


        if (ListenPort is < 1 or > 65535)
        {
            throw new InvalidOperationException("Vestigo:ListenPort must be between 1 and 65535.");
        }

        if (ApiTokens.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Vestigo:ApiTokens cannot contain blank tokens.");
        }

        if (MaxInputBytes < 1 || MaxEvents < 1 || MaxOutputBytes < 1 || MaxReadBatch < 1 || MaxConcurrentCaptures < 1)
        {
            throw new InvalidOperationException("Vestigo size and count limits must be positive.");
        }

        if (MaxCaptureDuration <= TimeSpan.Zero || SpoolRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Vestigo durations must be positive.");
        }

        if (BufferSizeKb == 0 || FlushTimerSeconds == 0)
        {
            throw new InvalidOperationException("ETW buffer size and flush timer must be positive.");
        }
    }
}
