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

    public List<RootDefinition> Roots { get; set; } = [];

    public Dictionary<string, ModuleSettings> Modules { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int MaxConcurrentJobs { get; set; } = 8;

    public long MaxJobOutputBytes { get; set; } = 64L * 1024 * 1024;

    public TimeSpan JobRetention { get; set; } = TimeSpan.FromHours(24);

    public int MaxJobReadBytes { get; set; } = 64 * 1024;

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

    public EnvironmentOptions Environment { get; set; } = new();

    public LogsOptions Logs { get; set; } = new();

    public EventLogOptions EventLog { get; set; } = new();

    public bool AuthenticationEnabled => ApiTokens.Count > 0;

    public void Validate()
    {
        if (!IPAddress.TryParse(ListenAddress, out IPAddress? address) || address.AddressFamily == AddressFamily.Unspecified)
        {
            throw new InvalidOperationException($"Vestigo:ListenAddress '{ListenAddress}' is not an IP address.");
        }

        if (!IPAddress.IsLoopback(address))
        {
            throw new InvalidOperationException("Vestigo only accepts loopback listen addresses.");
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

        if (MaxConcurrentJobs < 1 || MaxJobOutputBytes < 1 || MaxJobReadBytes < 1 || JobRetention <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Vestigo job limits must be positive.");
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (RootDefinition root in Roots)
        {
            if (string.IsNullOrWhiteSpace(root.Name) || !System.Text.RegularExpressions.Regex.IsMatch(root.Name, "^[A-Za-z][A-Za-z0-9_-]+$"))
            {
                throw new InvalidOperationException(
                    $"Vestigo:Roots name '{root.Name}' must be at least two characters of letters, digits, '_' or '-' and start with a letter.");
            }

            if (!names.Add(root.Name))
            {
                throw new InvalidOperationException($"Vestigo:Roots contains duplicate name '{root.Name}'.");
            }

            if (string.IsNullOrWhiteSpace(root.Path) || !System.IO.Path.IsPathFullyQualified(root.Path))
            {
                throw new InvalidOperationException($"Vestigo:Roots '{root.Name}' needs an absolute Path.");
            }

            if (root.Access == RootAccess.None)
            {
                throw new InvalidOperationException($"Vestigo:Roots '{root.Name}' needs at least one access flag.");
            }
        }

        Environment.Validate();
        Logs.Validate();
        EventLog.Validate();

        if (BufferSizeKb == 0 || FlushTimerSeconds == 0)
        {
            throw new InvalidOperationException("ETW buffer size and flush timer must be positive.");
        }
    }
}
