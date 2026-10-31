namespace Vestigo.Configuration;

/// <summary>
///     Settings for the <c>Processes</c> module, bound from <c>Vestigo:Processes</c>.
/// </summary>
public sealed class ProcessesOptions
{
    public const string SectionName = "Processes";

    /// <summary>
    ///     Allows <c>run_process</c> to resolve bare names such as <c>cmd.exe</c> from the service PATH.
    ///     When false only files inside roots with Execute access can be started.
    /// </summary>
    public bool AllowPathLookup { get; set; } = true;
}
