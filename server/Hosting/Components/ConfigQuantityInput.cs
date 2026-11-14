using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

using MudBlazor;

namespace Vestigo.Hosting.Components;

/// <summary>Numeric input that reports text MudBlazor cannot parse instead of committing a fallback number.</summary>
public sealed class ConfigQuantityInput : MudNumericField<decimal>
{
    [Parameter] public EventCallback<string> ConversionFailed { get; set; }
    private string? _failure;
    private bool _rejected;

    /// <summary>A failed conversion does not yield an amount, including zero.</summary>
    internal static bool TryAcceptParsedAmount(bool conversionFailed, decimal? converted, out decimal amount)
    {
        if (conversionFailed || converted is not decimal parsed)
        {
            amount = default;
            return false;
        }

        amount = parsed;
        return true;
    }

    /// <summary>A spin step that keeps the retained amount still has to clear a rejected entry.</summary>
    internal static bool NotifyUnchangedExplicitValue(bool wasRejected, decimal? requested, decimal? current) =>
        wasRejected && requested is not null && EqualityComparer<decimal?>.Default.Equals(requested, current);

    protected override void OnConversionErrorOccurred(string error)
    {
        base.OnConversionErrorOccurred(error);
        _failure = error;
    }

    protected override async Task UpdateValuePropertyAsync(bool updateText)
    {
        _failure = null;
        decimal? converted = ConvertGet(ReadText);
        if (!TryAcceptParsedAmount(_failure is not null || ConversionError, converted, out decimal parsed))
        {
            await RejectTextAsync();
            return;
        }

        _failure = null;
        _rejected = false;
        bool unchanged = EqualityComparer<decimal?>.Default.Equals(ReadValue, parsed);
        await base.UpdateValuePropertyAsync(updateText);
        // MudBlazor skips ValueChanged when the parsed amount matches the current value, which would leave a prior parse error in place.
        if (unchanged) await ValueChanged.InvokeAsync(parsed);
    }

    protected override async Task OnBlurredAsync(FocusEventArgs obj)
    {
        // Parse the text the user left in the field. The inherited blur formats from the last accepted value and would hide a rejected entry.
        string? original = ReadText;
        _failure = null;
        decimal? converted = ConvertGet(original);
        if (!TryAcceptParsedAmount(_failure is not null || ConversionError, converted, out _))
        {
            _isFocused = false;
            await RejectTextAsync();
            return;
        }

        _failure = null;
        _rejected = false;
        await base.OnBlurredAsync(obj);
    }

    protected override async Task SetValueAndUpdateTextAsync(decimal value, bool updateText = true, bool force = false)
    {
        // Spin buttons publish this value directly. Drop the rejected text so the field shows the accepted amount.
        bool notifyUnchanged = NotifyUnchangedExplicitValue(_rejected, value, ReadValue);
        _rejected = false;
        _failure = null;
        // Drop the failed parse before validation runs, or it is copied onto the corrected value.
        ResetConverterErrors();
        await base.SetValueAndUpdateTextAsync(value, updateText, force);
        // A spin that stays on the current amount, such as ArrowUp at Max, returns before MudBlazor replaces the text.
        if (notifyUnchanged)
            await base.UpdateTextPropertyAsync(false);
        await ReparseAcceptedTextAsync();
        if (notifyUnchanged) await ValueChanged.InvokeAsync(value);
    }

    private async Task ReparseAcceptedTextAsync()
    {
        _failure = null;
        _ = ConvertGet(ReadText);
        if (_failure is not null || ConversionError)
        {
            // Keep the accepted amount when the formatted text does not round-trip.
            _failure = null;
            ResetConverterErrors();
        }

        await ValidateValue();
    }

    protected override Task UpdateTextPropertyAsync(bool updateValue)
    {
        // Formatting is derived from the retained value. Keep a rejected entry on screen instead of restoring that value.
        if (_rejected) return Task.CompletedTask;
        return base.UpdateTextPropertyAsync(updateValue);
    }

    private async Task RejectTextAsync()
    {
        string error = string.IsNullOrWhiteSpace(_failure) ? ConversionErrorMessage ?? ConfigurationQuantity.NumberParseError : _failure;
        _failure = null;
        _rejected = true;
        await ConversionFailed.InvokeAsync(error);
    }
}
