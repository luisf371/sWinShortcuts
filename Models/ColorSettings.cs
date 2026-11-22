using System;
using System.Collections.Generic;

namespace sWinShortcuts.Models;

/// <summary>
/// A profile's color preset. Primary is the default look. Secondary and Tertiary are optional
/// alternates that the global hotkey can cycle through.
/// </summary>
public enum ColorVariant
{
    Primary = 0,
    Secondary = 1,
    Tertiary = 2
}

public sealed class ColorSettings
{
    private const int VARIANT_COUNT = 3;

    private readonly Dictionary<string, DisplayColorProfile> _primaryProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DisplayColorProfile> _secondaryProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DisplayColorProfile> _tertiaryProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    // Runtime-only applied preset. Every launch starts on Primary. Guarded by _sync and advanced by
    // the hotkey through ToggleVariant.
    private ColorVariant _activeVariant = ColorVariant.Primary;

    public bool IsEnabled { get; set; } = false;
    public bool KeepColorActive { get; set; }

    private bool _hasSecondary;
    private bool _hasTertiary;

    /// <summary>Whether the user has opted in to the Secondary preset.</summary>
    public bool HasSecondary
    {
        get { lock (_sync) { return _hasSecondary; } }
        set
        {
            lock (_sync)
            {
                _hasSecondary = value;
                if (!value && _activeVariant == ColorVariant.Secondary)
                {
                    _activeVariant = ColorVariant.Primary;
                }
            }
        }
    }

    /// <summary>Whether the user has opted in to the Tertiary preset.</summary>
    public bool HasTertiary
    {
        get { lock (_sync) { return _hasTertiary; } }
        set
        {
            lock (_sync)
            {
                _hasTertiary = value;
                if (!value && _activeVariant == ColorVariant.Tertiary)
                {
                    _activeVariant = ColorVariant.Primary;
                }
            }
        }
    }

    /// <summary>
    /// Which preset is currently applied. Runtime state (not persisted).
    /// </summary>
    public ColorVariant ActiveVariant
    {
        get { lock (_sync) { return _activeVariant; } }
    }

    private Dictionary<string, DisplayColorProfile> StoreFor(ColorVariant variant)
        => variant switch
        {
            ColorVariant.Secondary => _secondaryProfiles,
            ColorVariant.Tertiary => _tertiaryProfiles,
            _ => _primaryProfiles
        };

    private bool IsAvailableLocked(ColorVariant variant)
        => variant switch
        {
            ColorVariant.Primary => true,
            ColorVariant.Secondary => _hasSecondary && _secondaryProfiles.Count > 0,
            ColorVariant.Tertiary => _hasTertiary && _tertiaryProfiles.Count > 0,
            _ => false
        };

    /// <summary>
    /// Returns a point-in-time snapshot of the per-display profiles for the ACTIVE variant. The returned
    /// dictionary is a fresh deep copy, so it is safe to enumerate from any thread while the UI thread
    /// mutates the underlying store. Falls back to Primary when the active preset is unavailable.
    /// </summary>
    public IReadOnlyDictionary<string, DisplayColorProfile> DisplayProfiles => SnapshotProfiles();

    public IReadOnlyDictionary<string, DisplayColorProfile> SnapshotProfiles()
    {
        lock (_sync)
        {
            var effective = IsAvailableLocked(_activeVariant) ? _activeVariant : ColorVariant.Primary;
            return SnapshotLocked(effective);
        }
    }

    /// <summary>Snapshot of a SPECIFIC variant — used by the INI serializer and the editor UI, which must
    /// address each preset explicitly regardless of which one is currently active.</summary>
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
            _tertiaryProfiles.Clear();
        }
    }

    /// <summary>
    /// Raised after <see cref="ToggleVariant"/> moves to a different preset, on the caller's thread (the
    /// keyboard-hook thread for the hotkey), so handlers must only queue work and never throw.
    /// </summary>
    public event EventHandler? VariantToggled;

    /// <summary>
    /// Advances to the next enabled, populated preset in cycle order. Primary is always available.
    /// The hotkey may supply the visible preview variant as its origin. Returns the new active variant.
    /// </summary>
    public ColorVariant ToggleVariant(ColorVariant? origin = null)
    {
        ColorVariant before;
        var after = ColorVariant.Primary;
        lock (_sync)
        {
            before = origin ?? _activeVariant;
            var start = IsAvailableLocked(before) ? before : ColorVariant.Primary;
            for (var step = 1; step <= VARIANT_COUNT; step++)
            {
                var next = (ColorVariant)(((int)start + step) % VARIANT_COUNT);
                if (IsAvailableLocked(next))
                {
                    after = next;
                    break;
                }
            }

            _activeVariant = after;
        }

        // Same before/after test as the toast, outside the lock.
        if (after != before)
        {
            VariantToggled?.Invoke(this, EventArgs.Empty);
        }

        return after;
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
    /// Seed missing rows in an optional preset from Primary before the editor creates blank display rows.
    /// Preserves all existing entries and user edits in the optional preset.
    /// </summary>
    public void EnsureVariantInitialized(ColorVariant variant)
    {
        if (variant == ColorVariant.Primary)
        {
            return;
        }

        lock (_sync)
        {
            var store = StoreFor(variant);
            foreach (var (id, p) in _primaryProfiles)
            {
                if (store.ContainsKey(id))
                {
                    continue;
                }

                store[id] = new DisplayColorProfile
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
