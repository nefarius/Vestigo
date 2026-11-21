using System.Text.Json.Nodes;

using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor.Services;

using Vestigo.Hosting.Components;

namespace Vestigo.Server.Tests;

public class ConfigLogonFieldsTests
{
    [Fact]
    public async Task SystemLogonGatesRenderHints()
    {
        await using BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        JsonObject values = new()
        {
            ["AllowAutoLogonChanges"] = false,
            ["AllowInteractiveLogon"] = false,
            ["LogonConsentLifetime"] = "00:05:00"
        };

        IRenderedComponent<ConfigFields> cut = context.Render<ConfigFields>(parameters => parameters
            .Add(field => field.Values, values)
            .Add(field => field.Prefix, "System"));

        Assert.Contains("consent", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Credential Provider", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("System:AllowAutoLogonChanges", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("System:AllowInteractiveLogon", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("System:LogonConsentLifetime", cut.Markup, StringComparison.Ordinal);
    }
}
