using System.ComponentModel;

namespace Vestigo.Input;

/// <summary>Action kinds accepted by <c>send_input</c>.</summary>
public static class InputKinds
{
    public const string Move = "move";

    public const string Click = "click";

    public const string Down = "down";

    public const string Up = "up";

    public const string Scroll = "scroll";

    public const string Drag = "drag";

    public const string Type = "type";

    public const string Key = "key";

    public const string Wait = "wait";

    public static readonly IReadOnlyList<string> All = [Move, Click, Down, Up, Scroll, Drag, Type, Key, Wait];
}

/// <summary>One action as the MCP client writes it. Which fields apply depends on <see cref="Type" />.</summary>
public sealed class InputActionSpec
{
    [Description("move, click, down, up, scroll, drag, type, key or wait.")]
    public string Type { get; set; } = string.Empty;

    [Description("Target element id from inspect_screen or find_on_screen (needs captureId). Alternative to x and y.")]
    public int? ElementId { get; set; }

    [Description("X. Preview-image pixels of captureId when captureId is set, otherwise absolute virtual-screen physical pixels.")]
    public int? X { get; set; }

    [Description("Y. Same coordinate space as x.")]
    public int? Y { get; set; }

    [Description("drag only: destination element id (needs captureId). Alternative to toX and toY.")]
    public int? ToElementId { get; set; }

    [Description("drag only: destination X, same coordinate space as x.")]
    public int? ToX { get; set; }

    [Description("drag only: destination Y.")]
    public int? ToY { get; set; }

    [Description("click, down, up, drag: left (default), right or middle.")]
    public string? Button { get; set; }

    [Description("click only: 1 (default) to 3 clicks, for example 2 for a double click.")]
    public int? Count { get; set; }

    [Description("scroll only: wheel notches to scroll down (negative scrolls up).")]
    public int? DeltaY { get; set; }

    [Description("scroll only: wheel notches to scroll right (negative scrolls left).")]
    public int? DeltaX { get; set; }

    [Description("type only: text typed as Unicode characters.")]
    public string? Text { get; set; }

    [Description("key only: a key or chord such as enter, ctrl+c, ctrl+shift+esc, alt+f4 or win+r.")]
    public string? Keys { get; set; }

    [Description("wait only: milliseconds to pause.")]
    public int? Milliseconds { get; set; }

    [Description("click or key: milliseconds to hold the button or keys down before releasing them. 0 to 5000. Default 0.")]
    public int? HoldMilliseconds { get; set; }

    [Description("Milliseconds to wait after this action, 0 to 5000. Counted with wait and hold toward MaxWaitMilliseconds.")]
    public int? DelayAfterMilliseconds { get; set; }
}

/// <summary>One action after the service resolved element ids and preview coordinates to physical pixels.</summary>
public sealed record InputStep(
    string Kind,
    int? X,
    int? Y,
    int? ToX,
    int? ToY,
    string? Button,
    int Count,
    int DeltaX,
    int DeltaY,
    string? Text,
    IReadOnlyList<KeyStroke>? Keys,
    int Milliseconds,
    int HoldMilliseconds = 0,
    int DelayAfterMilliseconds = 0);

/// <summary>The batch handed to the session helper.</summary>
public sealed record InputBatch(IReadOnlyList<InputStep> Steps, string? FocusWindow);

/// <summary>Helper output of <c>send-input</c>.</summary>
public sealed record InputResult(
    int Performed,
    int SessionId,
    string? FocusWarning,
    int SubmittedEvents = 0,
    string? FocusRequested = null,
    string? FocusActual = null,
    bool? FocusGranted = null,
    int? CursorX = null,
    int? CursorY = null);
