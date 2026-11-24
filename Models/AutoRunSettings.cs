using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class AutoRunSettings
{
    public bool IsEnabled { get; set; } = false;

    // None selects a single-key trigger. Otherwise any combination of the Ctrl/Alt/Shift/Win flags; every flag
    // must be held. Persisted by IniExtensions.Get/SetModifierKeys ("Alt+Control").
    public ModifierKeys TriggerModifier { get; set; } = ModifierKeys.Control;

    // The TriggerModifier flags held on the right-hand key; the others are the left key (LCtrl+R by default).
    // Files from before sides were recorded have none, so their modifiers are left.
    public ModifierKeys TriggerRightModifiers { get; set; }

    public Key TriggerKey { get; set; } = Key.R;

    public bool SprintEnabled { get; set; } = false;

    public Key SprintKey { get; set; } = Key.LeftShift;

    public SprintActivation SprintMode { get; set; } = SprintActivation.Hold;

    // Background = experimental focus-independent PostMessage transport (game-dependent; a no-op
    // for Unreal/DirectInput/raw-input games). Foreground = the reliable SendInput path.
    public AutoRunSendMode SendMode { get; set; } = AutoRunSendMode.Foreground;
}

public enum SprintActivation
{
    Hold = 0,
    Press = 1
}

public enum AutoRunSendMode
{
    Foreground = 0,
    Background = 1
}
