using System;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Microsoft.Win32;
using sWinShortcuts.Models;
using sWinShortcuts.Views;

namespace sWinShortcuts.Services;

public sealed class CrosshairService : ICrosshairService, IDisposable
{
    private readonly ILoggerService _logger;
    private readonly IInputHookService _inputHookService;
    // Captured on the UI thread. Headless tests inject only the queue, never a window.
    private readonly Dispatcher? _dispatcher;
    private readonly Action<Action>? _enqueue;
    private readonly object _gate = new();
    // Session choices follow profile lifetime without keeping removed profiles alive.
    private readonly ConditionalWeakTable<Profile, StrongBox<bool>> _offsetModes = new();

    // Desired configuration and physical state are published together under _gate. Live edits
    // mutate the same Profile, so compare values rather than the Profile reference.
    private bool _shown;
    private bool _reportsRightButton;
    private bool _rightButtonHeld;
    private Profile? _appliedProfile;
    private long _appliedForegroundGeneration;
    private StrongBox<bool>? _offsetMode;
    private int _appliedOffsetX;
    private int _appliedOffsetY;
    private string _appliedImagePath = string.Empty;
    private int _appliedSizeAdjustment = CrosshairSettings.DefaultSizeAdjustment;
    private IntPtr _appliedHwnd;
    private bool _hasAppliedConfiguration;
    private bool _stopped;
    private bool _disposed;

    // Window operations are dispatcher-only and never run inside _gate.
    private CrosshairWindow? _window;

    internal bool AppliedVisibility { get; private set; }
    internal (int X, int Y) AppliedOffset { get; private set; }

    public CrosshairService(ILoggerService logger, IInputHookService inputHookService)
        : this(logger, inputHookService, enqueue: null)
    {
    }

