using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace sWinShortcuts.Models;

/// <summary>
/// Which of a profile's two color presets is being referenced. Primary is the "as default" look;
/// Secondary is an optional alternate (e.g. more vibrant / brighter) toggled live by a global hotkey.
/// </summary>
public enum ColorVariant
{
    Primary = 0,
    Secondary = 1
}

public sealed class ColorSettings
{
    private readonly Dictionary<string, DisplayColorProfile> _primaryProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DisplayColorProfile> _secondaryProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    // Runtime-only: which preset is currently APPLIED. Deliberately NOT persisted — every launch starts on
    // Primary so the "as default" look is what comes up. Guarded by _sync (flipped from the activation
    // worker via ToggleVariant, read by the INI serializer / apply path via SnapshotProfiles).
    private ColorVariant _activeVariant = ColorVariant.Primary;

    public bool IsEnabled { get; set; } = false;

    private bool _hasSecondary;
    private Key? _toggleKey;

    /// <summary>
    /// Whether a Secondary preset has been configured for this profile. When false, only Primary is ever
    /// applied and the global toggle key is a no-op for this profile (the user opts in explicitly).
    /// Guarded by <c>_sync</c> — written by the UI thread, read by the activation worker / hook event.
    /// </summary>
    public bool HasSecondary
    {
        get { lock (_sync) { return _hasSecondary; } }
        set { lock (_sync) { _hasSecondary = value; } }
    }

    /// <summary>
    /// The GLOBAL color-variant toggle key. Only the global Color profile's value is authoritative at
    /// runtime (<see cref="Services.ProfileActivationService"/> pushes it to the hook); it lives on
    /// <see cref="ColorSettings"/> so it rides the existing color persistence. Guarded by <c>_sync</c> — a
    /// bare Key? is a non-atomic struct the worker must not read torn while the UI writes it.
    /// </summary>
    public Key? ToggleKey
    {
        get { lock (_sync) { return _toggleKey; } }
        set { lock (_sync) { _toggleKey = value; } }
    }

    // Legacy property kept for backward compatibility during INI migration
    [Obsolete("Use per-display IsEnabled instead")]
    public string SelectedDisplayId { get; set; } = string.Empty;

    /// <summary>
    /// Which preset is currently applied. Runtime state (not persisted).
    /// </summary>
    public ColorVariant ActiveVariant
    {
        get { lock (_sync) { return _activeVariant; } }
    }

    private Dictionary<string, DisplayColorProfile> StoreFor(ColorVariant variant)
        => variant == ColorVariant.Secondary ? _secondaryProfiles : _primaryProfiles;

    /// <summary>
    /// Returns a point-in-time snapshot of the per-display profiles for the ACTIVE variant. The returned
    /// dictionary is a fresh deep copy, so it is safe to enumerate from any thread while the UI thread
    /// mutates the underlying store. Falls back to Primary if Secondary is active but not configured.
    /// </summary>
    public IReadOnlyDictionary<string, DisplayColorProfile> DisplayProfiles => SnapshotProfiles();

    public IReadOnlyDictionary<string, DisplayColorProfile> SnapshotProfiles()
    {
        lock (_sync)
        {
            // Secondary only when opted-in AND actually populated — a HasSecondary-but-empty store must NEVER
            // apply (it would push neutral/disabled plans and wipe the calibrated Primary look).
            var effective = (_activeVariant == ColorVariant.Secondary && _hasSecondary && _secondaryProfiles.Count > 0)
                ? ColorVariant.Secondary
                : ColorVariant.Primary;
            return SnapshotLocked(effective);
        }
    }

    /// <summary>Snapshot of a SPECIFIC variant — used by the INI serializer and the editor UI, which must
    /// address Primary/Secondary explicitly regardless of which one is currently active.</summary>
    public IReadOnlyDictionary<string, DisplayColorProfile> SnapshotProfiles(ColorVariant variant)
    {
        lock (_sync)
        {
            return SnapshotLocked(variant);
        }
    }

