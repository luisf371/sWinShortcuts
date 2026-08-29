using System;
using System.Collections.Generic;
using Microsoft.Win32;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

internal readonly record struct AmdDisplayTarget(string DeviceName, int AdapterIndex, int DisplayIndex);

internal readonly record struct AmdSaturationRange(int Default, int Min, int Max, int Step);

internal interface IAmdAdlApi : IDisposable
{
    bool TryInitialize();
    bool TryRefresh();
    IReadOnlyList<AmdDisplayTarget> GetDisplays();
    bool TryGetSaturationRange(AmdDisplayTarget target, out AmdSaturationRange range);
    bool TrySetSaturation(AmdDisplayTarget target, int value);
    bool TryFlush(int adapterIndex);
}

/// <summary>Applies per-display saturation through AMD's ADL2 driver API.</summary>
public sealed class AmdColorControlService : IDisposable
{
    private readonly ILoggerService _logger;
    private readonly IAmdAdlApi _api;
    private readonly object _sync = new();
    private readonly Dictionary<string, AmdDisplayTarget> _targetCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<AmdDisplayTarget, AmdSaturationRange> _rangeCache = [];
    private bool _availabilityChecked;
    private bool _available;
    private bool _disposed;

    public AmdColorControlService(ILoggerService logger)
        : this(logger, new AmdAdlApi())
    {
    }

    internal AmdColorControlService(ILoggerService logger, IAmdAdlApi api)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    internal ColorApplyOutcome ApplyDigitalVibrance(DisplayInfo display, DisplayColorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(profile);

        lock (_sync)
        {
            if (_disposed || !EnsureAvailable())
            {
                _logger.Log("[Color][ADL] ADL2 is not available; skipping digital vibrance.");
                return ColorApplyOutcome.Skipped;
            }

            try
            {
                var target = FindTarget(display);
                if (target is null)
                {
                    return ColorApplyOutcome.Skipped;
                }

                if (!TryGetRange(target.Value, out var range))
                {
                    _logger.Log($"[Color][ADL] Saturation is unavailable for '{display.DeviceName}'; skipping.");
                    return ColorApplyOutcome.Skipped;
                }

                if (!TryMapPercentToAdlValue(profile.DigitalVibrance, range, out var value))
                {
                    _logger.Log($"[Color][ADL] Driver returned an invalid saturation range for '{display.DeviceName}'; skipping.");
                    return ColorApplyOutcome.Skipped;
                }

                _logger.Log($"[Color][ADL] Applying saturation {value} for requested {profile.DigitalVibrance}% to '{display.DeviceName}'.");

                if (!_api.TrySetSaturation(target.Value, value))
                {
                    Evict(target.Value);
                    _logger.Log($"[Color][ADL] ADL2_Display_Color_Set failed for '{display.DeviceName}'.");
                    return ColorApplyOutcome.Failed;
                }

                if (!_api.TryFlush(target.Value.AdapterIndex))
                {
                    _logger.Log($"[Color][ADL] ADL2_Flush_Driver_Data failed for adapter {target.Value.AdapterIndex}.");
                    return ColorApplyOutcome.Failed;
                }

                _logger.Log($"[Color][ADL] Saturation apply succeeded for '{display.DeviceName}'.");
                return ColorApplyOutcome.Applied;
            }
            catch (Exception ex)
            {
                _logger.Log($"[Color][ADL] Unexpected apply failure for '{display.DeviceName}': {ex}");
                return ColorApplyOutcome.Failed;
            }
        }
    }

