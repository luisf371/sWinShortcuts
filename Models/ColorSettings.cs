using System;
using System.Collections.Generic;

namespace sWinShortcuts.Models;

public sealed class ColorSettings
{
    private readonly Dictionary<string, DisplayColorProfile> _displayProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public bool IsEnabled { get; set; } = false;

    // Legacy property kept for backward compatibility during INI migration
    [Obsolete("Use per-display IsEnabled instead")]
    public string SelectedDisplayId { get; set; } = string.Empty;

    /// <summary>
    /// Returns a point-in-time snapshot of the per-display profiles. The returned dictionary is a
    /// fresh copy (value references shared), so it is safe to enumerate from any thread while the
    /// UI thread mutates the underlying store via <see cref="GetOrCreateProfile"/>/<see cref="SetProfile"/>.
    /// </summary>
    public IReadOnlyDictionary<string, DisplayColorProfile> DisplayProfiles => SnapshotProfiles();

    public IReadOnlyDictionary<string, DisplayColorProfile> SnapshotProfiles()
    {
        lock (_sync)
        {
            // Deep copy: the values are mutable and the UI writes them live (slider drags), so the
            // activation worker / INI serializer get value copies, not shared references — a shared
            // reference could later be read as a plan mixing pre- and post-edit fields.
            var snapshot = new Dictionary<string, DisplayColorProfile>(_displayProfiles.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (id, profile) in _displayProfiles)
            {
                snapshot[id] = new DisplayColorProfile
                {
                    DisplayId = profile.DisplayId,
                    IsEnabled = profile.IsEnabled,
                    Brightness = profile.Brightness,
                    Contrast = profile.Contrast,
                    Gamma = profile.Gamma,
                    DigitalVibrance = profile.DigitalVibrance
                };
            }

            return snapshot;
        }
    }

    public DisplayColorProfile GetOrCreateProfile(string displayId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayId);

        lock (_sync)
        {
            if (_displayProfiles.TryGetValue(displayId, out var existing))
            {
                return existing;
            }

            // A never-configured display must start DISABLED: the activation worker treats an enabled
            // per-display profile (even with neutral defaults) as "apply", which would write an identity
            // gamma ramp / DVC 0 and wipe calibration (C1). The user enables it explicitly to opt in.
            var created = new DisplayColorProfile
            {
                DisplayId = displayId,
                IsEnabled = false
            };
            _displayProfiles[displayId] = created;
            return created;
        }
    }

    public void SetProfile(DisplayColorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.DisplayId);

        lock (_sync)
        {
            _displayProfiles[profile.DisplayId] = profile;
        }
    }

    // F-018: mutate an existing per-display profile UNDER _sync, so a concurrent SnapshotProfiles() (the
    // activation worker / INI serializer, on another thread) always deep-copies a coherent whole-before or
    // whole-after value instead of a torn mix of pre/post-edit fields. The UI setters route writes here.
    public void UpdateProfile(string displayId, Action<DisplayColorProfile> mutate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayId);
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_sync)
        {
            if (_displayProfiles.TryGetValue(displayId, out var existing))
            {
                mutate(existing);
            }
        }
    }

    public void ClearProfiles()
    {
        lock (_sync)
        {
            _displayProfiles.Clear();
        }
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
