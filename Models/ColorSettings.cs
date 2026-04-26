using System;
using System.Collections.Generic;

namespace sWinShortcuts.Models;

public sealed class ColorSettings
{
    private readonly Dictionary<string, DisplayColorProfile> _displayProfiles = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled { get; set; } = false;

    // Legacy property kept for backward compatibility during INI migration
    [Obsolete("Use per-display IsEnabled instead")]
    public string SelectedDisplayId { get; set; } = string.Empty;

    public IReadOnlyDictionary<string, DisplayColorProfile> DisplayProfiles => _displayProfiles;

    public DisplayColorProfile GetOrCreateProfile(string displayId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayId);

        if (_displayProfiles.TryGetValue(displayId, out var existing))
        {
            return existing;
        }

        var created = new DisplayColorProfile
        {
            DisplayId = displayId
        };
        _displayProfiles[displayId] = created;
        return created;
    }

    public void SetProfile(DisplayColorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.DisplayId);

        _displayProfiles[profile.DisplayId] = profile;
    }

    public void ClearProfiles()
    {
        _displayProfiles.Clear();
    }
}

public sealed class DisplayColorProfile
{
    public const int DefaultBrightness = 50;
    public const int DefaultContrast = 50;
    public const double DefaultGamma = 1.0;
    public const int DefaultDigitalVibrance = 50;

    public string DisplayId { get; set; } = string.Empty;

    /// <summary>
    /// Whether this individual display's color settings are enabled.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    public int Brightness { get; set; } = DefaultBrightness;

    public int Contrast { get; set; } = DefaultContrast;

    public double Gamma { get; set; } = DefaultGamma;

    public int DigitalVibrance { get; set; } = DefaultDigitalVibrance;

    public void ResetToDefaults()
    {
        Brightness = DefaultBrightness;
        Contrast = DefaultContrast;
        Gamma = DefaultGamma;
        DigitalVibrance = DefaultDigitalVibrance;
    }
}
