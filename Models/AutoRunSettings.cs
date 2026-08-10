using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class AutoRunSettings
{
    public bool IsEnabled { get; set; } = false;

    // None selects a single-key trigger. Otherwise exactly one side-agnostic modifier is supported;
    // combined flags (e.g. Control|Alt) do not round-trip through SetEnum/GetEnum.
    public ModifierKeys TriggerModifier { get; set; } = ModifierKeys.Control;

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
