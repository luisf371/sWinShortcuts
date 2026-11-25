using System;
using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class MacroSettings
{
    public bool IsEnabled { get; set; }

    // Publish complete, privately owned arrays; never edit a published definition or step in place.
    public MacroDefinition[] Definitions { get; set; } = [];

    public string? LoadError { get; set; }
}

public sealed record MacroDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Label { get; init; } = "New macro";
    public bool IsEnabled { get; init; }
    public bool ToggleMode { get; init; }
    // Off: playback waits for the shortcut and its modifiers to be released. On: it starts at the press.
    public bool StartOnPress { get; init; }
    public bool CancelOnMouseMovement { get; init; }
    public Key ShortcutKey { get; init; } = Key.None;
    public MouseButton? ShortcutMouseButton { get; init; }
    public InputTrigger ShortcutTrigger => ShortcutMouseButton is { } button
        ? InputTrigger.FromMouseButton(button) : InputTrigger.FromKey(ShortcutKey);
    public ModifierKeys ShortcutModifiers { get; init; }
    // The ShortcutModifiers flags held on the right-hand key; the others are the left key. Older files have none.
    public ModifierKeys ShortcutRightModifiers { get; init; }
    public MacroStep[] Steps { get; init; } = [];

    public MacroDefinition Duplicate() => this with
    {
        Id = Guid.NewGuid(),
        IsEnabled = false,
        ShortcutKey = Key.None,
        ShortcutMouseButton = null,
        ShortcutModifiers = ModifierKeys.None,
        ShortcutRightModifiers = ModifierKeys.None,
        Steps = (MacroStep[])Steps.Clone()
    };
}

public readonly record struct MacroStep
{
    public MacroStepKind Kind { get; init; }
    public Key Key { get; init; }
    public MouseButton? MouseButton { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int Slot { get; init; }
    public int DurationMs { get; init; }
    public int WheelDelta { get; init; }
    public bool HorizontalWheel { get; init; }
    // Move steps only (MoveTo, MoveCursorBy, MoveMouseBy, MoveToSaved). Mouse click always stops.
    public MacroHeldButtonPolicy HeldButtons { get; init; }
}

/// <summary>What a move step does while a physical mouse button (other than the macro's own shortcut) is held.</summary>
public enum MacroHeldButtonPolicy
{
    Stop,
    Wait,
    Allow
}

public enum MacroStepKind
{
    KeyPress,
    KeyDown,
    KeyUp,
    Wait,
    MouseClick,
    MouseDown,
    MouseUp,
    MoveTo,
    MouseWheel,
    FreezeMouse,
    UnfreezeMouse,
    // X/Y are an offset. MoveCursorBy moves the on-screen cursor (absolute input, arrival-checked);
    // MoveMouseBy sends device-like relative motion that games read as mouse movement.
    MoveCursorBy,
    MoveMouseBy,
    SavePosition,
    MoveToSaved
}

public enum MacroSessionMode
{
    Idle,
    PreparingPlayback,
    WaitingForShortcutRelease,
    WaitingForPhysicalModifiers,
    Playing,
    PreparingRecording,
    Recording,
    Finishing,
    Faulted,
    WaitingForRecordingTarget,
    WaitingForMouseRelease
}

public readonly record struct MacroSessionSnapshot(
    long SessionId,
    Profile? OwnerProfile,
    Guid MacroId,
    MacroSessionMode Mode,
    TimeSpan Elapsed,
    int RowCount,
    string? FailureReason = null);

public enum MacroRecordingEndReason
{
    Stopped,
    RowLimit,
    DurationLimit,
    Interrupted,
    Cancelled,
    Faulted
}

public sealed record MacroRecordingResult(
    long SessionId,
    Profile OwnerProfile,
    Guid MacroId,
    MacroStep[] Steps,
    MacroRecordingEndReason EndReason,
    bool AppendedBalancingReleases,
    string? FailureReason = null);
