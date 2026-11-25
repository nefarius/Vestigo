using System.Collections;
using System.Reflection;

using Bunit;

using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

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

    [Theory]
    [InlineData("dark", true)]
    [InlineData("light", false)]
    public async Task SavedThemeIsRevealedOnlyAfterTheProviderRendersIt(string stored, bool dark)
    {
        await using BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        ThemeReveal reveal = WatchReveal(context, stored, systemDark: false, readFails: false);

        context.Render<ConfigThemeToggle>();

        Assert.True(reveal.ReadNoted);
        Assert.True(reveal.StartedLight);
        Assert.Equal(dark, reveal.Dark);
    }

    [Fact]
    public async Task SystemDarkThemeIsRevealedOnlyAfterTheProviderRendersIt()
    {
        await using BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        ThemeReveal reveal = WatchReveal(context, stored: null, systemDark: true, readFails: false);

        context.Render<ConfigThemeToggle>();

        Assert.True(reveal.ReadNoted);
        Assert.True(reveal.StartedLight);
        Assert.True(reveal.Dark);
        Assert.True(reveal.FollowsSystem);
    }

    [Fact]
    public async Task UnreadableThemeStillRevealsTheInitialPalette()
    {
        await using BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddMudServices();
        ThemeReveal reveal = WatchReveal(context, stored: null, systemDark: false, readFails: true);

        context.Render<ConfigThemeToggle>();

        Assert.True(reveal.ReadNoted);
        Assert.True(reveal.StartedLight);
        Assert.False(reveal.Dark);
    }

    private static ThemeReveal WatchReveal(BunitContext context, string? stored, bool systemDark, bool readFails)
    {
        ThemeReveal reveal = new();
        JSRuntimeInvocationHandler<string?> read = context.JSInterop.Setup<string?>("vestigoTheme.read", _ =>
        {
            reveal.NoteRead(context.Renderer);
            return true;
        });
        if (readFails)
            read.SetException(new JSException("storage unavailable"));
        else
            read.SetResult(stored);

        context.JSInterop.Setup<bool>("vestigoTheme.systemDark").SetResult(systemDark);
        context.JSInterop.SetupVoid("vestigoTheme.reveal", _ =>
        {
            reveal.Capture(context);
            return true;
        });
        return reveal;
    }

    private sealed class ThemeReveal
    {
        public bool ReadNoted { get; private set; }
        public bool? StartedLight { get; private set; }
        public bool? Dark { get; private set; }
        public bool? FollowsSystem { get; private set; }

        public void NoteRead(object renderer)
        {
            if (ReadNoted)
                return;

            ReadNoted = true;
            StartedLight = !FindProvider(renderer).IsDarkMode;
        }

        public void Capture(BunitContext context)
        {
            MudThemeProvider provider = FindProvider(context.Renderer);
            Dark = provider.IsDarkMode;
            FollowsSystem = provider.ObserveSystemDarkModeChange;
        }

        private static MudThemeProvider FindProvider(object renderer)
        {
            FieldInfo field = typeof(Renderer).GetField("_componentStateByComponent", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The Blazor renderer did not expose its component map.");
            foreach (DictionaryEntry entry in (IDictionary)field.GetValue(renderer)!)
            {
                if (entry.Key is MudThemeProvider provider)
                    return provider;
            }

            throw new InvalidOperationException("MudThemeProvider was not rendered.");
        }
    }
}
