using System.Windows.Input;
using sWinShortcuts.Models;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests.Fakes;

// The step rows the editor's Key press and Mouse click insert, for tests that play presses.
internal static class MacroPresses
{
    public static MacroStep[] Press(Key key, int holdMs = 50) =>
    [
        new() { Kind = MacroStepKind.KeyDown, Key = key },
        new() { Kind = MacroStepKind.Wait, DurationMs = holdMs },
        new() { Kind = MacroStepKind.KeyUp, Key = key }
    ];

    public static MacroStep[] Click(MouseButton button, int x, int y, int holdMs = 50) =>
    [
        new() { Kind = MacroStepKind.MoveTo, X = x, Y = y },
        new() { Kind = MacroStepKind.MouseDown, MouseButton = button },
        new() { Kind = MacroStepKind.Wait, DurationMs = holdMs },
        new() { Kind = MacroStepKind.MouseUp, MouseButton = button }
    ];
}
