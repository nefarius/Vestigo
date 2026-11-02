namespace Vestigo.Etws;

/// <summary>Effective Trace-module settings that agents may inspect. Secrets are never included.</summary>
public sealed record TraceSettingsDescription(
    IReadOnlyList<string> InputRoots,
    IReadOnlyList<string> OutputRoots,
    bool UsesDefaultRoots,
    string? TracesRootName,
    string? TracesRootPath,
    string SymbolCache,
    string? SymbolServer,
    string SymbolServerSource,
    TraceAuthDescription Auth,
    IReadOnlyList<string> AttachableSessions,
    long MaxInputBytes,
    TimeSpan MaxCaptureDuration,
    int MaxEvents,
    int MaxArchiveEntries);

/// <summary>Whether symbol-server authentication is configured. Never includes the secret.</summary>
public sealed record TraceAuthDescription(string Scheme, bool Configured);

/// <summary>One binary's PDB identity and how it was resolved.</summary>
public sealed record BinarySymbolResolution(
    string BinaryPath,
    PdbReferenceDescription? Reference,
    string? PdbPath,
    string Origin,
    string? Failure,
    string? Detail);

/// <summary>Files extracted from a trace archive.</summary>
public sealed record TraceArchiveExtraction(
    string Destination,
    IReadOnlyList<string> ExtractedPaths,
    IReadOnlyList<string> SkippedEntries);
