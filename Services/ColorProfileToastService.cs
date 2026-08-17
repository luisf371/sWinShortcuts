using System;
using System.Threading;
using System.Windows.Threading;
using sWinShortcuts.Models;
using sWinShortcuts.Views;

namespace sWinShortcuts.Services;

// 2-second toast for the global color-preset toggle key: "Color Profile: Primary/Secondary". Every
// press restarts a fresh 2s window (deliberately NO dedup, unlike RapidFireStatusService).
//
// Dispatch contract (identical to RapidFireStatusService): Show is ENQUEUE-ONLY. It is called from
// ProfileActivationService's ColorVariantToggleRequested handler — i.e. the keyboard-hook thread —
// so building or touching a WPF window inline there risks LowLevelHooksTimeout (a stalled hook
// callback is silently removed by Windows). Show therefore schedules ApplyShow and returns
// immediately. This service deliberately does NOT subscribe to ColorVariantToggleRequested itself:
// that event fires PRE-flip and subscriber order is subscription order, so a direct subscriber
// would toast the inverted variant. ProfileActivationService raises Show AFTER ToggleVariant(),
// using the before/after compare as the "was a real flip" predicate.
public sealed class ColorProfileToastService : IDisposable
{
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(2);

    private readonly ILoggerService _logger;
    // Captured in the ctor, which DI builds on the UI thread. Null (headless tests) disables
    // every window operation.
    private readonly Dispatcher? _dispatcher;
    private readonly Action<Action> _enqueue;
    private readonly object _gate = new();

    // Dispose fence: once true (set under _gate), Show no-ops, and a queued-but-not-yet-run
    // ApplyShow re-checks it and bails — nothing touches a torn-down window/timer.
    private bool _disposed;

    private ColorProfileToastWindow? _window;

    // Dispatcher/test-thread-only: the 2s hide timer. A DispatcherObject — created and stopped
    // exclusively inside dispatcher-marshaled code (ApplyShow/Dispose), never from the hook thread.
    private DispatcherTimer? _timer;

    // Dispatcher/test-thread-only: last variant applied to the window (test observation). Written
    // and read only inside ApplyShow and on the thread that drains the enqueue seam — no volatile
    // needed (a nullable enum can't be volatile anyway).
    private ColorVariant? _appliedVariant;

    /// <summary>Last variant ApplyShow applied. Internal for test observation.</summary>
    internal ColorVariant? AppliedVariant => _appliedVariant;

    // Count of ApplyShow runs — internal for test observation (no dedup: every press counts).
    private int _shownCount;

    internal int ShownCount => Volatile.Read(ref _shownCount);

    public ColorProfileToastService(ILoggerService logger)
        : this(logger, enqueue: null)
    {
    }

    // enqueue: test seam. null -> production enqueue below. The production enqueue NEVER runs
    // inline (BeginInvoke queues even from the dispatcher thread) and is exception-isolated so
    // Show can never throw back through the hook thread; a dispatcher that is null or shutting
    // down is a no-op.
    internal ColorProfileToastService(ILoggerService logger, Action<Action>? enqueue)
    {
        _logger = logger;
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
        _enqueue = enqueue ?? ProductionEnqueue;
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
            _logger.Log($"[ColorProfileToast] Dispatcher enqueue failed: {ex}");
        }
    }

    // Hook-thread-safe: fence check, then enqueue-only.
    public void Show(ColorVariant variant)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        _enqueue(() => ApplyShow(variant));
    }

    // Runs on the dispatcher (or the test thread via the seam).
    private void ApplyShow(ColorVariant variant)
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
            _appliedVariant = variant;
            Volatile.Write(ref _shownCount, ShownCount + 1);

            // Headless (unit tests, no Application.Current): the window layer is skipped — the
            // bookkeeping above is what tests observe.
            if (System.Windows.Application.Current is null)
            {
                return;
            }

            (_window ??= new ColorProfileToastWindow()).ShowToast($"Color Profile: {variant}");

            // Fresh 2s window per press: Stop()+Start() on an already-running timer restarts it,
            // so a press while visible extends the toast instead of double-hiding it.
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = ToastDuration
                };
                _timer.Tick += OnTimerTick;
            }

            _timer.Stop();
            _timer.Start();
        }
        catch (Exception ex)
        {
            // An overlay must never take the app down (same acceptance as RapidFireStatusService).
            _logger.Log($"[ColorProfileToast] Apply failed: {ex}");
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        _window?.HideToast();
    }

    public void Dispose()
    {
        ColorProfileToastWindow? window;
        DispatcherTimer? timer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Fence FIRST: new Show calls no-op, queued ApplyShow no-ops, then detach the window.
            _disposed = true;
            window = _window;
            _window = null;
            timer = _timer;
            _timer = null;
        }

        if (window is null && timer is null)
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
                // Inline teardown is safe here: Dispose is never a hook callback. Both are
                // DispatcherObjects owned by this thread.
                timer?.Stop();
                window?.Close();
            }
            else
            {
                if (timer is not null)
                {
                    dispatcher.BeginInvoke(timer.Stop, DispatcherPriority.Send);
                }

                if (window is not null)
                {
                    dispatcher.BeginInvoke(window.Close, DispatcherPriority.Send);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"[ColorProfileToast] Window close failed: {ex}");
        }
    }
}
