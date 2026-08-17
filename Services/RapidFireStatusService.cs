using System;
using System.Windows.Threading;
using sWinShortcuts.Models;
using sWinShortcuts.Views;

namespace sWinShortcuts.Services;

// Status dot for the sticky Rapid Fire arm: green = armed & ready (owner is the settled active
// profile), gray = armed but not ready, hidden = off. See RapidFireArmStatus.
//
// Dispatch contract: handlers are ENQUEUE-ONLY. RapidFireArmChanged fires from the foreground
// watcher thread inside its publication lock and from the keyboard-hook dispatcher — building or
// touching a WPF window inline there risks LowLevelHooksTimeout (a stalled hook callback is
// silently removed by Windows) and lock-order inversions, so every handler schedules ApplyLatest
// and returns immediately. Deliberately NOT CrosshairService.RunOnDispatcher: that helper inlines
// on CheckAccess, which would build the window inside a hook callback.
public sealed class RapidFireStatusService : IDisposable
{
    private readonly ILoggerService _logger;
    private readonly IInputHookService _inputHookService;
    // Captured in the ctor, which DI builds on the UI thread. Null (headless tests) disables
    // every window operation.
    private readonly Dispatcher? _dispatcher;
    private readonly Action<Action> _enqueue;
    private readonly object _gate = new();

    // Dispose fence: once true (set under _gate), handlers no-op, and a queued-but-not-yet-run
    // ApplyLatest re-checks it and bails — nothing touches a torn-down window.
    private bool _disposed;

    private RapidFireStatusWindow? _window;

    // Dispatcher/test-thread-only: last status applied to the window. Both the dedup and the
    // "reposition only on visual change" contract hang off this value.
    private volatile RapidFireArmStatus _appliedStatus = RapidFireArmStatus.Off;

    /// <summary>Last status ApplyLatest applied (dedup source). Internal for test observation.</summary>
    internal RapidFireArmStatus AppliedStatus => _appliedStatus;

    // Count of ApplyLatest runs that passed the dedup — internal for test observation.
    private int _appliedCount;

    internal int AppliedCount => Volatile.Read(ref _appliedCount);

    public RapidFireStatusService(ILoggerService logger, IInputHookService inputHookService)
        : this(logger, inputHookService, enqueue: null)
    {
    }

    // enqueue: test seam. null -> production enqueue below. The production enqueue NEVER runs
    // inline (BeginInvoke queues even from the dispatcher thread) and is exception-isolated so a
    // handler can never throw back through SetForegroundIdentity into the caller's publication
    // lock; a dispatcher that is null or shutting down is a no-op.
    internal RapidFireStatusService(ILoggerService logger, IInputHookService inputHookService, Action<Action>? enqueue)
    {
        _logger = logger;
        _inputHookService = inputHookService;
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
        _enqueue = enqueue ?? ProductionEnqueue;
        _inputHookService.RapidFireArmChanged += OnRapidFireArmChanged;
        _inputHookService.ActiveProfileChanged += OnActiveProfileChanged;
    }

    private void ProductionEnqueue(Action action)
    {
        try
        {
            var dispatcher = _dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            dispatcher.BeginInvoke(action, DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            _logger.Log($"[RapidFireStatus] Dispatcher enqueue failed: {ex}");
        }
    }

    private void OnRapidFireArmChanged(object? sender, EventArgs e) => EnqueueApply();

    private void OnActiveProfileChanged(object? sender, Profile? profile) => EnqueueApply();

    private void EnqueueApply()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        _enqueue(ApplyLatest);
    }

    // Runs on the dispatcher (or the test thread via the seam). Reads the status at EXECUTION
    // time: cross-thread queue order resolves last-op-wins, so the newest queued ApplyLatest is
    // authoritative regardless of which event scheduled it.
    private void ApplyLatest()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        try
        {
            var status = _inputHookService.GetRapidFireArmStatus();
            if (status == _appliedStatus)
            {
                // Dedup: spurious raises are contractual, and repositioning is pinned to visual
                // state changes only.
                return;
            }

            _appliedStatus = status;
            Volatile.Write(ref _appliedCount, AppliedCount + 1);

            // Headless (unit tests, no Application.Current): the window layer is skipped — the
            // applied-status bookkeeping above is what tests observe.
            if (System.Windows.Application.Current is null)
            {
                return;
            }

            switch (status)
            {
                case RapidFireArmStatus.Off:
                    _window?.HideOverlay();
                    break;
                case RapidFireArmStatus.ArmedNotReady:
                    (_window ??= new RapidFireStatusWindow()).ApplyState(ready: false);
                    break;
                case RapidFireArmStatus.Ready:
                    (_window ??= new RapidFireStatusWindow()).ApplyState(ready: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            // An overlay must never take the app down (same acceptance as CrosshairService).
            _logger.Log($"[RapidFireStatus] Apply failed: {ex}");
        }
    }

    public void Dispose()
    {
        RapidFireStatusWindow? window;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Fence FIRST: new events no-op, queued ApplyLatest no-ops, then detach the window.
            _disposed = true;
            _inputHookService.RapidFireArmChanged -= OnRapidFireArmChanged;
            _inputHookService.ActiveProfileChanged -= OnActiveProfileChanged;
            window = _window;
            _window = null;
        }

        if (window is null)
        {
            return;
        }

        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            // A queued Close can be aborted by WPF during shutdown — acceptable; WPF owns final
            // teardown of windows at exit.
            return;
        }

        try
        {
            if (dispatcher.CheckAccess())
            {
                // Inline close is safe here: Dispose is never a hook callback.
                window.Close();
            }
            else
            {
                dispatcher.BeginInvoke(window.Close, DispatcherPriority.Send);
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"[RapidFireStatus] Window close failed: {ex}");
        }
    }
}
