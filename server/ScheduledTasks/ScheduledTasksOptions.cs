namespace Vestigo.ScheduledTasks;

/// <summary>
///     Settings for the ScheduledTasks module, bound from <c>Vestigo:ScheduledTasks</c>.
/// </summary>
public sealed class ScheduledTasksOptions
{
    public const string SectionName = "ScheduledTasks";

    /// <summary>Enables run, stop, enable, disable, and delete. Listing, inspecting, and history work regardless.</summary>
    public bool AllowChanges { get; set; }

    /// <summary>
    ///     Case-insensitive task path prefixes that delete and disable refuse. Configuration entries are added
    ///     to these defaults because the configuration binder appends to existing lists.
    /// </summary>
    public List<string> ProtectedPaths { get; set; } = ["\\Microsoft\\Windows\\"];

    public void Validate()
    {
        foreach (string path in ProtectedPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("Vestigo:ScheduledTasks:ProtectedPaths must not contain empty paths.");
            }

            if (!path.Trim().StartsWith('\\'))
            {
                throw new InvalidOperationException("Vestigo:ScheduledTasks:ProtectedPaths entries must start with a backslash.");
            }
        }
    }

    public bool IsProtected(string path) =>
        ProtectedPaths.Any(prefix => path.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase));
}
