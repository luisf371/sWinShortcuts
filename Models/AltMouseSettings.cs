using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class AltMouseSettings
{
    public bool IsEnabled { get; set; }

    public MouseButtonBinding LeftButton { get; init; } = new();

    public MouseButtonBinding RightButton { get; init; } = new();

    public MouseButtonBinding MiddleButton { get; init; } = new();

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
