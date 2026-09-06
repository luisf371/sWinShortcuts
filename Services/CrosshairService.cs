using System;
using System.Windows.Threading;
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

    // Desired configuration and physical state are published together under _gate. Live edits
    // mutate the same Profile, so compare values rather than the Profile reference.
    private bool _shown;
    private bool _reportsRightButton;
    private bool _rightButtonHeld;
    private string _appliedImagePath = string.Empty;
    private int _appliedSizeAdjustment = CrosshairSettings.DefaultSizeAdjustment;
    private IntPtr _appliedHwnd;
    private bool _hasAppliedConfiguration;
    private bool _disposed;

    // Window operations are dispatcher-only and never run inside _gate.
    private CrosshairWindow? _window;

    internal bool AppliedVisibility { get; private set; }

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
    }

    public void ApplyProfile(Profile? profile, IntPtr foregroundHwnd)
    {
        var shouldShow = CrosshairDecision.ShouldShow(profile);
        var reportsRightButton = CrosshairDecision.ReportsRightButton(profile);
        var imagePath = profile?.Crosshair.ImagePath ?? string.Empty;
        var sizeAdjustment = profile?.Crosshair.SizeAdjustment ?? CrosshairSettings.DefaultSizeAdjustment;
        bool skipApply;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            skipApply = _hasAppliedConfiguration && (_dispatcher is null || _window is not null) &&
                shouldShow == _shown &&
                reportsRightButton == _reportsRightButton &&
                string.Equals(imagePath, _appliedImagePath, StringComparison.OrdinalIgnoreCase) &&
                sizeAdjustment == _appliedSizeAdjustment &&
                (!shouldShow || foregroundHwnd == _appliedHwnd);
            _shown = shouldShow;
            _reportsRightButton = reportsRightButton;
            _appliedImagePath = imagePath;
            _appliedSizeAdjustment = sizeAdjustment;
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
            RunOnDispatcher(ApplyOnDispatcher, synchronous: _window is null);
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
        RunOnDispatcher(ApplyVisibilityOnDispatcher, synchronous: false, priority: DispatcherPriority.Input);
    }

    private void ApplyOnDispatcher()
    {
        bool shouldShow;
        bool visible;
        IntPtr foregroundHwnd;
        string imagePath;
        int sizeAdjustment;
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
            _hasAppliedConfiguration = true;
            AppliedVisibility = visible;
        }

        if (System.Windows.Application.Current is null)
        {
            return;
        }

        if (shouldShow)
        {
            var window = _window ??= new CrosshairWindow();
            window.ApplyConfiguration(foregroundHwnd, imagePath, sizeAdjustment);
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

    private void RunOnDispatcher(Action action, bool synchronous, DispatcherPriority priority = DispatcherPriority.Render)
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
            if (synchronous)
            {
                if (dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    dispatcher.Invoke(action);
                }
            }
            else
            {
                dispatcher.BeginInvoke(priority, action);
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"[Crosshair] Dispatcher operation failed: {ex}");
        }
    }

    private void OnRightButtonStateChanged(object? sender, bool isDown) => SetRightButtonHeld(isDown);

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
            window = _window;
            _window = null;
            _shown = false;
            _reportsRightButton = false;
            _rightButtonHeld = false;
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

        if (window is not null)
        {
            RunOnDispatcher(window.Close, synchronous: false);
        }
    }
}
