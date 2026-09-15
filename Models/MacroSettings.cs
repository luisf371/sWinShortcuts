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
    public bool CancelOnMouseMovement { get; init; }
    public Key ShortcutKey { get; init; } = Key.None;
    public MouseButton? ShortcutMouseButton { get; init; }
    public InputTrigger ShortcutTrigger => ShortcutMouseButton is { } button
        ? InputTrigger.FromMouseButton(button) : InputTrigger.FromKey(ShortcutKey);
    public ModifierKeys ShortcutModifiers { get; init; }
    public MacroStep[] Steps { get; init; } = [];

    public MacroDefinition Duplicate() => this with
    {
        Id = Guid.NewGuid(),
        IsEnabled = false,
        ShortcutKey = Key.None,
        ShortcutMouseButton = null,
        ShortcutModifiers = ModifierKeys.None,
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
    public int DurationMs { get; init; }
    public int WheelDelta { get; init; }
    public bool HorizontalWheel { get; init; }
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
    MouseWheel
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
    Faulted
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
