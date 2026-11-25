using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Vestigo.Input;

/// <summary>One physical key, plus the modifiers that must be down while it is tapped.</summary>
internal sealed record PhysicalKeyStroke(ushort Vk, ushort Scan, bool Extended, bool Shift, bool Control, bool Alt);

/// <summary>Every key of one text action. Nothing has been sent yet.</summary>
internal sealed record PhysicalKeyPlan(IReadOnlyList<PhysicalKeyStroke> Strokes);

/// <summary>Why one character cannot be typed. <see cref="Position" /> is an index into the original string.</summary>
internal readonly record struct PhysicalKeyFailure(int Position, string Reason);

/// <summary>Character-to-key mapping for one keyboard layout. A null map means the character is not a single key.</summary>
internal interface IKeyboardLayout
{
    PhysicalKeyStroke? TryMap(char value);

    bool IsDeadKey(char value);
}

/// <summary>
///     Translates a whole string before any input is sent. Shift states other than shift, ctrl, and alt
///     (the VkKeyScanEx hankaku bit, for example) are rejected by the layout returning null.
/// </summary>
internal static class PhysicalKeyTranslator
{
    public static bool TryPlan(string text, IKeyboardLayout layout, out PhysicalKeyPlan? plan, out PhysicalKeyFailure? failure)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(layout);
        List<PhysicalKeyStroke> strokes = [];
        for (int index = 0; index < text.Length; index++)
        {
            char value = text[index];
            if (value == '\r')
            {
                continue;
            }

            if (value == '\n')
            {
                strokes.Add(new PhysicalKeyStroke(0x0D, 0, false, false, false, false));
                continue;
            }

            if (value == '\t')
            {
                strokes.Add(new PhysicalKeyStroke(0x09, 0, false, false, false, false));
                continue;
            }

            if (char.IsSurrogate(value))
            {
                plan = null;
                failure = new PhysicalKeyFailure(index, "non_bmp");
                return false;
            }

            if (layout.IsDeadKey(value))
            {
                plan = null;
                failure = new PhysicalKeyFailure(index, "dead_key");
                return false;
            }

            // A null map includes shift-state bits other than shift, ctrl, and alt.
            if (layout.TryMap(value) is not { } stroke)
            {
                plan = null;
                failure = new PhysicalKeyFailure(index, "unmappable");
                return false;
            }

            strokes.Add(stroke);
        }

        plan = new PhysicalKeyPlan(strokes);
        failure = null;
        return true;
    }
}

/// <summary>Win32 layout of a window's UI thread. Caps Lock compensation follows the character the layout produces.</summary>
internal sealed class WindowsKeyboardLayout : IKeyboardLayout
{
    private readonly HKL _layout;
    private readonly bool _capsLock;

    public WindowsKeyboardLayout(HWND window)
    {
        uint threadId = 0;
        if (!window.IsNull)
        {
            threadId = PInvoke.GetWindowThreadProcessId(window, out _);
        }

        _layout = PInvoke.GetKeyboardLayout(threadId);
        _capsLock = (PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_CAPITAL) & 1) != 0;
    }

    public bool CapsLock => _capsLock;

    public PhysicalKeyStroke? TryMap(char value)
    {
        if (!TryScan(value, out ushort vk, out int shiftState) || (shiftState & ~0x07) != 0)
        {
            return null;
        }

        bool shift = (shiftState & 1) != 0;
        bool control = (shiftState & 2) != 0;
        bool alt = (shiftState & 4) != 0;
        shift = SelectShift(value, vk, shift, control, alt);

        uint scanEx = PInvoke.MapVirtualKeyEx(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC_EX, _layout);
        int prefix = (int)((scanEx >> 8) & 0xFF);
        bool extended = prefix is 0xE0 or 0xE1;
        return new PhysicalKeyStroke(vk, (ushort)(scanEx & 0xFF), extended, shift, control, alt);
    }

    public bool IsDeadKey(char value)
    {
        if (!TryScan(value, out ushort vk, out int shiftState) || (shiftState & ~0x07) != 0)
        {
            return false;
        }

        bool shift = SelectShift(value, vk, (shiftState & 1) != 0, (shiftState & 2) != 0, (shiftState & 4) != 0);
        KeySample sample = Sample(vk, shift, (shiftState & 2) != 0, (shiftState & 4) != 0);
        if (sample.Known)
        {
            return sample.Dead;
        }

        uint mapped = PInvoke.MapVirtualKeyEx(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_CHAR, _layout);
        return !shift && (mapped & 0x80000000) != 0;
    }

    private bool SelectShift(char value, ushort vk, bool shift, bool control, bool alt)
    {
        if (!_capsLock)
        {
            return shift;
        }

        KeySample scanned = Sample(vk, shift, control, alt);
        KeySample flipped = Sample(vk, !shift, control, alt);
        if (scanned.Known && flipped.Known)
        {
            return scanned.Character == value ? shift : flipped.Character == value ? !shift : shift;
        }

        return vk is >= 0x41 and <= 0x5A ? !shift : shift;
    }

    private KeySample Sample(ushort vk, bool shift, bool control, bool alt)
    {
        Span<byte> state = stackalloc byte[256];
        if (_capsLock)
        {
            state[(int)VIRTUAL_KEY.VK_CAPITAL] = 1;
        }

        if (shift)
        {
            state[(int)VIRTUAL_KEY.VK_SHIFT] = 0x80;
        }

        if (control)
        {
            state[(int)VIRTUAL_KEY.VK_CONTROL] = 0x80;
        }

        if (alt)
        {
            state[(int)VIRTUAL_KEY.VK_MENU] = 0x80;
        }

        Span<char> buffer = stackalloc char[8];
        int written;
        unsafe
        {
            fixed (byte* statePointer = state)
            fixed (char* bufferPointer = buffer)
            {
                written = PInvoke.ToUnicodeEx(vk, 0, statePointer, bufferPointer, buffer.Length, 0, _layout);
                if (written < 0)
                {
                    // Clear the dead-key state on this helper thread. The console user's composition state belongs to another thread.
                    _ = PInvoke.ToUnicodeEx(vk, 0, statePointer, bufferPointer, buffer.Length, 0, _layout);
                }
            }
        }

        return written switch
        {
            < 0 => new KeySample(true, null, true),
            0 => new KeySample(false, null, false),
            _ => new KeySample(false, buffer[0], true)
        };
    }

    private readonly record struct KeySample(bool Dead, char? Character, bool Known);

    private bool TryScan(char value, out ushort vk, out int shiftState)
    {
        short scanned = PInvoke.VkKeyScanEx(value, _layout);
        if (scanned == -1)
        {
            vk = 0;
            shiftState = 0;
            return false;
        }

        vk = (ushort)(scanned & 0xFF);
        shiftState = (scanned >> 8) & 0xFF;
        return true;
    }
}
