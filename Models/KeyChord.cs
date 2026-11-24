using System.Text;
using System.Windows.Input;
using sWinShortcuts.Converters;

namespace sWinShortcuts.Models;

/// <summary>
/// A captured keybind: modifier flags plus one primary key, mouse button, or wheel direction.
/// <see cref="RightModifiers"/> marks the modifiers held on the right-hand key; the others are the left key, so
/// a chord keeps the side that was pressed. Display text puts the modifiers first, in the order Ctrl, Alt,
/// Shift, Win, with their side (LCtrl+LShift+E).
/// </summary>
public readonly record struct KeyChord(ModifierKeys Modifiers, InputTrigger Input, ModifierKeys RightModifiers = ModifierKeys.None)
{
    // A side only means something for a modifier the chord holds.
    public ModifierKeys RightModifiers { get; init; } = RightModifiers & Modifiers;

    public static KeyChord None => default;

    public bool IsEmpty => Input.Kind == InputTriggerKind.None;

    public static KeyChord FromKey(Key key, ModifierKeys modifiers = ModifierKeys.None, ModifierKeys rightModifiers = ModifierKeys.None) =>
        new(modifiers, InputTrigger.FromKey(key), rightModifiers);

    public static KeyChord FromTrigger(InputTrigger trigger, ModifierKeys modifiers = ModifierKeys.None,
        ModifierKeys rightModifiers = ModifierKeys.None) =>
        new(modifiers, trigger, rightModifiers);

    public string ToDisplayText(string noneText = "None")
    {
        if (IsEmpty)
        {
            return noneText;
        }

        var text = new StringBuilder();
        AppendModifier(text, ModifierKeys.Control, Key.LeftCtrl, Key.RightCtrl);
        AppendModifier(text, ModifierKeys.Alt, Key.LeftAlt, Key.RightAlt);
        AppendModifier(text, ModifierKeys.Shift, Key.LeftShift, Key.RightShift);
        AppendModifier(text, ModifierKeys.Windows, Key.LWin, Key.RWin);
        text.Append(InputTriggerDisplayConverter.ToDisplayText(Input));
        return text.ToString();
    }

    // Prefixes use the same names as the keys themselves, so LCtrl reads the same alone and in LCtrl+S.
    private void AppendModifier(StringBuilder text, ModifierKeys flag, Key left, Key right)
    {
        if ((Modifiers & flag) != 0)
        {
            text.Append(KeyDisplayConverter.ToDisplayText((RightModifiers & flag) != 0 ? right : left)).Append('+');
        }
    }
}