    private IReadOnlyDictionary<string, DisplayColorProfile> SnapshotLocked(ColorVariant variant)
    {
        var store = StoreFor(variant);

        // Deep copy: the values are mutable and the UI writes them live (slider drags), so the activation
        // worker / INI serializer get value copies, not shared references — a shared reference could later
        // be read as a plan mixing pre- and post-edit fields.
        var snapshot = new Dictionary<string, DisplayColorProfile>(store.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, profile) in store)
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

    public DisplayColorProfile GetOrCreateProfile(string displayId) => GetOrCreateProfile(displayId, ColorVariant.Primary);

    public DisplayColorProfile GetOrCreateProfile(string displayId, ColorVariant variant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayId);

        lock (_sync)
        {
            var store = StoreFor(variant);
            if (store.TryGetValue(displayId, out var existing))
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
            store[displayId] = created;
            return created;
        }
    }

    public void SetProfile(DisplayColorProfile profile) => SetProfile(profile, ColorVariant.Primary);

    public void SetProfile(DisplayColorProfile profile, ColorVariant variant)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.DisplayId);

        lock (_sync)
        {
            StoreFor(variant)[profile.DisplayId] = profile;
        }
    }

    // F-018: mutate an existing per-display profile UNDER _sync, so a concurrent SnapshotProfiles() (the
    // activation worker / INI serializer, on another thread) always deep-copies a coherent whole-before or
    // whole-after value instead of a torn mix of pre/post-edit fields. The UI setters route writes here.
    public void UpdateProfile(string displayId, Action<DisplayColorProfile> mutate)
        => UpdateProfile(displayId, ColorVariant.Primary, mutate);

    public void UpdateProfile(string displayId, ColorVariant variant, Action<DisplayColorProfile> mutate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayId);
        ArgumentNullException.ThrowIfNull(mutate);

        lock (_sync)
        {
            if (StoreFor(variant).TryGetValue(displayId, out var existing))
            {
                mutate(existing);
            }
        }
    }

    public void ClearProfiles()
    {
        lock (_sync)
        {
            _primaryProfiles.Clear();
            _secondaryProfiles.Clear();
        }
    }

    /// <summary>
    /// Flips the active variant Primary&lt;-&gt;Secondary (only meaningful when <see cref="HasSecondary"/>).
    /// Called from the activation worker when the global toggle key fires. Returns the new active variant.
    /// </summary>
    public ColorVariant ToggleVariant()
    {
        lock (_sync)
        {
            // Never switch to an un-opted-in or empty Secondary (would apply a blank/neutral plan).
            if (!_hasSecondary || _secondaryProfiles.Count == 0)
            {
                _activeVariant = ColorVariant.Primary;
                return _activeVariant;
            }

            _activeVariant = _activeVariant == ColorVariant.Primary ? ColorVariant.Secondary : ColorVariant.Primary;
            return _activeVariant;
        }
    }

    /// <summary>Force the active variant (e.g. the editor previewing a specific variant, or reset to Primary
    /// on load / deactivate).</summary>
    public void SetActiveVariant(ColorVariant variant)
    {
        lock (_sync)
        {
            _activeVariant = variant;
        }
    }

    /// <summary>
    /// Ensure every Primary display has a Secondary counterpart, seeding any MISSING one from Primary (a
    /// per-display fill that PRESERVES existing Secondary entries / user edits). A freshly-enabled or only
    /// partially-configured Secondary thus never leaves an enabled display blank/disabled — which the editor
    /// would materialize and a toggle would then apply as a neutral plan, wiping the calibrated Primary there.
    /// </summary>
    public void EnsureSecondaryInitialized()
    {
        lock (_sync)
        {
            foreach (var (id, p) in _primaryProfiles)
            {
                if (_secondaryProfiles.ContainsKey(id))
                {
                    continue;
                }

                _secondaryProfiles[id] = new DisplayColorProfile
                {
                    DisplayId = p.DisplayId,
                    IsEnabled = p.IsEnabled,
                    Brightness = p.Brightness,
                    Contrast = p.Contrast,
                    Gamma = p.Gamma,
                    DigitalVibrance = p.DigitalVibrance
                };
            }
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
