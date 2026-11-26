namespace Vestigo.Performance;

/// <summary>Allow-listed kernel ETW focus groups.</summary>
public static class KernelTraceGroups
{
    public const string DiskIo = "disk_io";
    public const string FileIo = "file_io";
    public const string DpcIsr = "dpc_isr";
    public const string Scheduler = "scheduler";
    public const string HardFaults = "hard_faults";
    public const string Network = "network";

    public static readonly IReadOnlyList<string> All = [DiskIo, FileIo, DpcIsr, Scheduler, HardFaults, Network];

    public static OperationResult<IReadOnlyList<string>> Parse(IReadOnlyList<string>? groups)
    {
        if (groups is null || groups.Count == 0)
        {
            return OperationResult<IReadOnlyList<string>>.Ok(All);
        }

        List<string> selected = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string group in groups)
        {
            if (string.IsNullOrWhiteSpace(group))
            {
                return OperationResult<IReadOnlyList<string>>.Fail("invalid_request", "A kernel trace group name is blank.");
            }

            string match = All.FirstOrDefault(item => string.Equals(item, group.Trim(), StringComparison.OrdinalIgnoreCase))
                           ?? string.Empty;
            if (match.Length == 0)
            {
                return OperationResult<IReadOnlyList<string>>.Fail(
                    "invalid_request",
                    $"Unknown kernel trace group '{group}'. Use disk_io, file_io, dpc_isr, scheduler, hard_faults, or network.");
            }

            if (seen.Add(match))
            {
                selected.Add(match);
            }
        }

        return OperationResult<IReadOnlyList<string>>.Ok(selected);
    }

    public static bool Includes(IReadOnlyList<string> groups, string name) =>
        groups.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
}