    internal static bool TryMapPercentToAdlValue(
        int percent,
        AmdSaturationRange range,
        out int value)
    {
        value = range.Default;
        if (range.Max <= range.Min ||
            range.Default < range.Min ||
            range.Default > range.Max ||
            range.Step <= 0 ||
            range.Step > (long)range.Max - range.Min)
        {
            return false;
        }

        var clampedPercent = Math.Clamp(percent, DisplayColorProfile.DefaultDigitalVibrance, 100);
        if (clampedPercent == DisplayColorProfile.DefaultDigitalVibrance)
        {
            return true;
        }

        if (clampedPercent == 100)
        {
            value = range.Max;
            return true;
        }

        var normalized = (clampedPercent - DisplayColorProfile.DefaultDigitalVibrance) /
            (100.0 - DisplayColorProfile.DefaultDigitalVibrance);
        var raw = range.Default + (((double)range.Max - range.Default) * normalized);
        var steps = Math.Round((raw - range.Min) / range.Step, MidpointRounding.AwayFromZero);
        var stepped = range.Min + (steps * range.Step);
        value = (int)Math.Clamp(stepped, range.Default, range.Max);
        return true;
    }

    private bool EnsureAvailable()
    {
        if (_availabilityChecked)
        {
            return _available;
        }

        _availabilityChecked = true;
        try
        {
            _available = _api.TryInitialize();
            _logger.Log($"[Color][ADL] ADL2 initialization checked. Available={_available}.");
        }
        catch (Exception ex)
        {
            _available = false;
            _logger.Log($"[Color][ADL] ADL2 initialization failed: {ex}");
        }

        return _available;
    }

    private AmdDisplayTarget? FindTarget(DisplayInfo display)
    {
        var deviceName = display.DeviceName;
        if (_targetCache.TryGetValue(deviceName, out var cached))
        {
            return cached;
        }

        var displays = _api.GetDisplays();
        AmdDisplayTarget? exact = null;
        var exactCount = 0;

        foreach (var candidate in displays)
        {
            if (candidate.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
            {
                exact = candidate;
                exactCount++;
            }
        }

        if (exactCount == 1)
        {
            _targetCache[deviceName] = exact!.Value;
            return exact;
        }

        if (exactCount > 1)
        {
            _logger.Log($"[Color][ADL] Device '{deviceName}' matched {exactCount} ADL displays; skipping to avoid affecting the wrong monitor.");
            return null;
        }

        if (display.GpuVendor == GpuVendor.Amd && displays.Count == 1)
        {
            _logger.Log("[Color][ADL] Applying saturation to the single enumerated AMD display.");
            _targetCache[deviceName] = displays[0];
            return displays[0];
        }

        _logger.Log($"[Color][ADL] Could not map device '{deviceName}' among {displays.Count} AMD displays; skipping to avoid affecting the wrong monitor.");
        return null;
    }

    private bool TryGetRange(AmdDisplayTarget target, out AmdSaturationRange range)
    {
        if (_rangeCache.TryGetValue(target, out range))
        {
            return true;
        }

        if (!_api.TryGetSaturationRange(target, out range))
        {
            return false;
        }

        _rangeCache[target] = range;
        return true;
    }

    private void Evict(AmdDisplayTarget target)
    {
        _rangeCache.Remove(target);
        string? keyToRemove = null;
        foreach (var entry in _targetCache)
        {
            if (entry.Value == target)
            {
                keyToRemove = entry.Key;
                break;
            }
        }

        if (keyToRemove is not null)
        {
            _targetCache.Remove(keyToRemove);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        RefreshTopology();
    }

    internal void RefreshTopology()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_availabilityChecked && _available)
            {
                var refreshed = false;
                try
                {
                    refreshed = _api.TryRefresh();
                }
                catch (Exception ex)
                {
                    _logger.Log($"[Color][ADL] Adapter refresh failed: {ex}");
                }

                if (!refreshed)
                {
                    _availabilityChecked = false;
                    _available = false;
                    _logger.Log("[Color][ADL] Adapter refresh failed; ADL2 will be reinitialized on the next apply.");
                }
            }
            else if (_availabilityChecked)
            {
                _availabilityChecked = false;
            }

            _targetCache.Clear();
            _rangeCache.Clear();
        }
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _targetCache.Clear();
            _rangeCache.Clear();
            _api.Dispose();
        }
    }
}
