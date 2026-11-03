using Vestigo.Devices;
using Vestigo.EventLogs;
using Vestigo.Services;
using Vestigo.SystemInfo;

namespace Vestigo.Diagnostics;

/// <summary>A fact the explanation is allowed to cite, with the place it came from.</summary>
public sealed record ObservedFact(string Source, string Fact, DateTimeOffset? At);

/// <summary>A labeled interpretation tied to evidence already present in <see cref="DeviceProblemReport.Observed" />.</summary>
public sealed record ProblemHypothesis(string Label, string Statement, IReadOnlyList<string> Evidence);

/// <summary>
///     Documented meaning of a problem code, plus hypotheses that cite observed evidence.
///     <see cref="InsufficientEvidence" /> is true when the code is outside the rule table.
/// </summary>
public sealed record DeviceProblemReport(
    string InstanceId,
    uint? ProblemCode,
    IReadOnlyList<ObservedFact> Observed,
    string? Explanation,
    IReadOnlyList<ProblemHypothesis> Hypotheses,
    bool InsufficientEvidence);

/// <summary>Evidence collected before the rule table runs. Null probes mean that source was not available.</summary>
internal sealed record DeviceProblemEvidence(
    DeviceDetails Device,
    ClassFilterSet? ClassFilters,
    string? ClassFilterNote,
    ServiceInfo? Service,
    string? ServiceNote,
    BootSecurityResult? BootSecurity,
    string? BootSecurityNote,
    IReadOnlyList<EventEntry> PnpEvents,
    string? PnpNote,
    InstallHistory? InstallHistory,
    string? InstallNote);
