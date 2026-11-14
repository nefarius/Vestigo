using Bunit;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

using MudBlazor;
using MudBlazor.Services;

using Vestigo.Hosting.Components;

namespace Vestigo.Server.Tests;

public class ConfigQuantityInputTests
{
    [Fact]
    public async Task ArrowUp_ShowsTheCorrectedAmountAndClearsErrors()
    {
        await using BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        ArrowKeyInterceptor keys = new();
        context.Services.AddMudServices();
        context.Services.AddSingleton<IKeyInterceptorService>(keys);

        IRenderedComponent<QuantityHost> cut = context.Render<QuantityHost>();
        cut.Find("input").Input("abc");

        Assert.True(cut.Instance.Field.HasErrors);
        Assert.Equal(5m, cut.Instance.Amount);

        await cut.InvokeAsync(() => keys.PressAsync("ArrowUp"));

        Assert.Equal(6m, cut.Instance.Amount);
        Assert.Equal("6", cut.Find("input").GetAttribute("value"));
        Assert.False(cut.Instance.Field.HasErrors);
    }

    [Fact]
    public async Task ArrowUp_AtMaximum_ReplacesRejectedTextWithTheRetainedAmount()
    {
        await using BunitContext context = new();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        ArrowKeyInterceptor keys = new();
        context.Services.AddMudServices();
        context.Services.AddSingleton<IKeyInterceptorService>(keys);

        IRenderedComponent<QuantityHost> cut = context.Render<QuantityHost>(parameters => parameters.Add(host => host.Maximum, 5m));
        cut.Find("input").Input("abc");

        Assert.True(cut.Instance.Field.HasErrors);

        await cut.InvokeAsync(() => keys.PressAsync("ArrowUp"));

        Assert.Equal(5m, cut.Instance.Amount);
        Assert.Equal("5", cut.Find("input").GetAttribute("value"));
        Assert.False(cut.Instance.Field.HasErrors);
    }

    private sealed class QuantityHost : ComponentBase
    {
        [Parameter] public decimal? Maximum { get; set; }
        public decimal Amount { get; private set; } = 5m;
        public ConfigQuantityInput Field { get; private set; } = null!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ConfigQuantityInput>(0);
            builder.AddComponentParameter(1, nameof(ConfigQuantityInput.Immediate), true);
            builder.AddComponentParameter(2, nameof(ConfigQuantityInput.Step), 1m);
            builder.AddComponentParameter(3, nameof(ConfigQuantityInput.Value), Amount);
            builder.AddComponentParameter(4, nameof(ConfigQuantityInput.ValueChanged), EventCallback.Factory.Create<decimal>(this, value => Amount = value));
            if (Maximum is decimal maximum)
                builder.AddComponentParameter(5, nameof(ConfigQuantityInput.Max), maximum);
            builder.AddComponentReferenceCapture(6, component => Field = (ConfigQuantityInput)component);
            builder.CloseComponent();
        }
    }

    private sealed class ArrowKeyInterceptor : IKeyInterceptorService
    {
        private readonly Dictionary<string, IKeyDownObserver> _down = new(StringComparer.Ordinal);

        public Task PressAsync(string key) =>
            Task.WhenAll(_down.Values.Select(observer => observer.NotifyOnKeyDownAsync(new KeyboardEventArgs { Key = key, Type = "keydown" })));

        public Task SubscribeAsync(string elementId, KeyInterceptorOptions options, Action<KeyMapBuilder> configure)
        {
            KeyMapBuilder builder = KeyMapBuilder.Create();
            configure(builder);
            (IKeyDownObserver? down, _) = builder.Build();
            if (down is not null) _down[elementId] = down;
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(string elementId, KeyInterceptorOptions options, IKeyDownObserver? keyDown = null, IKeyUpObserver? keyUp = null)
        {
            if (keyDown is not null) _down[elementId] = keyDown;
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(IKeyInterceptorObserver observer, KeyInterceptorOptions options) => Task.CompletedTask;

        public Task SubscribeAsync(string elementId, KeyInterceptorOptions options, Action<KeyboardEventArgs>? keyDown = null, Action<KeyboardEventArgs>? keyUp = null) =>
            Task.CompletedTask;

        public Task SubscribeAsync(string elementId, KeyInterceptorOptions options, Func<KeyboardEventArgs, Task>? keyDown = null, Func<KeyboardEventArgs, Task>? keyUp = null) =>
            Task.CompletedTask;

        public Task DispatchAsync(string elementId, KeyEventKind kind, KeyboardEventArgs args)
        {
            if (kind == KeyEventKind.Down && _down.TryGetValue(elementId, out IKeyDownObserver? observer))
                return observer.NotifyOnKeyDownAsync(args);
            return Task.CompletedTask;
        }

        public Task UpdateKeyAsync(IKeyInterceptorObserver observer, KeyOptions option) => Task.CompletedTask;

        public Task UpdateKeyAsync(string elementId, KeyOptions option) => Task.CompletedTask;

        public Task UnsubscribeAsync(IKeyInterceptorObserver observer) => Task.CompletedTask;

        public Task UnsubscribeAsync(string elementId)
        {
            _down.Remove(elementId);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _down.Clear();
            return ValueTask.CompletedTask;
        }
    }
}
