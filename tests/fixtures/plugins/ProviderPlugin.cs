using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using Vestigo.Extensibility;

namespace Vestigo.PluginFixture;

public sealed class ProviderPlugin : IVestigoPlugin
{
    public IReadOnlyList<VestigoModule> Modules => [new ProviderModule()];
}

public sealed class ProviderModule : VestigoModule
{
    public override string Name => "ProviderFixture";
    public override string Description => "Protocol tool provider fixture; no external process or browser.";
    public override IReadOnlyList<Type> ToolTypes => [];
    public override void ConfigureServices(IServiceCollection services, PluginContext context)
    {
        services.AddSingleton<IVestigoToolProvider>(_ => new FixtureProvider(
            context.Settings["Prefix"] ?? "fixture_",
            !bool.TryParse(context.Settings["RequiresSessionIdentity"], out bool required) || required,
            context.Settings["DisposeMarker"]));
        if (context.Settings["SecondPrefix"] is { } second)
            services.AddSingleton<IVestigoToolProvider>(_ => new FixtureProvider(second, false, null));
        if (bool.TryParse(context.Settings["ReplaceHandlers"], out bool replace) && replace)
            services.Configure<McpServerOptions>(options => options.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [] }));
    }
}

public sealed class FixtureProvider(string prefix, bool requiresIdentity, string? disposeMarker) : IVestigoToolProvider, IDisposable
{
    private readonly ConcurrentDictionary<PluginSessionIdentity, int> _calls = new();
    private bool _updated;
    public string Prefix => prefix;
    public bool RequiresSessionIdentity => requiresIdentity;

    public ValueTask<IReadOnlyList<Tool>> ListToolsAsync(PluginToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Tool tool = JsonSerializer.Deserialize<Tool>("""
            {"name":"echo","title":"Provider echo","description":"Echo arbitrary JSON through a protocol provider.",
             "inputSchema":{"type":"object","properties":{"value":{},"fail":{"type":"boolean"},"rename":{"type":"boolean"}}},
             "outputSchema":{"type":"object"},"annotations":{"readOnlyHint":true},"_meta":{"fixture":"metadata"}}
            """)!;
        if (_updated) tool.Name = "updated";
        return ValueTask.FromResult<IReadOnlyList<Tool>>([tool]);
    }

    public ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams request, PluginToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int count = context.SessionIdentity is { } identity ? _calls.AddOrUpdate(identity, 1, (_, previous) => previous + 1) : 0;
        if (request.Arguments?.TryGetValue("rename", out JsonElement rename) == true && rename.GetBoolean()) _updated = true;
        bool fail = request.Arguments?.TryGetValue("fail", out JsonElement failure) == true && failure.GetBoolean();
        CallToolResult result = JsonSerializer.Deserialize<CallToolResult>(JsonSerializer.Serialize(new
        {
            content = new object[]
            {
                new { type = "text", text = request.Name },
                new { type = "image", data = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6R3sAAAAASUVORK5CYII=", mimeType = "image/png" }
            },
            structuredContent = new { owner = context.OwnerId, session = context.SessionIdentity?.SessionId, count, arguments = request.Arguments, meta = request.Meta },
            isError = fail,
            _meta = new { fixture = "result metadata" }
        }))!;
        return ValueTask.FromResult(result);
    }

    public void Dispose()
    {
        if (disposeMarker is not null) File.WriteAllText(disposeMarker, "disposed");
    }
}
