namespace sWinShortcuts.Models;

public sealed class CrosshairSettings
{
    // Ported from AHK "WinSet TransColor, 0 180" => 180/255 opacity, kept fixed (no slider by design).
    public const double DefaultOpacity = 0.70;

    public bool IsEnabled { get; set; }

    public bool HideWhileRightButtonHeld { get; set; }

    // Empty => render the bundled default PNG (pack://application:,,,/Icons/Crosshair.png).
    public string ImagePath { get; set; } = string.Empty;
}
