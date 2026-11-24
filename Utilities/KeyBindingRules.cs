using System;
using System.Collections.Generic;
using System.Windows.Input;
using sWinShortcuts.Models;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.Utilities;

/// <summary>
/// The allowed set of each keybind field. Each rule returns null when the captured chord is accepted, or the
/// message the capture box shows when it is rejected. A None chord is only offered by fields that allow it.
/// </summary>
public static class KeyBindingRules
{
    public const string AltIsModifierMessage = "Alt is the Alt + Shortcut modifier";
    public const string AppToggleModifierMessage = "App toggle keys can't be modifiers";

    // Delegate instances for XAML ({x:Static}) capture-box validators.
    public static Func<KeyChord, string?> AssignableKeyValidator { get; } = AssignableKey;
    public static Func<KeyChord, string?> HoldBreathPanicValidator { get; } = HoldBreathPanic;
    public static Func<KeyChord, string?> AutoRunTriggerValidator { get; } = AutoRunTrigger;

    /// <summary>Any single assignable key (output keys such as Target, Tap, Hold, Sprint, Hold-breath, Caps remap).</summary>
    public static string? AssignableKey(KeyChord chord)
    {
        if (chord.IsEmpty)
        {
            return null;
        }

        return chord.Input.Kind == InputTriggerKind.KeyboardKey && KeyCatalog.IsAssignable(chord.Input.Key)
            ? null
            : CantUse(chord);
    }

    /// <summary>App toggle keys (Color, Rapid Fire, Crosshair offset): one non-modifier key, or None.</summary>
    public static string? AppToggle(KeyChord chord)
    {
        if (chord.IsEmpty)
        {
            return null;
        }

        if (chord.Input.Kind != InputTriggerKind.KeyboardKey || !KeyCatalog.IsAssignable(chord.Input.Key))
        {
            return CantUse(chord);
        }

        return KeyInteropUtilities.NormalizeAppToggleKey(chord.Input.Key) is null ? AppToggleModifierMessage : null;
    }

    /// <summary>
    /// Alt + Shortcut trigger: a mouse button, a wheel direction, or a key the gesture machine registers
    /// (<see cref="KeyCatalog.AltShortcutKeys"/>), not used by another row.
    /// </summary>
    public static string? AltShortcutSource(KeyChord chord, IEnumerable<InputTrigger> otherRowSources)
    {
        var input = chord.Input;
        switch (input.Kind)
        {
            case InputTriggerKind.KeyboardKey when input.Key is Key.LeftAlt or Key.RightAlt:
                return AltIsModifierMessage;
            case InputTriggerKind.KeyboardKey when !KeyCatalog.AltShortcutKeys.Contains(input.Key):
                return CantUse(chord);
            case InputTriggerKind.KeyboardKey:
            case InputTriggerKind.MouseButton:
            case InputTriggerKind.MouseWheel:
                return UniqueAmong(chord, otherRowSources);
            default:
                return CantUse(chord);
        }
    }

    /// <summary>Key Mappings source: an assignable key or a wheel direction, not used by another row.</summary>
    public static string? CombinedSource(KeyChord chord, IEnumerable<InputTrigger> otherRowSources)
    {
        var input = chord.Input;
        var allowed = input.Kind switch
        {
            InputTriggerKind.KeyboardKey => KeyCatalog.IsAssignable(input.Key),
            InputTriggerKind.MouseWheel => true,
            _ => false
        };
        return allowed ? UniqueAmong(chord, otherRowSources) : CantUse(chord);
    }

    /// <summary>Hold-breath Early Cancel: None, an assignable key, or the Middle/X1/X2 mouse buttons.</summary>
    public static string? HoldBreathPanic(KeyChord chord)
    {
        var input = chord.Input;
        return input.Kind switch
        {
            InputTriggerKind.None => null,
            InputTriggerKind.KeyboardKey when KeyCatalog.IsAssignable(input.Key) => null,
            InputTriggerKind.MouseButton when input.MouseButton is MouseButton.Middle or MouseButton.XButton1 or MouseButton.XButton2 => null,
            _ => CantUse(chord)
        };
    }

    /// <summary>Macro step and press keys: a key macro playback can send.</summary>
    public static string? MacroKey(KeyChord chord) =>
        chord.Input.Kind == InputTriggerKind.KeyboardKey && MacroValidation.IsSupportedKey(chord.Input.Key)
            ? null
            : CantUse(chord);

    /// <summary>Macro shortcut: None, or Ctrl/Alt/Shift/Win prefixes plus one key or one mouse button.</summary>
    public static string? MacroShortcut(KeyChord chord)
    {
        if (chord.IsEmpty)
        {
            return null;
        }

        if (!MacroValidation.IsValidModifiers(chord.Modifiers))
        {
            return CantUse(chord);
        }

        var input = chord.Input;
        return input.Kind switch
        {
            InputTriggerKind.KeyboardKey when MacroValidation.IsSupportedKey(input.Key) => null,
            InputTriggerKind.MouseButton when Enum.IsDefined(input.MouseButton) => null,
            _ => CantUse(chord)
        };
    }

    /// <summary>Auto-Run trigger: optional Ctrl/Alt/Shift/Win prefixes plus one assignable key.</summary>
    public static string? AutoRunTrigger(KeyChord chord)
    {
        if (chord.IsEmpty)
        {
            return "Auto-Run needs a trigger key";
        }

        return MacroValidation.IsValidModifiers(chord.Modifiers) &&
               chord.Input.Kind == InputTriggerKind.KeyboardKey && KeyCatalog.IsAssignable(chord.Input.Key)
            ? null
            : CantUse(chord);
    }

    private static string? UniqueAmong(KeyChord chord, IEnumerable<InputTrigger> otherRowSources)
    {
        foreach (var other in otherRowSources)
        {
            if (other == chord.Input)
            {
                return $"Another row already uses {chord.ToDisplayText()}";
            }
        }

        return null;
    }

    private static string CantUse(KeyChord chord) => $"{chord.ToDisplayText()} can't be used here";
}
