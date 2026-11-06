namespace Vestigo.Input;

/// <summary>Settings for the Input module, bound from <c>Vestigo:Input</c>.</summary>
public sealed class InputOptions
{
    public const string SectionName = "Input";

    public const int DefaultMaxActions = 50;

    public const int MaxMaxActions = 200;

    public const int DefaultMaxWaitMilliseconds = 10_000;

    public const int MaxMaxWaitMilliseconds = 60_000;

    public const int DefaultMaxTypedCharacters = 2_000;

    public const int MaxMaxTypedCharacters = 8_000;

    /// <summary>Enables <c>send_input</c>. Off by default: injected input acts as the console user.</summary>
    public bool AllowInput { get; set; }

    /// <summary>Largest number of actions accepted in one <c>send_input</c> call.</summary>
    public int MaxActions { get; set; } = DefaultMaxActions;

    /// <summary>Largest total of all <c>wait</c> actions in one call, in milliseconds.</summary>
    public int MaxWaitMilliseconds { get; set; } = DefaultMaxWaitMilliseconds;

    /// <summary>Largest total of characters across all <c>type</c> actions in one call.</summary>
    public int MaxTypedCharacters { get; set; } = DefaultMaxTypedCharacters;

    public void Validate()
    {
        if (MaxActions is < 1 or > MaxMaxActions)
        {
            throw new InvalidOperationException($"Vestigo:Input:MaxActions must be between 1 and {MaxMaxActions}.");
        }

        if (MaxWaitMilliseconds is < 0 or > MaxMaxWaitMilliseconds)
        {
            throw new InvalidOperationException($"Vestigo:Input:MaxWaitMilliseconds must be between 0 and {MaxMaxWaitMilliseconds}.");
        }

        if (MaxTypedCharacters is < 1 or > MaxMaxTypedCharacters)
        {
            throw new InvalidOperationException($"Vestigo:Input:MaxTypedCharacters must be between 1 and {MaxMaxTypedCharacters}.");
        }
    }
}