    internal CrosshairService(ILoggerService logger, IInputHookService inputHookService, Action<Action>? enqueue)
    {
        _logger = logger;
        _inputHookService = inputHookService;
        _enqueue = enqueue;
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
        _inputHookService.RightButtonStateChanged += OnRightButtonStateChanged;
        _inputHookService.CrosshairOffsetToggleRequested += OnCrosshairOffsetToggleRequested;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _stopped = false;
            _hasAppliedConfiguration = false;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Close admission and publish the hidden configuration atomically. Window work is queued.
            _stopped = true;
            _offsetModes.Clear();
            ApplyProfile(null, IntPtr.Zero);
        }
    }

    public void ApplyProfile(Profile? profile, IntPtr foregroundHwnd, long foregroundGeneration = 0)
    {
        var shouldShow = CrosshairDecision.ShouldShow(profile);
        var reportsRightButton = CrosshairDecision.ReportsRightButton(profile);
        var imagePath = profile?.Crosshair.ImagePath ?? string.Empty;
        var sizeAdjustment = profile?.Crosshair.SizeAdjustment ?? CrosshairSettings.DefaultSizeAdjustment;
        var offsetX = profile?.Crosshair.OffsetX ?? 0;
        var offsetY = profile?.Crosshair.OffsetY ?? 0;
        bool skipApply;
        lock (_gate)
        {
            if (_disposed || (_stopped && profile is not null))
            {
                return;
            }

            var profileChanged = !ReferenceEquals(profile, _appliedProfile);
            if (_offsetMode is not null && _appliedProfile is not null &&
                !CrosshairDecision.ShouldShow(_appliedProfile))
            {
                _offsetMode.Value = false;
            }
            // Allocate state on profile application, never in the input callback.
            _offsetMode = profile is null ? null : _offsetModes.GetOrCreateValue(profile);
            if (!shouldShow && _offsetMode is not null) _offsetMode.Value = false;

            skipApply = !profileChanged && _hasAppliedConfiguration && (_dispatcher is null || _window is not null) &&
                shouldShow == _shown &&
                reportsRightButton == _reportsRightButton &&
                string.Equals(imagePath, _appliedImagePath, StringComparison.OrdinalIgnoreCase) &&
                sizeAdjustment == _appliedSizeAdjustment &&
                offsetX == _appliedOffsetX && offsetY == _appliedOffsetY &&
                (!shouldShow || foregroundHwnd == _appliedHwnd);
            _shown = shouldShow;
            _reportsRightButton = reportsRightButton;
            _appliedImagePath = imagePath;
            _appliedSizeAdjustment = sizeAdjustment;
            _appliedProfile = profile;
            _appliedForegroundGeneration = foregroundGeneration;
            _appliedOffsetX = offsetX;
            _appliedOffsetY = offsetY;
            _appliedHwnd = foregroundHwnd;
            if (!reportsRightButton)
            {
                _rightButtonHeld = false;
            }

            // This setter takes no feature lock and may synchronously publish physical state.
            // The reentrant monitor orders that event with this policy, including on deduped applies.
            _inputHookService.SetRightButtonObservation(reportsRightButton);
        }

        if (!skipApply)
        {
            RunOnDispatcher(ApplyOnDispatcher);
        }
    }

    public void SetRightButtonHeld(bool isDown)
    {
        lock (_gate)
        {
            if (_disposed || !_shown || !_reportsRightButton)
            {
                return;
            }

            _rightButtonHeld = isDown;
        }

        // Always enqueue, even on the UI thread. Input callbacks never wait or reload assets.
        RunOnDispatcher(ApplyVisibilityOnDispatcher, priority: DispatcherPriority.Input);
    }

    private void ApplyOnDispatcher()
    {
        bool shouldShow;
        bool visible;
        IntPtr foregroundHwnd;
        string imagePath;
        int sizeAdjustment;
        (int X, int Y) offset;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            shouldShow = _shown;
            visible = _shown && (!_reportsRightButton || !_rightButtonHeld);
            foregroundHwnd = _appliedHwnd;
            imagePath = _appliedImagePath;
            sizeAdjustment = _appliedSizeAdjustment;
            offset = _offsetMode?.Value == true ? (_appliedOffsetX, _appliedOffsetY) : (0, 0);
            AppliedOffset = offset;
            _hasAppliedConfiguration = true;
            AppliedVisibility = visible;
        }

        if (System.Windows.Application.Current is null)
        {
            return;
        }

        try
        {
            if (shouldShow)
            {
                var window = _window ??= new CrosshairWindow();
                window.ApplyConfiguration(foregroundHwnd, imagePath, sizeAdjustment, offset.X, offset.Y);
                if (visible)
                {
                    window.ShowOverlay();
                }
                else
                {
                    window.HideOverlay();
                }
            }
            else
            {
                _window?.HideOverlay();
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"[Crosshair] Overlay configuration failed: {ex}");
        }
    }

    private void ApplyVisibilityOnDispatcher()
    {
        bool visible;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            visible = _shown && (!_reportsRightButton || !_rightButtonHeld);
            if (visible == AppliedVisibility)
            {
                return;
            }

            AppliedVisibility = visible;
        }

        if (visible)
        {
            _window?.ShowOverlay();
        }
        else
        {
            _window?.HideOverlay();
        }
    }

    private void RunOnDispatcher(Action action, DispatcherPriority priority = DispatcherPriority.Render)
    {
        if (_enqueue is not null)
        {
            _enqueue(action);
            return;
        }

        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            // The activation worker must never wait for a UI thread that may be stopping that worker.
            dispatcher.BeginInvoke(priority, action);
        }
        catch (Exception ex)
        {
            _logger.Log($"[Crosshair] Dispatcher operation failed: {ex}");
        }
    }

    private void OnRightButtonStateChanged(object? sender, bool isDown) => SetRightButtonHeld(isDown);

    private void OnCrosshairOffsetToggleRequested(object? sender, (Profile? Profile, long ForegroundGeneration) context)
    {
        lock (_gate)
        {
            if (_disposed || !_shown || _offsetMode is null || !ReferenceEquals(context.Profile, _appliedProfile) ||
                context.ForegroundGeneration != _appliedForegroundGeneration)
            {
                return;
            }

            _offsetMode.Value = !_offsetMode.Value;
        }

        // Keep input callbacks enqueue-only; the dispatcher reads the latest profile and mode.
        RunOnDispatcher(ApplyOnDispatcher, priority: DispatcherPriority.Input);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        RunOnDispatcher(ApplyOnDispatcher);
    }

    public void Dispose()
    {
        CrosshairWindow? window;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _inputHookService.RightButtonStateChanged -= OnRightButtonStateChanged;
            _inputHookService.CrosshairOffsetToggleRequested -= OnCrosshairOffsetToggleRequested;
            window = _window;
            _window = null;
            _shown = false;
            _reportsRightButton = false;
            _rightButtonHeld = false;
            _appliedProfile = null;
            _offsetMode = null;
            _offsetModes.Clear();
            AppliedOffset = (0, 0);
            AppliedVisibility = false;
            try
            {
                _inputHookService.SetRightButtonObservation(false);
            }
            catch (Exception ex)
            {
                _logger.Log($"[Crosshair] Failed to clear right-button observation: {ex}");
            }
        }

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        if (window is not null)
        {
            RunOnDispatcher(window.Close);
        }
    }
}
