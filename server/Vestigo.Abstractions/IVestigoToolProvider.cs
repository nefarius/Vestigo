using ModelContextProtocol.Protocol;

namespace Vestigo.Extensibility;

/// <summary>
/// Optional API 1.1 tool source. Register as a singleton IVestigoToolProvider in an enabled
/// module's ConfigureServices. The host owns routing and prefixes names without changing schemas.
/// </summary>
public interface IVestigoToolProvider
{
    /// <summary>Exclusive namespace, e.g. "example_". Stable for the provider's lifetime.</summary>
    string Prefix { get; }

    /// <summary>When true, tools are hidden and calls rejected without authenticated session identity.</summary>
    bool RequiresSessionIdentity => false;

    /// <summary>Return the complete current catalog with unprefixed names; honor cancellation and bound I/O.</summary>
    ValueTask<IReadOnlyList<Tool>> ListToolsAsync(PluginToolContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Receives an unprefixed name, original arguments and protocol metadata (except host session metadata).
    /// Return the full result, including content, structured content and IsError. Never replay calls implicitly.
    /// </summary>
    ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams request, PluginToolContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Host-authenticated ownership plus an explicit caller-selected session. Use this entire value as
/// the isolation key, never SessionId alone. This is application identity, not an MCP transport session.
/// Providers own resource leases, concurrency and cleanup. Identity does not grant additional permissions.
/// </summary>
public sealed record PluginSessionIdentity(string OwnerId, string SessionId)
{
    public const string HeaderName = "X-Vestigo-Session-Id";
    public const string MetadataKey = "vestigo/sessionId";
}

/// <summary>Per-request host context. OwnerId is opaque, credential-specific and changes on host restart.</summary>
public sealed record PluginToolContext(string? OwnerId, PluginSessionIdentity? SessionIdentity);
