using System.Text;
using System.Windows.Input;
using sWinShortcuts.Converters;

namespace sWinShortcuts.Models;

/// <summary>
/// A captured keybind: side-agnostic modifier flags plus one primary key, mouse button, or wheel direction.
/// Display text always puts the modifiers first, in the order Ctrl, Alt, Shift, Win.
/// </summary>
public readonly record struct KeyChord(ModifierKeys Modifiers, InputTrigger Input)
{
    public static KeyChord None => default;

    public bool IsEmpty => Input.Kind == InputTriggerKind.None;

    public static KeyChord FromKey(Key key, ModifierKeys modifiers = ModifierKeys.None) =>
        new(modifiers, InputTrigger.FromKey(key));

    public static KeyChord FromTrigger(InputTrigger trigger, ModifierKeys modifiers = ModifierKeys.None) =>
        new(modifiers, trigger);

    public string ToDisplayText(string noneText = "None")
    {
        if (IsEmpty)
        {
            return noneText;
        }

        var text = new StringBuilder();
        AppendModifiers(text, Modifiers);
        text.Append(InputTriggerDisplayConverter.ToDisplayText(Input));
        return text.ToString();
    }

    public static string FormatModifiers(ModifierKeys modifiers)
    {
        var text = new StringBuilder();
        AppendModifiers(text, modifiers);
        return text.Length == 0 ? "None" : text.ToString(0, text.Length - 1);
    }

    private static void AppendModifiers(StringBuilder text, ModifierKeys modifiers)
    {
        if ((modifiers & ModifierKeys.Control) != 0) text.Append("Ctrl+");
        if ((modifiers & ModifierKeys.Alt) != 0) text.Append("Alt+");
        if ((modifiers & ModifierKeys.Shift) != 0) text.Append("Shift+");
        if ((modifiers & ModifierKeys.Windows) != 0) text.Append("Win+");
    }
}
