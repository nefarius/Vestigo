using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using ModelContextProtocol;

using Vestigo.Extensibility;

namespace Vestigo.Plugins;

internal sealed class PluginRequestIdentity(IHttpContextAccessor http)
{
    public PluginToolContext Resolve(JsonObject? metadata)
    {
        HttpContext? request = http.HttpContext;
        string? owner = request?.User.Identity?.IsAuthenticated == true
            ? request.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
        string? header = null;
        if (request is not null && request.Request.Headers.TryGetValue(PluginSessionIdentity.HeaderName, out var values))
        {
            if (values.Count != 1) throw Invalid();
            header = values[0] ?? "";
        }
        string? meta = null;
        if (metadata is not null && metadata.TryGetPropertyValue(PluginSessionIdentity.MetadataKey, out JsonNode? node))
        {
            if (node is not JsonValue value || !value.TryGetValue(out meta) || meta is null) throw Invalid();
        }
        if (header is not null && meta is not null && header != meta) throw Invalid();
        string? session = header ?? meta;
        if (session is null) return new(owner, null);
        if (owner is null || !Regex.IsMatch(session, "\\A[A-Za-z0-9_-]{16,128}\\z", RegexOptions.CultureInvariant))
            throw Invalid();
        return new(owner, new(owner, session));
    }

    private static McpProtocolException Invalid() => new(
        "session_identity_invalid: supply one matching, authenticated session ID (16–128 letters, digits, '_' or '-').",
        McpErrorCode.InvalidParams);
}
