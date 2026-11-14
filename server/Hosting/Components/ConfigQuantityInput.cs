using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

using MudBlazor;

namespace Vestigo.Hosting.Components;

/// <summary>Numeric input that reports text MudBlazor cannot parse instead of committing a fallback number.</summary>
public sealed class ConfigQuantityInput : MudNumericField<decimal>
{
    [Parameter] public EventCallback<string> ConversionFailed { get; set; }
    private string? _failure;
    private bool _textInvalid;

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
            string error = string.IsNullOrWhiteSpace(_failure) ? ConversionErrorMessage ?? ConfigurationQuantity.NumberParseError : _failure;
            _failure = null;
            _textInvalid = true;
            await ConversionFailed.InvokeAsync(error);
            return;
        }

        _failure = null;
        bool unchanged = EqualityComparer<decimal?>.Default.Equals(ReadValue, parsed);
        _textInvalid = false;
        await base.UpdateValuePropertyAsync(updateText);
        // MudBlazor skips ValueChanged when the parsed amount matches the current value, which would leave a prior parse error in place.
        if (unchanged) await ValueChanged.InvokeAsync(parsed);
    }

    protected override async Task OnBlurredAsync(FocusEventArgs obj)
    {
        await base.OnBlurredAsync(obj);
        if (!_textInvalid) return;
        _failure = null;
        decimal? converted = ConvertGet(ReadText);
        if (!TryAcceptParsedAmount(_failure is not null || ConversionError, converted, out decimal parsed)) return;
        _failure = null;
        _textInvalid = false;
        await ValueChanged.InvokeAsync(parsed);
    }
}
