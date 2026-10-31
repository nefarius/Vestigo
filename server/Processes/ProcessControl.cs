using System.ComponentModel;
using System.Diagnostics;

namespace Vestigo.Processes;

/// <summary>One running process, as listed to clients.</summary>
public sealed record ProcessSummary(int Pid, string Name, int SessionId, DateTimeOffset? StartTime);

/// <summary>Per-process outcome of a kill request.</summary>
public sealed record KillOutcome(int Pid, string Name, bool Killed, string? Error);

/// <summary>Enumerates and terminates processes. Kept apart from the MCP layer so it can be tested.</summary>
public static class ProcessControl
{
    public static IReadOnlyList<ProcessSummary> List(string? nameFilter, int? pid, int? sessionId, int limit)
    {
        List<ProcessSummary> result = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (pid is { } wanted && process.Id != wanted)
                {
                    continue;
                }

                if (sessionId is { } session && process.SessionId != session)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(nameFilter)
                    && process.ProcessName.IndexOf(CloseSelector.Normalize(nameFilter.Trim()), StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                result.Add(new ProcessSummary(process.Id, process.ProcessName, process.SessionId, StartTime(process)));
            }
        }

        return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Pid).Take(limit).ToList();
    }

    /// <summary>Kills processes by pid or exact name, never the host, System, or Idle.</summary>
    public static OperationResult<IReadOnlyList<KillOutcome>> Kill(int? pid, string? name, bool entireTree, int ownPid)
    {
        if (pid is null == string.IsNullOrWhiteSpace(name))
        {
            return OperationResult<IReadOnlyList<KillOutcome>>.Fail("invalid_request", "Provide exactly one of pid or processName.");
        }

        List<Process> targets = [];
        try
        {
            if (pid is { } id)
            {
                if (ProcessGuards.CheckKillTarget(id, ownPid) is { } refusal)
                {
                    return OperationResult<IReadOnlyList<KillOutcome>>.Fail("refused", refusal);
                }

                try
                {
                    targets.Add(Process.GetProcessById(id));
                }
                catch (ArgumentException)
                {
                    return OperationResult<IReadOnlyList<KillOutcome>>.Fail("not_found", $"No process with id {id}.");
                }
            }
            else
            {
                string wanted = CloseSelector.Normalize(name!.Trim());
                foreach (Process candidate in Process.GetProcessesByName(wanted))
                {
                    if (ProcessGuards.CheckKillTarget(candidate.Id, ownPid) is null)
                    {
                        targets.Add(candidate);
                    }
                    else
                    {
                        candidate.Dispose();
                    }
                }

                if (targets.Count == 0)
                {
                    return OperationResult<IReadOnlyList<KillOutcome>>.Fail("not_found", $"No killable process named '{wanted}'.");
                }
            }

            List<KillOutcome> outcomes = [];
            foreach (Process target in targets)
            {
                string processName = SafeName(target);
                try
                {
                    target.Kill(entireTree);
                    target.WaitForExit(5000);
                    outcomes.Add(new KillOutcome(target.Id, processName, true, null));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
                {
                    outcomes.Add(new KillOutcome(target.Id, processName, false, ex.Message));
                }
            }

            return OperationResult<IReadOnlyList<KillOutcome>>.Ok(outcomes);
        }
        finally
        {
            foreach (Process target in targets)
            {
                target.Dispose();
            }
        }
    }

    private static DateTimeOffset? StartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static string SafeName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return string.Empty;
        }
    }
}
