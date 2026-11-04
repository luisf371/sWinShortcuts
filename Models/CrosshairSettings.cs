namespace sWinShortcuts.Models;

public sealed class CrosshairSettings
{
    // Ported from AHK "WinSet TransColor, 0 180" => 180/255 opacity, kept fixed (no slider by design).
    public const double DefaultOpacity = 0.70;

    // Artificial re-sizer: uniform percentage scale of the rendered overlay, in percent points
    // (-50 => 50% of source size, 0 => 100%, +50 => 150%). Centered/symmetrical by construction.
    public const int MinSizeAdjustment = -50;
    public const int MaxSizeAdjustment = 50;
    public const int DefaultSizeAdjustment = 0;

    // Position relative to the monitor center in physical pixels: right/down are positive.
    public const int MinOffset = -500;
    public const int MaxOffset = 500;

    // Right-button hide delay: 0 = hide at once (off); slider steps by HideDelayStepMilliseconds.
    public const int MaxHideDelayMilliseconds = 1000;
    public const int HideDelayStepMilliseconds = 50;

    public bool IsEnabled { get; set; }

    public bool HideWhileRightButtonHeld { get; set; }

    public int HideDelayMilliseconds { get; set; }

    public int SizeAdjustment { get; set; } = DefaultSizeAdjustment;

    public int OffsetX { get; set; }

    public int OffsetY { get; set; }

    // Empty => render the bundled default PNG (pack://application:,,,/Icons/Crosshair.png).
    public string ImagePath { get; set; } = string.Empty;
}
