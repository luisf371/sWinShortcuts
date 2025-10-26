using System.Collections.Generic;
using System.Windows.Input;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.Models;

public sealed class AltMouseSettings
{
    public bool IsEnabled { get; set; }

    public Dictionary<MouseButton, MouseButtonBinding> Bindings { get; } = new();

    /// <summary>
    /// Tap-hold split threshold in milliseconds.
    /// </summary>
    public int HoldThresholdMilliseconds { get; set; } = 50;
}

public sealed class MouseButtonBinding
{
    public Key? TapKey { get; set; }

    public Key? HoldKey { get; set; }

    public bool SuppressOriginalWhileAltIsHeld => TapKey.HasValue || HoldKey.HasValue;
}
