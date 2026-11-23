using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using sWinShortcuts.Models;

namespace sWinShortcuts.Converters;

/// <summary>
/// Two-way bridge between a single-key property (<see cref="Key"/>, <see cref="Nullable{Key}"/>, or
/// <see cref="InputTrigger"/>) and a capture box's <see cref="KeyChord"/>. The bound property's type picks the
/// conversion back; None maps to Key.None, null, or InputTrigger.None for that type. Modifiers never reach a
/// single-key property: the capture box rejects them before setting its chord.
/// </summary>
public sealed class KeyChordConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Key key => KeyChord.FromKey(key),
        InputTrigger trigger => KeyChord.FromTrigger(trigger),
        KeyChord chord => chord,
        _ => KeyChord.None
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var chord = value is KeyChord captured ? captured : KeyChord.None;
        if (targetType == typeof(InputTrigger))
        {
            return chord.Input;
        }

        if (targetType == typeof(KeyChord))
        {
            return chord;
        }

        var key = chord.Input.Kind == InputTriggerKind.KeyboardKey ? chord.Input.Key : Key.None;
        if (targetType == typeof(Key?))
        {
            return key == Key.None ? null : key;
        }

        if (targetType == typeof(Key))
        {
            return key;
        }

        return System.Windows.Data.Binding.DoNothing;
    }
}
