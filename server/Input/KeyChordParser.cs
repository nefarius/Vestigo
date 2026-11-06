using System.Globalization;

namespace Vestigo.Input;

/// <summary>One virtual key to press; <paramref name="Extended" /> sets <c>KEYEVENTF_EXTENDEDKEY</c>.</summary>
public sealed record KeyStroke(ushort Vk, bool Extended);

/// <summary>Parses chords such as <c>ctrl+shift+esc</c> or <c>alt+f4</c> into virtual-key codes.</summary>
public static class KeyChordParser
{
    public const int MaxKeysPerChord = 6;

    private static readonly Dictionary<string, KeyStroke> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = new(0x11, false),
        ["control"] = new(0x11, false),
        ["shift"] = new(0x10, false),
        ["alt"] = new(0x12, false),
        ["win"] = new(0x5B, true),
        ["windows"] = new(0x5B, true),
        ["meta"] = new(0x5B, true),
        ["super"] = new(0x5B, true),
        ["cmd"] = new(0x5B, true),
        ["enter"] = new(0x0D, false),
        ["return"] = new(0x0D, false),
        ["esc"] = new(0x1B, false),
        ["escape"] = new(0x1B, false),
        ["tab"] = new(0x09, false),
        ["space"] = new(0x20, false),
        ["backspace"] = new(0x08, false),
        ["delete"] = new(0x2E, true),
        ["del"] = new(0x2E, true),
        ["insert"] = new(0x2D, true),
        ["ins"] = new(0x2D, true),
        ["home"] = new(0x24, true),
        ["end"] = new(0x23, true),
        ["pageup"] = new(0x21, true),
        ["pgup"] = new(0x21, true),
        ["pagedown"] = new(0x22, true),
        ["pgdn"] = new(0x22, true),
        ["up"] = new(0x26, true),
        ["down"] = new(0x28, true),
        ["left"] = new(0x25, true),
        ["right"] = new(0x27, true),
        ["capslock"] = new(0x14, false),
        ["printscreen"] = new(0x2C, true),
        ["menu"] = new(0x5D, true),
        ["apps"] = new(0x5D, true)
    };

    public static bool TryParse(string? chord, out IReadOnlyList<KeyStroke> keys, out string? error)
    {
        keys = [];
        error = null;
        if (string.IsNullOrWhiteSpace(chord))
        {
            error = "keys must not be empty.";
            return false;
        }

        List<KeyStroke> result = [];
        string trimmed = chord.Trim();
        List<string> tokens;
        if (trimmed == "+")
        {
            tokens = ["+"];
        }
        else if (trimmed.EndsWith("++", StringComparison.Ordinal))
        {
            // "ctrl++" is ctrl plus the plus key.
            tokens = [.. trimmed[..^2].Split('+', StringSplitOptions.TrimEntries), "+"];
        }
        else
        {
            tokens = [.. trimmed.Split('+', StringSplitOptions.TrimEntries)];
        }

        foreach (string token in tokens)
        {
            if (token.Length == 0)
            {
                error = $"keys '{chord}' has an empty segment.";
                return false;
            }

            if (!TryParseKey(token, out KeyStroke? key))
            {
                error = $"Unknown key '{token}'.";
                return false;
            }

            result.Add(key!);
        }

        if (result.Count > MaxKeysPerChord)
        {
            error = $"A chord can hold at most {MaxKeysPerChord} keys.";
            return false;
        }

        keys = result;
        return true;
    }

    private static bool TryParseKey(string token, out KeyStroke? key)
    {
        if (Named.TryGetValue(token, out key))
        {
            return true;
        }

        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                key = new KeyStroke(c, false);
                return true;
            }

            ushort? punctuation = c switch
            {
                '+' => 0xBB,
                ',' => 0xBC,
                '-' => 0xBD,
                '.' => 0xBE,
                ';' => 0xBA,
                '/' => 0xBF,
                '`' => 0xC0,
                '[' => 0xDB,
                '\\' => 0xDC,
                ']' => 0xDD,
                '\'' => 0xDE,
                '=' => 0xBB,
                _ => null
            };
            if (punctuation is { } vk)
            {
                key = new KeyStroke(vk, false);
                return true;
            }
        }

        if ((token[0] is 'f' or 'F') && int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int function)
            && function is >= 1 and <= 24)
        {
            key = new KeyStroke((ushort)(0x70 + function - 1), false);
            return true;
        }

        key = null;
        return false;
    }
}
