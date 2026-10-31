namespace Vestigo.Configuration;

/// <summary>Settings for the Environment module (<c>Vestigo:Environment</c>).</summary>
public sealed class EnvironmentOptions
{
    /// <summary>Variable names (case-insensitive) that the tools refuse to set or unset in any scope.</summary>
    public List<string> ProtectedNames { get; set; } = ["COMSPEC", "SystemRoot", "windir"];

    public void Validate()
    {
        if (ProtectedNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Vestigo:Environment:ProtectedNames cannot contain blank names.");
        }
    }
}

/// <summary>Settings for the Logs module (<c>Vestigo:Logs</c>).</summary>
public sealed class LogsOptions
{
    /// <summary>Upper bound for lines returned by one tail or read call.</summary>
    public int MaxTailLines { get; set; } = 2000;

    /// <summary>Upper bound for bytes examined by one tail or read call.</summary>
    public int MaxTailBytes { get; set; } = 1024 * 1024;

    public void Validate()
    {
        if (MaxTailLines < 1 || MaxTailBytes < 16)
        {
            throw new InvalidOperationException("Vestigo:Logs limits must be positive (MaxTailBytes at least 16).");
        }
    }
}

/// <summary>Settings for the EventLog module (<c>Vestigo:EventLog</c>).</summary>
public sealed class EventLogOptions
{
    /// <summary>Upper bound for events returned by one query.</summary>
    public int MaxEvents { get; set; } = 500;

    /// <summary>Upper bound for events examined by one query, which matters for message text filters.</summary>
    public int MaxScannedEvents { get; set; } = 20_000;

    public void Validate()
    {
        if (MaxEvents < 1 || MaxScannedEvents < MaxEvents)
        {
            throw new InvalidOperationException(
                "Vestigo:EventLog:MaxEvents must be positive and MaxScannedEvents must not be smaller.");
        }
    }
}
