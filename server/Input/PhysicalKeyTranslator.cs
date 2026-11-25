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

/// <summary>Win32 layout of a window's UI thread. Caps Lock inverts shift for A–Z inside <see cref="TryMap" />.</summary>
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
        if (_capsLock && vk is >= 0x41 and <= 0x5A)
        {
            shift = !shift;
        }

        uint scanEx = PInvoke.MapVirtualKeyEx(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC_EX, _layout);
        int prefix = (int)((scanEx >> 8) & 0xFF);
        bool extended = prefix is 0xE0 or 0xE1;
        return new PhysicalKeyStroke(vk, (ushort)(scanEx & 0xFF), extended, shift, control, alt);
    }

    public bool IsDeadKey(char value)
    {
        if (!TryScan(value, out ushort vk, out _))
        {
            return false;
        }

        uint mapped = PInvoke.MapVirtualKeyEx(vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_CHAR, _layout);
        return (mapped & 0x80000000) != 0;
    }

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
