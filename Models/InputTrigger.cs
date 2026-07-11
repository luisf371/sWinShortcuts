using System;
using System.Windows.Input;

namespace sWinShortcuts.Models;

public enum InputTriggerKind
{
    None,
    KeyboardKey,
    MouseButton
}

public readonly record struct InputTrigger(InputTriggerKind Kind, Key Key, MouseButton MouseButton)
{
    public static InputTrigger None => new(InputTriggerKind.None, Key.None, default);

    public static InputTrigger FromKey(Key key) =>
        key == Key.None
            ? None
            : new(InputTriggerKind.KeyboardKey, key, default);

    public static InputTrigger FromMouseButton(MouseButton button) =>
        Enum.IsDefined(button)
            ? new(InputTriggerKind.MouseButton, Key.None, button)
            : None;
}
