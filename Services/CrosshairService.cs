using System;
using System.Threading;
using System.Windows.Threading;
using sWinShortcuts.Models;
using sWinShortcuts.Views;

namespace sWinShortcuts.Services;

public sealed class CrosshairService : ICrosshairService, IDisposable
{
    private readonly ILoggerService _logger;
    private readonly IInputHookService _inputHookService;
    // Captured in the ctor, which DI builds on the UI thread. Null (headless unit tests) disables
    // every window operation; the decision/observation logic still runs.
    private readonly Dispatcher? _dispatcher;
    private readonly object _gate = new();

    // Applied configuration, mutated only under _gate. Value-based (NOT reference-based): live edits
    // mutate the same Profile instance, so reference equality would swallow an image re-pick.
    private bool _shown;
    private bool _reportsRightButton;
    private string _appliedImagePath = string.Empty;
    private IntPtr _appliedHwnd;

    // Window reference: created/closed only on the dispatcher. The hwnd is compared in the dedup so a
    // monitor-changing re-focus of the same profile re-centers the overlay.
    private CrosshairWindow? _window;

    // Dispatcher-thread-only: the last RMB state delivered to the window. Both the apply callback and
    // the RMB callback derive visibility from it, so their relative queue order cannot leave the
    // overlay visible while the button is held (or hidden after release).
    private bool _rightButtonHeld;

    public CrosshairService(ILoggerService logger, IInputHookService inputHookService)
    {
        _logger = logger;
        _inputHookService = inputHookService;
        _inputHookService.RightButtonStateChanged += OnRightButtonStateChanged;
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
    }

    public void ApplyProfile(Profile? profile, IntPtr foregroundHwnd)
    {
        var shouldShow = CrosshairDecision.ShouldShow(profile);
        var reportsRightButton = CrosshairDecision.ReportsRightButton(profile);
        var imagePath = profile?.Crosshair.ImagePath ?? string.Empty;

        var skipApply = false;
        lock (_gate)
        {
            // Dedup: foreground churn re-fires the same profile repeatedly. Skip the dispatcher
            // round-trip when nothing the overlay depends on changed. (hwnd only matters while
            // shown — a hidden overlay has no position to preserve.)
            if (_window is not null &&
                shouldShow == _shown &&
                reportsRightButton == _reportsRightButton &&
                string.Equals(imagePath, _appliedImagePath, StringComparison.OrdinalIgnoreCase) &&
                (!shouldShow || foregroundHwnd == _appliedHwnd))
            {
                skipApply = true;
            }
            else
            {
                _shown = shouldShow;
                _reportsRightButton = reportsRightButton;
                _appliedImagePath = imagePath;
                _appliedHwnd = foregroundHwnd;
            }
        }

        if (!skipApply)
        {
            RunOnDispatcher(() => ApplyOnDispatcher(shouldShow, foregroundHwnd, imagePath), synchronous: _window is null);
        }

        // Always (re-)sync the hook gate, even on a deduped apply: while false the hook pays nothing,
        // and arming re-publishes the current physical button state so a swallowed WM_RBUTTONUP can
        // never leave the overlay stuck hidden.
        _inputHookService.SetRightButtonObservation(reportsRightButton);
    }

    public void SetRightButtonHeld(bool isDown)
    {
        lock (_gate)
        {
            if (!_shown || !_reportsRightButton)
            {
                return;
            }
        }

        // Hook thread: BeginInvoke only — the low-level mouse hook must never wait on the UI queue.
        RunOnDispatcher(() =>
        {
            _rightButtonHeld = isDown;

            lock (_gate)
            {
                // Config may have changed while this was queued; an ungated or hidden overlay's
                // visibility is owned by the apply path, not by RMB state.
                if (!_shown || !_reportsRightButton)
                {
                    return;
                }
            }

            var window = _window;
            if (window is null)
            {
                return;
            }

            if (isDown)
            {
                window.HideOverlay();
            }
            else
            {
                window.ShowOverlay();
            }
        }, synchronous: false, priority: DispatcherPriority.Input);
    }

    private void ApplyOnDispatcher(bool shouldShow, IntPtr foregroundHwnd, string imagePath)
    {
        if (shouldShow)
        {
            var window = _window ??= new CrosshairWindow();
            window.ApplyConfiguration(foregroundHwnd, imagePath);
            if (_rightButtonHeld)
            {
                // An RMB-down delivered before this apply must keep winning.
                window.HideOverlay();
            }
            else
            {
                window.ShowOverlay();
            }
        }
        else
        {
            _rightButtonHeld = false;
            _window?.HideOverlay();
        }
    }

    private void RunOnDispatcher(Action action, bool synchronous, DispatcherPriority priority = DispatcherPriority.Render)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                action();
            }
            else if (synchronous)
            {
                // First window creation runs synchronously so a construction failure surfaces
                // immediately in the caller's context (activation worker) instead of on a later
                // dispatcher frame; every later mutation is queued (FIFO preserves apply order).
                dispatcher.Invoke(action);
            }
            else
            {
                dispatcher.BeginInvoke(priority, action);
            }
        }
        catch (Exception ex)
        {
            // Dispatcher shutdown racing a queued op throws; an overlay must never take the app down.
            _logger.Log($"[Crosshair] Dispatcher operation failed: {ex}");
        }
    }

    private void OnRightButtonStateChanged(object? sender, bool isDown) => SetRightButtonHeld(isDown);

    public void Dispose()
    {
        _inputHookService.RightButtonStateChanged -= OnRightButtonStateChanged;

        CrosshairWindow? window;
        lock (_gate)
        {
            window = _window;
            _window = null;
            _shown = false;
            _reportsRightButton = false;
        }

        try
        {
            _inputHookService.SetRightButtonObservation(false);
        }
        catch (Exception ex)
        {
            _logger.Log($"[Crosshair] Failed to clear right-button observation: {ex}");
        }

        if (window is not null)
        {
            RunOnDispatcher(window.Close, synchronous: false);
        }
    }
}
