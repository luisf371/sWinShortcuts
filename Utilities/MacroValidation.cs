using System;
using System.Collections.Generic;
using System.Windows.Input;
using sWinShortcuts.Models;

namespace sWinShortcuts.Utilities;

public static class MacroValidation
{
    public const int MaxDefinitions = 64;
    public const int MaxSteps = 1_000;
    public const int MaxDurationMs = 3_600_000;

    public static string? GetFormatError(MacroSettings settings)
    {
        if (settings.Definitions is null || settings.Definitions.Length > MaxDefinitions)
        {
            return $"A profile can contain at most {MaxDefinitions} macros.";
        }

        var ids = new HashSet<Guid>();
        foreach (var macro in settings.Definitions)
        {
            var error = GetFormatError(macro);
            if (error is not null)
            {
                return error;
            }

            if (!ids.Add(macro.Id))
            {
                return "Macro IDs must be unique within a profile.";
            }
        }

        return null;
    }

    public static string? GetFormatError(MacroDefinition macro)
    {
        if (macro is null || macro.Id == Guid.Empty)
        {
            return "A macro needs a nonempty ID.";
        }

        if (string.IsNullOrWhiteSpace(macro.Label) || macro.Label.Trim().Length > 100)
        {
            return "Macro labels must contain 1 to 100 characters.";
        }

        foreach (var character in macro.Label)
        {
            if (char.IsControl(character))
            {
                return "Macro labels cannot contain control characters.";
            }
        }

        if (!IsSupportedKey(macro.ShortcutKey, allowNone: true))
        {
            return "The macro shortcut key is unsupported.";
        }

        if (macro.ShortcutMouseButton is { } button)
        {
            if (!Enum.IsDefined(button)) return "The macro shortcut mouse button is unsupported.";
            if (macro.ShortcutKey != Key.None) return "A macro shortcut must use either a keyboard key or a mouse button.";
        }

        if (!IsValidModifiers(macro.ShortcutModifiers))
        {
            return "Macro shortcut modifiers may only contain Ctrl, Alt, Shift, and Win.";
        }

        if (macro.Steps is null || macro.Steps.Length > MaxSteps)
        {
            return $"A macro can contain at most {MaxSteps} steps.";
        }

        for (var index = 0; index < macro.Steps.Length; index++)
        {
            var error = GetStepError(macro.Steps[index]);
            if (error is not null)
            {
                return $"Step {index + 1}: {error}";
            }
        }

        return null;
    }

    public static string? GetStepError(MacroStep step)
    {
        if (!Enum.IsDefined(step.Kind))
        {
            return "The action kind is unsupported.";
        }

        if (step.Kind is MacroStepKind.KeyPress or MacroStepKind.KeyDown or MacroStepKind.KeyUp && !IsSupportedKey(step.Key))
        {
            return "Choose a supported keyboard key.";
        }

        if (step.Kind is MacroStepKind.MouseClick or MacroStepKind.MouseDown or MacroStepKind.MouseUp &&
            (!step.MouseButton.HasValue || !Enum.IsDefined(step.MouseButton.Value)))
        {
            return "Choose a supported mouse button.";
        }

        if (step.Kind is MacroStepKind.KeyPress or MacroStepKind.MouseClick or MacroStepKind.Wait &&
            step.DurationMs is < 0 or > MaxDurationMs)
        {
            return $"Duration must be a whole number from 0 to {MaxDurationMs} milliseconds.";
        }

        if (step.Kind == MacroStepKind.MouseWheel && (step.WheelDelta is < short.MinValue or > short.MaxValue or 0))
        {
            return "Wheel delta must be a nonzero signed 16-bit value.";
        }

        // Coordinates are signed physical pixels, including negative positions on secondary displays.
        return null;
    }

    public static string? GetPlaybackError(MacroDefinition macro)
    {
        var formatError = GetFormatError(macro);
        if (formatError is not null)
        {
            return formatError;
        }

        if (macro.ShortcutTrigger.Kind == InputTriggerKind.None)
        {
            return "Assign a shortcut to play this macro.";
        }

        if (macro.ShortcutKey == Key.F12)
        {
            return "F12 is reserved for emergency cancellation while a macro is active.";
        }

        var baseModifier = macro.ShortcutKey switch
        {
            Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
            Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
            Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
            Key.LWin or Key.RWin => ModifierKeys.Windows,
            _ => ModifierKeys.None
        };
        if ((macro.ShortcutModifiers & baseModifier) != 0)
        {
            return "The shortcut key cannot also be one of its modifiers.";
        }

        if (macro.Steps.Length == 0)
        {
            return "Add at least one step to play this macro.";
        }

        var keysDown = new Dictionary<ushort, int>();
        var mouseDown = new Dictionary<Models.MouseButton, int>();
        for (var index = 0; index < macro.Steps.Length; index++)
        {
            var step = macro.Steps[index];
            switch (step.Kind)
            {
                case MacroStepKind.KeyDown:
                    keysDown.TryAdd(KeyInteropUtilities.ToVirtualKey(step.Key), index);
                    break;
                case MacroStepKind.KeyUp:
                    if (!keysDown.Remove(KeyInteropUtilities.ToVirtualKey(step.Key)))
                    {
                        return $"Step {index + 1}: a key UP must follow a matching DOWN.";
                    }
                    break;
                case MacroStepKind.KeyPress:
                    if (keysDown.ContainsKey(KeyInteropUtilities.ToVirtualKey(step.Key)))
                    {
                        return $"Step {index + 1}: a key press cannot target a key already held by this macro.";
                    }
                    break;
                case MacroStepKind.MouseDown:
                case MacroStepKind.MouseUp:
                case MacroStepKind.MouseClick:
                    var button = step.MouseButton.GetValueOrDefault();
                    if (step.Kind == MacroStepKind.MouseUp)
                    {
                        if (!mouseDown.Remove(button))
                        {
                            return $"Step {index + 1}: a mouse UP must follow a matching DOWN.";
                        }
                    }
                    else if (mouseDown.ContainsKey(button))
                    {
                        return $"Step {index + 1}: a mouse button already held by this macro cannot be pressed again.";
                    }
                    else if (step.Kind == MacroStepKind.MouseDown)
                    {
                        mouseDown.Add(button, index);
                    }
                    break;
            }
        }

        if (keysDown.Count == 0 && mouseDown.Count == 0)
        {
            return null;
        }

        var firstUnreleased = macro.Steps.Length;
        foreach (var index in keysDown.Values) firstUnreleased = Math.Min(firstUnreleased, index);
        foreach (var index in mouseDown.Values) firstUnreleased = Math.Min(firstUnreleased, index);
        return $"Step {firstUnreleased + 1}: add a matching UP before the macro ends.";
    }

    public static bool IsSupportedKey(Key key, bool allowNone = false) =>
        key == Key.None ? allowNone : Enum.IsDefined(key) && KeyInteropUtilities.ToVirtualKey(key) != 0;

    public static bool IsValidModifiers(ModifierKeys modifiers)
    {
        const ModifierKeys allowed = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows;
        return (modifiers & ~allowed) == ModifierKeys.None;
    }

    public static long GetTotalDurationMs(MacroDefinition macro)
    {
        long total = 0;
        foreach (var step in macro.Steps)
        {
            if (step.Kind is MacroStepKind.KeyPress or MacroStepKind.MouseClick or MacroStepKind.Wait)
            {
                total = checked(total + step.DurationMs);
            }
        }

        return total;
    }
}
