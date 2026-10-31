using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace Vestigo.RemoteDebugger;

/// <summary>One running <c>msvsmon.exe</c>.</summary>
public sealed record MsvsmonProcess(
    int Pid,
    int SessionId,
    string? Path,
    DateTimeOffset? StartTime,
    string? CommandLine,
    int? Port);

/// <summary>Enumerates msvsmon processes and listening TCP ports.</summary>
public static class MsvsmonProcesses
{
    public static IReadOnlyList<MsvsmonProcess> List(bool readCommandLines)
    {
        List<(int Pid, int Session, string? Path, DateTimeOffset? Start)> found = [];
        foreach (Process process in Process.GetProcessesByName("msvsmon"))
        {
            using (process)
            {
                string? path = null;
                DateTimeOffset? start = null;
                try
                {
                    path = process.MainModule?.FileName;
                    start = process.StartTime;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Elevated or exited processes may deny details.
                }

                found.Add((process.Id, process.SessionId, path, start));
            }
        }

        Dictionary<int, string> lines = found.Count > 0 && readCommandLines ? ReadCommandLines() : [];
        return found
            .Select(item =>
            {
                lines.TryGetValue(item.Pid, out string? line);
                return new MsvsmonProcess(item.Pid, item.Session, item.Path, item.Start, line, RemoteDebuggerPlanner.ParsePort(line));
            })
            .OrderBy(item => item.Pid)
            .ToList();
    }

    public static bool IsListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == port);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    /// <summary>Parses <c>ConvertTo-Json</c> output of ProcessId/CommandLine rows (a single object or an array).</summary>
    public static Dictionary<int, string> ParseCommandLines(string json)
    {
        Dictionary<int, string> result = [];
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            IEnumerable<JsonElement> rows = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : [document.RootElement];
            foreach (JsonElement row in rows)
            {
                if (row.ValueKind == JsonValueKind.Object
                    && row.TryGetProperty("ProcessId", out JsonElement id) && id.TryGetInt32(out int pid)
                    && row.TryGetProperty("CommandLine", out JsonElement line) && line.ValueKind == JsonValueKind.String)
                {
                    result[pid] = line.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return result;
    }

    private static Dictionary<int, string> ReadCommandLines()
    {
        try
        {
            CliOutput? result = CliRunner.Run(
                System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                [
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "Get-CimInstance Win32_Process -Filter \"Name='msvsmon.exe'\" | Select-Object ProcessId,CommandLine | ConvertTo-Json -Compress"
                ],
                TimeSpan.FromSeconds(15));
            return result is null ? [] : ParseCommandLines(result.StandardOutput);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }
    }
}
