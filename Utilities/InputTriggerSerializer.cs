using System;
using sWinShortcuts.Models;
using System.Windows.Input;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.Utilities;

public static class InputTriggerSerializer
{
    public static string Serialize(InputTrigger trigger)
    {
        return trigger.Kind switch
        {
            InputTriggerKind.KeyboardKey when trigger.Key != Key.None
                => $"Key:{KeySerializer.Serialize(trigger.Key)}",
            InputTriggerKind.MouseButton when Enum.IsDefined(trigger.MouseButton)
                => $"Mouse:{trigger.MouseButton}",
            _ => "None"
        };
    }

    public static InputTrigger Deserialize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return InputTrigger.None;
        }

        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return InputTrigger.None;
        }

        var kind = value[..separator].Trim();
        var payload = value[(separator + 1)..].Trim();

        if (kind.Equals("Key", StringComparison.OrdinalIgnoreCase) &&
            KeySerializer.Deserialize(payload) is { } key)
        {
            return InputTrigger.FromKey(key);
        }

        if (kind.Equals("Mouse", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<MouseButton>(payload, true, out var button) &&
            Enum.IsDefined(button))
        {
            return InputTrigger.FromMouseButton(button);
        }

        return InputTrigger.None;
    }
}
