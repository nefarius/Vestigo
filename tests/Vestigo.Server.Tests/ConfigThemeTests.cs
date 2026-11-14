using Bunit;

using Microsoft.Extensions.DependencyInjection;

using MudBlazor;
using MudBlazor.Services;

using Vestigo.Hosting.Components;

namespace Vestigo.Server.Tests;

public class ConfigThemeTests
{
    [Theory]
    [InlineData("dark", true)]
    [InlineData("light", false)]
    [InlineData("system", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void StoredThemeAcceptsOnlyLightAndDark(string? stored, bool? dark) =>
        Assert.Equal(dark, ConfigThemePreference.Parse(stored));

    [Theory]
    [InlineData(true, "dark")]
    [InlineData(false, "light")]
    public void StoredThemeUsesStableNames(bool dark, string stored) =>
        Assert.Equal(stored, ConfigThemePreference.Format(dark));

    [Fact]
    public async Task SavedDarkModeIsRestoredAndTheOppositeChoiceIsStored()
    {
        await using BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.JSInterop.Setup<string?>("vestigoTheme.read").SetResult("dark");
        var invocation = context.JSInterop.SetupVoid("vestigoTheme.write", "light");

        IRenderedComponent<ConfigThemeToggle> cut = context.Render<ConfigThemeToggle>();

        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<MudSwitch<bool>>().Instance.Value));
        Assert.False(cut.FindComponent<MudThemeProvider>().Instance.ObserveSystemDarkModeChange);
        Assert.True(cut.FindComponent<MudThemeProvider>().Instance.IsDarkMode);

        cut.Find("input[type=checkbox]").Change(false);

        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<MudSwitch<bool>>().Instance.Value));
        Assert.False(cut.FindComponent<MudThemeProvider>().Instance.IsDarkMode);
        invocation.VerifyInvoke("vestigoTheme.write");
    }

    [Fact]
    public async Task MissingSavedThemeFollowsTheSystem()
    {
        await using BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.JSInterop.Setup<string?>("vestigoTheme.read").SetResult("nope");
        context.JSInterop.Setup<bool>("vestigoTheme.systemDark").SetResult(true);

        IRenderedComponent<ConfigThemeToggle> cut = context.Render<ConfigThemeToggle>();

        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<MudSwitch<bool>>().Instance.Value));
        Assert.True(cut.FindComponent<MudThemeProvider>().Instance.ObserveSystemDarkModeChange);
        Assert.True(cut.FindComponent<MudThemeProvider>().Instance.IsDarkMode);
    }

    [Fact]
    public async Task SystemThemeChangeUpdatesTheDocumentWithoutSaving()
    {
        await using BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        context.JSInterop.Setup<string?>("vestigoTheme.read").SetResult(null);
        context.JSInterop.Setup<bool>("vestigoTheme.systemDark").SetResult(false);
        var apply = context.JSInterop.SetupVoid("vestigoTheme.apply", "dark");
        apply.SetVoidResult();

        IRenderedComponent<ConfigThemeToggle> cut = context.Render<ConfigThemeToggle>();
        cut.WaitForAssertion(() => Assert.False(cut.FindComponent<MudSwitch<bool>>().Instance.Value));

        await cut.InvokeAsync(() => cut.FindComponent<MudThemeProvider>().Instance.SystemDarkModeChangedAsync(true));

        cut.WaitForAssertion(() => Assert.True(cut.FindComponent<MudSwitch<bool>>().Instance.Value));
        apply.VerifyInvoke("vestigoTheme.apply");
        Assert.DoesNotContain(context.JSInterop.Invocations, call => call.Identifier == "vestigoTheme.write");
    }
}
