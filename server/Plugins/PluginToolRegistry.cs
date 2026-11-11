using System.Text.Json;
using System.Text.RegularExpressions;

using ModelContextProtocol;
using ModelContextProtocol.Protocol;

using Vestigo.Extensibility;

namespace Vestigo.Plugins;

internal sealed record PluginToolRegistryOptions(IReadOnlyList<string> Owners, IReadOnlySet<string> StaticNames);

/// <summary>One host-owned composition point; no replacement MCP server or runtime DI mutation.</summary>
internal sealed class PluginToolRegistry
{
    private sealed record Registration(IVestigoToolProvider Provider, string Prefix, bool RequiresIdentity, string Owner);
    private readonly IReadOnlyList<Registration> _providers;
    private readonly PluginRequestIdentity _identity;

    public PluginToolRegistry(IEnumerable<IVestigoToolProvider> providers, PluginToolRegistryOptions options, PluginRequestIdentity identity)
    {
        _identity = identity;
        IVestigoToolProvider[] resolved = providers.ToArray();
        if (resolved.Length != options.Owners.Count)
            throw new InvalidOperationException("provider_registration_invalid: register providers only through enabled module service hooks.");
        List<Registration> registrations = [];
        for (int index = 0; index < resolved.Length; index++)
        {
            string prefix;
            bool requiresIdentity;
            try { prefix = resolved[index].Prefix; requiresIdentity = resolved[index].RequiresSessionIdentity; }
            catch (Exception) { throw new InvalidOperationException($"provider_registration_invalid: metadata failed in {options.Owners[index]}."); }
            if (prefix is null || !Regex.IsMatch(prefix, "\\A[a-z][a-z0-9_]{0,30}_\\z", RegexOptions.CultureInvariant) ||
                registrations.Any(item => item.Prefix.StartsWith(prefix, StringComparison.Ordinal) || prefix.StartsWith(item.Prefix, StringComparison.Ordinal)) ||
                options.StaticNames.Any(name => name.StartsWith(prefix, StringComparison.Ordinal)))
                throw new InvalidOperationException($"provider_registration_invalid: conflicting or invalid prefix in {options.Owners[index]}.");
            registrations.Add(new(resolved[index], prefix, requiresIdentity, options.Owners[index]));
        }
        _providers = registrations;
    }

    public async ValueTask<ListToolsResult> ListAsync(ListToolsRequestParams? request, CancellationToken cancellationToken)
    {
        // The provider contract returns a complete catalog. Avoid mixing independent provider cursors.
        if (request?.Cursor is not null)
            throw new McpProtocolException("provider_cursor_invalid: the combined catalog is not paginated.", McpErrorCode.InvalidParams);
        PluginToolContext context = _identity.Resolve(request?.Meta);
        List<Tool> tools = [];
        foreach (Registration provider in _providers)
        {
            if (provider.RequiresIdentity && context.SessionIdentity is null) continue;
            tools.AddRange(await CatalogAsync(provider, context, cancellationToken));
        }
        return new() { Tools = tools };
    }

    public async ValueTask<CallToolResult> CallAsync(CallToolRequestParams? request, CancellationToken cancellationToken)
    {
        Registration? provider = request?.Name is not { } name ? null : _providers.FirstOrDefault(item => name.StartsWith(item.Prefix, StringComparison.Ordinal));
        if (provider is null)
            throw new McpProtocolException("provider_tool_unknown: no provider owns this tool.", McpErrorCode.InvalidParams);
        PluginToolContext context = _identity.Resolve(request!.Meta);
        if (provider.RequiresIdentity && context.SessionIdentity is null)
            throw new McpProtocolException("session_identity_required: this provider requires an authenticated owner and explicit session ID.", McpErrorCode.InvalidParams);
        IReadOnlyList<Tool> catalog = await CatalogAsync(provider, context, cancellationToken);
        if (!catalog.Any(tool => tool.Name == request.Name))
            throw new McpProtocolException("provider_tool_unknown: the tool is absent from the current provider catalog.", McpErrorCode.InvalidParams);
        CallToolRequestParams forwarded = Clone(request);
        forwarded.Name = request.Name[provider.Prefix.Length..];
        forwarded.Meta?.Remove(PluginSessionIdentity.MetadataKey);
        try
        {
            return await provider.Provider.CallToolAsync(forwarded, context, cancellationToken)
                ?? throw new InvalidOperationException("Null provider result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw Failed("provider_call_failed", provider); }
    }

    private static async ValueTask<IReadOnlyList<Tool>> CatalogAsync(Registration provider, PluginToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<Tool> supplied = await provider.Provider.ListToolsAsync(context, cancellationToken);
            List<Tool> catalog = [];
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (Tool definition in supplied)
            {
                Tool tool = Clone(definition);
                if (!Regex.IsMatch(tool.Name, "\\A[A-Za-z0-9_.-]+\\z", RegexOptions.CultureInvariant) ||
                    provider.Prefix.Length + tool.Name.Length > 128 || !names.Add(tool.Name) ||
                    tool.InputSchema.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("Invalid tool descriptor.");
                tool.Name = provider.Prefix + tool.Name;
                catalog.Add(tool);
            }
            return catalog;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw Failed("provider_catalog_invalid", provider); }
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    // Do not propagate plugin exception messages or inner exceptions into SDK logs.
    private static McpProtocolException Failed(string code, Registration provider) => new(
        $"{code}: {provider.Owner} could not complete the operation.", McpErrorCode.InternalError);
}
