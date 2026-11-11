using System.Diagnostics.Eventing.Reader;

namespace Vestigo.Power;

/// <summary>Reads the newest Power-Troubleshooter resume event from the System log.</summary>
public static class WindowsWakeSource
{
    private const string Query = "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and (EventID=1)]]";

    /// <summary>Returns the newest event, or an empty result when the log has none.</summary>
    public static WakeSourceResult Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return WakeSourceResult.None();
        }

        try
        {
            EventLogQuery query = new("System", PathType.LogName, Query) { ReverseDirection = true };
            using EventLogReader reader = new(query);
            using EventRecord? record = reader.ReadEvent();
            return record is null ? WakeSourceResult.None() : WakeSourceEvent.Parse(record.ToXml());
        }
        catch (EventLogException exception)
        {
            throw new IOException("The System event log could not be read.", exception);
        }
    }
}
