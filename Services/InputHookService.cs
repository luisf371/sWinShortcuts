using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Input;
using Microsoft.Win32;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services.Input;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.Services;

/// <summary>
/// High-performance input hook service optimized for gaming applications.
/// Uses lock-free algorithms and pre-allocated timers for sub-millisecond latency.
/// </summary>
public sealed class InputHookService : IInputHookService
{
    private readonly ILoggerService _logger;
    private readonly InputRuntimeState _runtime;
    private readonly InputExecutor _inputExecutor;
    private readonly GestureChordStateMachine _gestures;
    private readonly RapidFireStateMachine _rapidFire;
    private readonly AutoRunStateMachine _autoRun;
    private readonly AntiAfkStateMachine _antiAfk;
    private readonly RemapStateMachine _remaps;
    private static readonly Func<int, bool> IsPhysicalKeyDown =
        key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;

    private readonly object _profileLock = new();
    private readonly ThreadLocal<Random> _random = new(() =>
    {
        var timestamp = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        return new Random(unchecked((int)(timestamp ^ (threadId << 16))));
    });
    private volatile int _colorToggleVk;
    private bool _colorToggleDownLatched;
    private int _hookSeenToggleVk;
    private volatile bool _rightButtonPressed;
    private volatile bool _crosshairRightButtonWatch;
    private volatile Profile? _windowsProfile;


    // Hook handles
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private IntPtr _keyboardHookHandle = IntPtr.Zero;
    private IntPtr _mouseHookHandle = IntPtr.Zero;

    // P7: true iff timeBeginPeriod(1) succeeded in Start() and has not yet been paired with
    // timeEndPeriod(1). winmm requires matched calls; guarded by _profileLock (Start/Stop only).
    private bool _timerResolutionRaised;

    // P8: liveness ticks, Volatile.Write as the FIRST statement of each hook callback — any
    // invocation (including a filtered-out mouse move) proves that hook is still alive. Initialized
    // right after install so a freshly-started idle app can never look stale. Read by the watchdog
    // from a different thread, hence Volatile rather than plain fields.
    private long _lastKeyboardEventTick;
    private long _lastMouseEventTick;

    // P8 fail-open swap-window flags: while a hook re-install is in flight, its callback passes
    // every event straight to CallNextHookEx with ZERO side effects instead of relying on overlap
    // idempotency — MouseCallback's button-state tracking and hold-breath arm/release are NOT
    // idempotent across a double-processed event (a duplicate WM_RBUTTONDOWN would re-arm hold-breath
    // with a fresh jitter sample). Set/cleared inside the dispatcher-marshaled re-install continuation
    // (see WatchdogTick); read on the hook thread — same thread in practice, kept volatile regardless.
    private volatile bool _keyboardReplacementInProgress;
    private volatile bool _mouseReplacementInProgress;

    // P8: captured in Start() so the watchdog can marshal a re-install back onto the thread that
    // owns the hooks — SetWindowsHookEx callbacks are only pumped on the thread that installed them,
    // never a pool thread. _canReinstallHooks false means Start() ran off a message-pumping thread
    // (already-broken setup); the watchdog still detects and logs, but re-install stays disabled.
    private System.Windows.Threading.Dispatcher? _hookDispatcher;
    private bool _canReinstallHooks;

    private System.Threading.Timer? _hookWatchdogTimer;

    // P8: only ONE re-install freshness check may be queued on _hookDispatcher at a time. A stalled
    // dispatcher must not accumulate stale reinstall work — WatchdogTick claims this with
    // CompareExchange(1, 0) before queuing; the queued closure clears it in a finally (see
    // WatchdogTick) so a resumed dispatcher re-evaluates freshness once, not once per missed period.
    private int _reinstallCheckPending;

    // P8 rework: raw-input liveness side channel (see RawInputLivenessSink). Created in Start() on
    // the dispatcher; null means creation failed and the watchdog degrades to detection-only logging
    // (a hook idle-vs-dead question can no longer be answered, so it must never guess-reinstall).
    private RawInputLivenessSink? _rawInputSink;

    // P8 rework: per-device sink-open state. Written ONLY inside WatchdogTick's single-flight
    // section (_watchdogTickRunning CAS serializes overlapping timer callbacks); volatile because
    // the dispatcher-marshaled reinstall closure reads them for its freshness recheck.
    private volatile bool _keyboardSinkOpen;
    private volatile bool _mouseSinkOpen;

    // P8 rework: reentrancy guard making WatchdogTick a single writer of the sink-open flags —
    // System.Threading.Timer offers no overlap guarantee, and Interlocked entry/exit fences also
    // publish the flag writes to the next tick.
    private int _watchdogTickRunning;

    // Near-crash report latch for CrashReporter: Environment.TickCount of the last hook-loss report.
    // 0 = no episode in progress (sentinel; a tick of exactly 0 is a harmless one-boot-cycle false
    // "no episode" costing at most one extra report). Plain non-volatile: read/written only inside
    // WatchdogTick's single-flight CAS section — single writer, fenced by the Interlocked entry/exit
    // (same discipline as the _keyboardSinkOpen/_mouseSinkOpen flags above).
    private int _watchdogHookLossReportedAtTick;

    // Troubleshooting switch (Settings window, [App] HookWatchdog). The timer keeps ticking so the
    // toggle is live in both directions; a disabled tick only cleans up any open sinks and returns.
    private volatile bool _hookWatchdogEnabled = true;

    public bool HookWatchdogEnabled
    {
        get => _hookWatchdogEnabled;
        set
        {
            if (_hookWatchdogEnabled != value)
            {
                _hookWatchdogEnabled = value;
                LogDebug($"Hook-loss watchdog {(value ? "enabled" : "disabled")} via settings");
            }
        }
    }

    // Advanced Mode: global [App] gate for non-1:1 automation (Auto-Run, Anti-AFK, Hold-Breath, Rapid Fire, and
    // un-suppressed key mappings). Mirrors HookWatchdogEnabled end-to-end; live-togglable from Settings.
    // volatile for the lock-free gating reads on the hook thread (and the injector thread).
    public bool AdvancedModeEnabled
    {
        get => _runtime.AdvancedModeEnabled;
        set
        {
            if (_runtime.AdvancedModeEnabled == value)
            {
                return;
            }

            _runtime.SetAdvancedMode(value);
            LogDebug($"Advanced Mode {(value ? "enabled" : "disabled")} via settings");

            // true→false: release every gated held state so nothing keeps injecting under a now-off
            // gate. This setter runs on the UI dispatcher — which IS the hook thread — so each release
            // MUST be enqueue-only / non-blocking (a synchronous SendInput here could stall the
            // dispatcher on a foreign LL hook, the very freeze the injector exists to prevent). Each
            // release takes only its own leaf lock; they are never nested (I5). Anti-AFK needs no
            // action — its tick self-gates on _runtime.AdvancedModeEnabled. (Auto-Run release is wired in P3a.)
            if (!value)
            {
                if (_rapidFire.Release(preservePhysicalPairing: true))
                {
                    // Gate closed: the arm is gone. Raised only when an arm was actually live;
                    // handlers are enqueue-only so this stays safe on the dispatcher thread.
                    RaiseRapidFireArmChanged();
                }
                _autoRun.Release(includeBackground: true); // gate closed — release Background too
                _gestures.ReleaseHoldBreath();
                _remaps.ReleaseUnsuppressed();
            }
        }
    }

    // P8 watchdog thresholds/period.
    private const int WATCHDOG_PERIOD_MS = 10_000;
    private const double WATCHDOG_STALE_HOOK_THRESHOLD_MS = 30_000;
    private const uint WATCHDOG_FRESH_INPUT_THRESHOLD_MS = 2_000;

    // Near-crash report throttle: confirmed hook loss is edge-triggered into crash.log, then
    // re-reported at most this often while the loss persists. This guard is the PRIMARY bound —
    // the 512 KiB crash.log cap is only a backstop, because an unthrottled 10s-tick loop
    // (~8,640 reports/day) would let the trim evict the onset entry within hours.
    private const int WATCHDOG_CRASH_REREPORT_MS = 60_000;

    // Performance metrics
    private static readonly double TickToMilliseconds = 1000.0 / Stopwatch.Frequency;

    public InputHookService(ILoggerService logger, IInputSender inputSender)
    {
        _logger = logger;
        _runtime = new InputRuntimeState();
        _inputExecutor = new InputExecutor(_runtime, inputSender, logger);
        var transport = new NativeAutoRunTransport();
        _autoRun = new AutoRunStateMachine(_runtime, _inputExecutor, _random, logger, transport);
        _antiAfk = new AntiAfkStateMachine(_runtime, _autoRun, _random, logger, transport);
        _gestures = new GestureChordStateMachine(
            _runtime,
            _inputExecutor,
            _random,
            logger,
            () => _rightButtonPressed);
        _rapidFire = new RapidFireStateMachine(_runtime, inputSender, _random, logger, _profileLock);
        _remaps = new RemapStateMachine(_runtime, _inputExecutor, _random, logger, IsPhysicalKeyDown);
    }

    public event EventHandler<Profile?>? ActiveProfileChanged;

    public event EventHandler? ColorVariantToggleRequested;

    // Crosshair overlay RMB feed — see IInputHookService.RightButtonStateChanged. Observation-only:
    // fired from the mouse hook at human click frequency while armed, never causes suppression.
    public event EventHandler<bool>? RightButtonStateChanged;

    // Sticky-arm feed — see IInputHookService.RapidFireArmChanged. Raised ONLY on real transitions,
    // always OUTSIDE _profileLock (the single deliberate exception, documented at the raise site in
    // SetForegroundIdentity). Handlers are contractually enqueue-only and exception-isolated.
    public event EventHandler? RapidFireArmChanged;

    // ==================== LIFECYCLE ====================
    
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_runtime.IsDisposed, this);

        lock (_profileLock)
        {
            ObjectDisposedException.ThrowIf(_runtime.IsDisposed, this);

            if (_runtime.IsRunning)
            {
                return;
            }

            if (_inputExecutor.IsWorkerAlive)
            {
                throw new InvalidOperationException(
                    "The previous input executor is still draining releases; retry Start after it exits.");
            }
            _inputExecutor.DisposeCompletedQueue();

            // P8: hooks are (re-)installed only from a message-pumping thread — SetWindowsHookEx
            // delivers WH_*_LL callbacks via the installing thread's message loop, never a pool
            // thread. Capture it once here so the watchdog can marshal a later re-install back onto
            // this exact thread; if the check fails, Start() itself is already on a broken thread
            // (detection can still log, but re-install would be unsafe, so it stays disabled).
            _hookDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _canReinstallHooks = SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext;
            if (!_canReinstallHooks)
            {
                LogDebug("ERROR: InputHookService.Start() is not running on a dispatcher-pumped thread; hook-loss watchdog re-install is disabled (detection still logs)");
            }

            _keyboardProc = KeyboardCallback;
            _mouseProc = MouseCallback;

            var user32Handle = NativeMethods.LoadLibrary("user32.dll");
            if (user32Handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to load user32.dll");
            }

            _keyboardHookHandle = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL, 
                _keyboardProc, 
                user32Handle, 
                0);
                
            _mouseHookHandle = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL, 
                _mouseProc, 
                user32Handle, 
                0);

            if (_keyboardHookHandle == IntPtr.Zero || _mouseHookHandle == IntPtr.Zero)
            {
                // Capture the error BEFORE unhooking (UnhookWindowsHookEx clobbers GetLastWin32Error).
                var err = Marshal.GetLastWin32Error();

                if (_keyboardHookHandle != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
                    _keyboardHookHandle = IntPtr.Zero;
                }

                if (_mouseHookHandle != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
                    _mouseHookHandle = IntPtr.Zero;
                }

                _keyboardProc = null;
                _mouseProc = null;

                throw new Win32Exception(err, "Failed to install input hooks");
            }

            // P8: any invocation proves a hook alive; initialize both ticks now so a freshly-started
            // idle app is never mistaken for stale by the watchdog before its first real event.
            var startTick = Stopwatch.GetTimestamp();
            Volatile.Write(ref _lastKeyboardEventTick, startTick);
            Volatile.Write(ref _lastMouseEventTick, startTick);
            // Seed Anti-AFK's keyboard-idle basis too, so a freshly-started app accumulates idle from
            // 0 (not "infinitely idle" → an immediate spurious fire).

            // P7: request 1ms timer resolution while hooks are live. Win11 silently ignores
            // resolution requests from hidden/minimized-window processes (this app's tray state
            // while gaming) unless the process first opts out of power throttling for timers —
            // control-bit set + state-bit clear = "always honor this process's requested
            // resolution." Best-effort: SetProcessInformation fails pre-Win11 (ignored, logged at
            // debug); timeBeginPeriod success is remembered so later failures/Stop() can pair it.
            try
            {
                var throttleState = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = 1,
                    ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                    StateMask = 0
                };
                if (!NativeMethods.SetProcessInformation(NativeMethods.GetCurrentProcess(),
                        NativeMethods.ProcessPowerThrottling, ref throttleState,
                        (uint)Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>()))
                {
                    LogDebug($"SetProcessInformation(IGNORE_TIMER_RESOLUTION) failed (pre-Win11?): 0x{Marshal.GetLastWin32Error():X}");
                }

                _timerResolutionRaised = NativeMethods.timeBeginPeriod(1) == 0; // TIMERR_NOERROR
                if (!_timerResolutionRaised)
                {
                    LogDebug("timeBeginPeriod(1) failed to raise timer resolution");
                }

                // Recover from a desktop switch that swallows the button-up (lock screen, logoff):
                // without this, an injected hold-breath key would stay down until the next click.
                SystemEvents.SessionSwitch += OnSessionSwitch;

                // P8 rework: per-device liveness side channel for the watchdog. Best-effort — Start()
                // is on the dispatcher (required: the message-only window must live on the pumped
                // thread). On failure the watchdog degrades to detection-only logging; hook
                // installation itself must not fail over a diagnostics channel.
                try
                {
                    _rawInputSink = new RawInputLivenessSink();
                }
                catch (Exception ex)
                {
                    _rawInputSink = null;
                    LogDebug($"WARNING: raw-input liveness sink unavailable ({ex.Message}); hook-loss watchdog is detection-only");
                }
                _keyboardSinkOpen = false;
                _mouseSinkOpen = false;
                // Fresh session: the next confirmed hook-loss episode reports immediately (this is a
                // report throttle, not a key-pairing latch, so a blind clear is safe here).
                _watchdogHookLossReportedAtTick = 0;

                _inputExecutor.Start();

                // P8: hook-loss watchdog. 10s period is coarse on purpose — this only needs to catch
                // the rare silent hook removal (UI stall > LowLevelHooksTimeout), not run hot.
                _hookWatchdogTimer = new System.Threading.Timer(_ => WatchdogTick(), null, WATCHDOG_PERIOD_MS, WATCHDOG_PERIOD_MS);

                _antiAfk.Start();
            }
            catch
            {
                // Full rollback before rethrow: _runtime.IsRunning is still false here, so Stop() will never
                // run to unhook — and a retried Start() would otherwise stack a second pair of LL
                // hooks on top of these. Mirrors the hook-install-failure branch above.
                _hookWatchdogTimer?.Dispose();
                _hookWatchdogTimer = null;

                _antiAfk.Stop();
                _inputExecutor.StopAndDrain();

                _rawInputSink?.Dispose();
                _rawInputSink = null;

                SystemEvents.SessionSwitch -= OnSessionSwitch;

                // Pairing discipline: a later Start() step must not leave timeBeginPeriod unmatched.
                if (_timerResolutionRaised)
                {
                    NativeMethods.timeEndPeriod(1);
                    _timerResolutionRaised = false;
                }

                if (_keyboardHookHandle != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
                    _keyboardHookHandle = IntPtr.Zero;
                }

                if (_mouseHookHandle != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
                    _mouseHookHandle = IntPtr.Zero;
                }

                _keyboardProc = null;
                _mouseProc = null;

                throw;
            }

            try
            {
            // codex-final #2: a genuine (re)start is the only place to (re)seed the physical-Caps latch — a
            // mid-hold profile switch / watchdog reinstall keep the hook running and never call Start(), so
            // they PRESERVE the latch (a held Caps's UP still pairs with its original DOWN). SEED from the
            // ACTUAL physical key state, NOT blindly to false: if Caps is already held across Stop->Start (or
            // at initial launch) Windows already received that DOWN, so mark the press in-progress and NOT
            // suppressed — the carryover repeats + UP then PASS THROUGH and pair with Windows' DOWN instead
            // of a suppressed orphan UP (= stuck CapsLock). A not-held start seeds false -> next press is
            // fresh. Hooks are installed above but _runtime.IsRunning is still false, so no callback is honored yet.
            _remaps.SeedCapsPhysicalState(
                (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CAPITAL) & 0x8000) != 0);

            // Fresh session: SEED the color-toggle fire-once latch from the ACTUAL physical key state (mirrors
            // the Caps seed above). If the toggle key is still held across Stop->Start its press already fired,
            // so latch TRUE so its post-restart typematic repeats DON'T re-fire (a blind clear would
            // double-fire); its UP then clears it. Not held -> false -> the next press fires. Sync
            // _hookSeenToggleVk so HandleColorToggle's reconciliation doesn't immediately clear this seed.
            var colorToggleVk = _colorToggleVk;
            _colorToggleDownLatched = colorToggleVk != 0 && (NativeMethods.GetAsyncKeyState(colorToggleVk) & 0x8000) != 0;
            _hookSeenToggleVk = colorToggleVk;

            // Seed the Alt+Keyboard typematic latches from the ACTUAL physical key state (same rationale
            // as the Caps seed): a trigger key already held across Stop->Start had its DOWN delivered to
            // Windows, so its carryover repeats + UP must PASS THROUGH and pair with that DOWN —
            // PhysicallyDown=true (repeats are not fresh edges) and no suppression latch. Keys not held
            // seed false, so the next press is a fresh gesture. Hooks are installed but _runtime.IsRunning is
            // still false, so no callback can overwrite this baseline yet; the CORE runs inline (not
            // via the dispatcher marshaling) so the seed lands before _runtime.IsRunning flips. The panic
            // trigger's fresh-edge latch seeds the same way (keyboard triggers only), after resetting
            // the derivation epoch — a ticket left outstanding by the previous session must not keep
            // Early Cancel fenced on the new one.
            _gestures.RederivePhysicalState(IsPhysicalKeyDown);

            // Rapid Fire is runtime-only and always starts disarmed (Start never raises the arm
            // event — it is Off by definition). Seed the physical latches so a key or left button
            // held across restart cannot be mistaken for a fresh press.
            _rapidFire.Release(preservePhysicalPairing: false);
            _rapidFire.SeedTogglePhysicalState(IsPhysicalKeyDown);
            var physicalLeftVk = NativeMethods.GetSystemMetrics(NativeMethods.SM_SWAPBUTTON) != 0
                ? NativeMethods.VK_RBUTTON
                : NativeMethods.VK_LBUTTON;
            _rapidFire.SeedPhysicalLeftButton(
                (NativeMethods.GetAsyncKeyState(physicalLeftVk) & 0x8000) != 0);

            // Seed Auto-Run's movement-edge tracker at the hook-stream boundary. Callbacks are installed
            // but still gated by _runtime.IsRunning=false, so this baseline cannot overwrite a newer hook event.
            // Once live, ordered W/S hook events own the state until the next genuine hook boundary.
            _autoRun.SeedMovementPhysicalState();

            _runtime.SetRunning(true);
            LogDebug("InputHookService started");
            }
            catch
            {
                // Late physical-state seeding is still part of Start. Reuse the normal teardown so
                // a failed seed cannot leave hooks, timers, or the executor live for a retry.
                _runtime.SetRunning(true);
                Stop();
                throw;
            }
        }
    }

    public void Stop()
    {
        var rapidFireArmCleared = false;
        lock (_profileLock)
        {
            if (!_runtime.IsRunning)
            {
                return;
            }

            _hookWatchdogTimer?.Dispose();
            _hookWatchdogTimer = null;

            // Stop new Anti-AFK ticks. An in-flight tick re-checks _runtime.IsRunning (flipped false below) and
            // the injector drain skips any sequence item it enqueued.
            _antiAfk.Stop();

            // Unregisters any open device sinks from whatever thread Stop() runs on; the message-only
            // window itself is only destroyed when this is the owning (dispatcher) thread — the
            // App.OnExit path runs Stop() on a pool thread, where the moribund window is left for
            // process teardown (see RawInputLivenessSink.Dispose).
            _rawInputSink?.Dispose();
            _rawInputSink = null;
            _keyboardSinkOpen = false;
            _mouseSinkOpen = false;

            SystemEvents.SessionSwitch -= OnSessionSwitch;

            if (_keyboardHookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
                _keyboardHookHandle = IntPtr.Zero;
            }

            if (_mouseHookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }

            // Flip the running flag BEFORE releasing state: an in-flight hook callback that already
            // passed its entry check re-validates _runtime.IsRunning under the subsystem locks, so it can no
            // longer inject AFTER ReleaseAllState ran — with the hooks gone, nothing would ever
            // release such a key and it would stay stuck system-wide beyond process exit.
            _runtime.SetRunning(false);

            rapidFireArmCleared = ReleaseAllState(preservePhysicalPairing: false);
            // §11.6: ReleaseAllState skips a decoupled Background Auto-Run; Stop() (app exit) must still
            // release it — post the final UP before the injector drains below.
            _autoRun.Release(includeBackground: true);
            // Off-hook (app lifecycle): join the Background thread so its AttachThreadInput is undone
            // before we tear down further. ReleaseAutoRunState above only SIGNALS stop (hook-safe).
            _autoRun.JoinBackgroundInputThread();

            _inputExecutor.StopAndDrain(
                () => { },
                TimeSpan.FromSeconds(2));

            // P7 pairing: winmm requires matched Begin/End calls. Stop() is already idempotent via
            // the _runtime.IsRunning guard above, so this fires exactly once per successful Start().
            if (_timerResolutionRaised)
            {
                NativeMethods.timeEndPeriod(1);
                _timerResolutionRaised = false;
            }

            LogDebug("InputHookService stopped");
        }

        // Raised only after _profileLock closed (Stop is a hard boundary — the arm is gone).
        if (rapidFireArmCleared)
        {
            RaiseRapidFireArmChanged();
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // Desktop is BACK: the away-side handler below hard-cleared every latch (the desktop was
        // gone; the UPs were never seen). Re-baseline from the ACTUAL physical keys now that input
        // flows again — a trigger key still held across the transition must classify its repeats as
        // repeats (not fresh presses), and a still-held Alt must keep gating. The foreground
        // watcher may NOT provide this via re-activation when the game stayed foreground across
        // the lock/unlock, so the boundary itself must re-derive.
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or
                             SessionSwitchReason.RemoteConnect)
        {
            lock (_profileLock)
            {
                if (!_runtime.IsRunning)
                {
                    return;
                }

                RederivePhysicalModifierState();
                _autoRun.SeedMovementPhysicalState();
                LogDebug($"Session switch ({e.Reason}): re-derived physical state");
            }

            return;
        }

        // When the desktop switches away mid-press, the low-level hook never sees the button-up,
        // which would leave injected keys (hold-breath, combined overrides) stuck down.
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or
                             SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect))
        {
            return;
        }

        var rapidFireArmCleared = false;
        lock (_profileLock)
        {
            if (!_runtime.IsRunning)
            {
                return;
            }

            rapidFireArmCleared = ReleaseAllState(preservePhysicalPairing: false);
            // §11.6: ReleaseAllState skips a decoupled Background Auto-Run; the desktop is going away
            // (lock/logoff), so release it here too.
            _autoRun.Release(includeBackground: true);
            // The retained Anti-AFK background target belongs to a live desktop session too — the
            // game window's input queue is going away with it.
            _antiAfk.ReleaseForegroundTarget();
            LogDebug($"Session switch ({e.Reason}): released all injected state");
        }

        // Raised only after _profileLock closed (hard boundary — the arm is gone).
        if (rapidFireArmCleared)
        {
            RaiseRapidFireArmChanged();
        }
    }

    // ==================== HOOK-LOSS WATCHDOG (P8) ====================

    // Windows (Win7+) silently removes an LL hook whose callback exceeds LowLevelHooksTimeout (HKCU,
    // ~300ms class default, hard-capped 1000ms since Win10 1709) — no notification, no recovery
    // without this. Detection runs on the timer thread (cheap, lock-free reads); the actual
    // re-install is marshaled onto _hookDispatcher and takes _profileLock, same as every other
    // lifecycle mutation.
    //
    // Two-stage design: "hook quiet 30s while system input is fresh" is only SUSPICION — the fresh
    // input may be the OTHER device (GetLastInputInfo is global), which is exactly what normal
    // mouse-only aiming or keyboard-only typing looks like. Suspicion opens a per-device raw-input
    // sink (RawInputLivenessSink); only raw input for THAT device arriving while its hook stays
    // silent CONFIRMS loss and triggers a reinstall. A hook event closes the sink (proof of life),
    // so a healthy system carries zero raw-input traffic.
    private void WatchdogTick()
    {
        if (!_runtime.IsRunning)
        {
            return;
        }

        // Single-flight: System.Threading.Timer gives no overlap guarantee, and the sink-open flags
        // are single-writer state owned by this method (Interlocked entry/exit also fences the flag
        // writes for the next tick).
        if (Interlocked.CompareExchange(ref _watchdogTickRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!_hookWatchdogEnabled)
            {
                // Disabled mid-suspicion: close any open sinks (inside the single-flight section —
                // same single-writer discipline as every other sink transition) and stand down.
                if (_keyboardSinkOpen)
                {
                    _rawInputSink?.UnregisterKeyboard();
                    _keyboardSinkOpen = false;
                    LogDebug("Watchdog disabled: closed keyboard raw-input liveness sink");
                }

                if (_mouseSinkOpen)
                {
                    _rawInputSink?.UnregisterMouse();
                    _mouseSinkOpen = false;
                    LogDebug("Watchdog disabled: closed mouse raw-input liveness sink");
                }

                return;
            }

            if (!TryGetWatchdogAges(out var systemInputAgeMs, out var keyboardIdleMs, out var mouseIdleMs,
                    out var keyboardRawAgeMs, out var mouseRawAgeMs))
            {
                return; // best-effort; try again next period
            }

            var keyboardAction = DecideWatchdogAction(keyboardIdleMs, systemInputAgeMs, _keyboardSinkOpen,
                keyboardRawAgeMs, WATCHDOG_STALE_HOOK_THRESHOLD_MS, WATCHDOG_FRESH_INPUT_THRESHOLD_MS);
            var mouseAction = DecideWatchdogAction(mouseIdleMs, systemInputAgeMs, _mouseSinkOpen,
                mouseRawAgeMs, WATCHDOG_STALE_HOOK_THRESHOLD_MS, WATCHDOG_FRESH_INPUT_THRESHOLD_MS);

            ApplySinkTransition(keyboardAction, isKeyboard: true, keyboardIdleMs);
            ApplySinkTransition(mouseAction, isKeyboard: false, mouseIdleMs);

            var reinstallKeyboard = keyboardAction == WatchdogAction.Reinstall;
            var reinstallMouse = mouseAction == WatchdogAction.Reinstall;

            if (!reinstallKeyboard && !reinstallMouse)
            {
                // Episode boundary (decisions fell back to None/OpenSink/CloseSink — the hook proved
                // alive or is merely under suspicion): the next confirmed loss reports immediately.
                _watchdogHookLossReportedAtTick = 0;
                return;
            }

            LogDebug($"Watchdog: hook loss CONFIRMED by raw-input sink (keyboard={reinstallKeyboard}, mouse={reinstallMouse}, " +
                     $"kbIdle={keyboardIdleMs:F0}ms, mouseIdle={mouseIdleMs:F0}ms, kbRawAge={keyboardRawAgeMs:F0}ms, mouseRawAge={mouseRawAgeMs:F0}ms)");

            // Near-crash report (always on, independent of the debug toggle). Neither the episode
            // boundary above nor the _reinstallCheckPending CAS below bounds this site — the
            // degraded branch returns before the CAS and this one precedes it — so throttle
            // directly: a persistent Reinstall decision would otherwise re-fire every 10s tick.
            if (ShouldReportHookLoss(_watchdogHookLossReportedAtTick, Environment.TickCount, WATCHDOG_CRASH_REREPORT_MS))
            {
                CrashReporter.Write("InputHook.Watchdog.HookLossConfirmed", null,
                    $"keyboard={reinstallKeyboard}, mouse={reinstallMouse}, kbIdle={keyboardIdleMs:F0}ms, mouseIdle={mouseIdleMs:F0}ms, kbRawAge={keyboardRawAgeMs:F0}ms, mouseRawAge={mouseRawAgeMs:F0}ms");
                _watchdogHookLossReportedAtTick = Environment.TickCount;
            }

            if (!_canReinstallHooks || _hookDispatcher is null)
            {
                LogDebug("Watchdog: re-install is disabled (hooks were not installed on a dispatcher-pumped thread) — detection only");

                // Same throttle latch as the confirmed site: both describe one underlying hook-loss
                // episode, so sharing the stamp suppresses double-reporting when they alternate.
                if (ShouldReportHookLoss(_watchdogHookLossReportedAtTick, Environment.TickCount, WATCHDOG_CRASH_REREPORT_MS))
                {
                    CrashReporter.Write("InputHook.Watchdog.ReinstallDisabled", null,
                        "hooks were not installed on a dispatcher-pumped thread; detection only");
                    _watchdogHookLossReportedAtTick = Environment.TickCount;
                }
                return;
            }

            // Only one re-install check may be queued at a time (see _reinstallCheckPending) — otherwise
            // a stalled dispatcher accumulates one stale closure per missed 10s period, and every one of
            // them would reinstall + ReleaseAllState even after the first already fixed the hook.
            if (Interlocked.CompareExchange(ref _reinstallCheckPending, 1, 0) != 0)
            {
                return;
            }

            _hookDispatcher.InvokeAsync(() =>
            {
                try
                {
                    lock (_profileLock)
                    {
                        if (!_runtime.IsRunning || !_hookWatchdogEnabled)
                        {
                            return;
                        }

                        // This closure may have sat queued on a stalled dispatcher — exactly the scenario
                        // the pending-guard exists for — so the decisions computed above can be stale by
                        // now. Recompute with CURRENT ticks and only reinstall a hook that is STILL
                        // silent with its device provably active (any hook event that landed meanwhile
                        // flips the decision to CloseSink and the reinstall is skipped).
                        if (!TryGetWatchdogAges(out var freshSystemInputAgeMs, out var freshKeyboardIdleMs,
                                out var freshMouseIdleMs, out var freshKeyboardRawAgeMs, out var freshMouseRawAgeMs))
                        {
                            return; // best-effort; the periodic tick will retry
                        }

                        if (DecideWatchdogAction(freshKeyboardIdleMs, freshSystemInputAgeMs, _keyboardSinkOpen,
                                freshKeyboardRawAgeMs, WATCHDOG_STALE_HOOK_THRESHOLD_MS, WATCHDOG_FRESH_INPUT_THRESHOLD_MS) == WatchdogAction.Reinstall)
                        {
                            ReinstallKeyboardHookLocked();
                        }

                        if (DecideWatchdogAction(freshMouseIdleMs, freshSystemInputAgeMs, _mouseSinkOpen,
                                freshMouseRawAgeMs, WATCHDOG_STALE_HOOK_THRESHOLD_MS, WATCHDOG_FRESH_INPUT_THRESHOLD_MS) == WatchdogAction.Reinstall)
                        {
                            ReinstallMouseHookLocked();
                        }
                    }
                }
                finally
                {
                    Volatile.Write(ref _reinstallCheckPending, 0);
                }
            });
        }
        finally
        {
            Volatile.Write(ref _watchdogTickRunning, 0);
        }
    }

    // Applies an OpenSink/CloseSink decision. Runs only inside WatchdogTick's single-flight section,
    // which is what makes the sink-open flags single-writer. Reinstall/None are no-ops here: after a
    // reinstall the freshly-stamped hook tick makes the NEXT tick close the sink via CloseSink, which
    // doubles as post-reinstall verification.
    private void ApplySinkTransition(WatchdogAction action, bool isKeyboard, double hookIdleMs)
    {
        var deviceName = isKeyboard ? "keyboard" : "mouse";

        // Snapshot once: Stop() nulls the field concurrently with an in-flight tick (Timer.Dispose
        // does not wait for running callbacks). The sink's own methods are disposed-guarded, so the
        // worst case on a captured stale instance is a refused no-op registration.
        var sink = _rawInputSink;

        switch (action)
        {
            case WatchdogAction.OpenSink:
                if (sink is null)
                {
                    // Degraded mode (sink creation failed in Start): idle-vs-dead cannot be answered,
                    // so never guess-reinstall — log the suspicion and stay put.
                    LogDebug($"Watchdog: {deviceName} hook quiet {hookIdleMs / 1000:F0}s while system input is fresh — liveness sink unavailable, detection only");
                    return;
                }

                if (isKeyboard ? sink.RegisterKeyboard() : sink.RegisterMouse())
                {
                    if (isKeyboard) { _keyboardSinkOpen = true; } else { _mouseSinkOpen = true; }
                    LogDebug($"Watchdog: {deviceName} hook quiet {hookIdleMs / 1000:F0}s while system input is fresh — opened raw-input liveness sink");
                }
                else
                {
                    LogDebug($"Watchdog: failed to open {deviceName} raw-input sink (0x{Marshal.GetLastWin32Error():X}); retrying next period");
                }
                break;

            case WatchdogAction.CloseSink:
                // Clear the flag even if unregistration fails: a lingering registration only means
                // harmless tick stamps, and the next OpenSink re-registers the same target anyway.
                var unregistered = isKeyboard ? sink?.UnregisterKeyboard() : sink?.UnregisterMouse();
                if (isKeyboard) { _keyboardSinkOpen = false; } else { _mouseSinkOpen = false; }
                LogDebug($"Watchdog: {deviceName} hook proved alive; closed raw-input liveness sink (unregistered={unregistered})");
                break;
        }
    }

    // Shared by WatchdogTick's preliminary check and its dispatcher-marshaled recheck: system-wide
    // input age (GetLastInputInfo), each hook's idle time from its liveness tick, and each device's
    // raw-input age from the liveness sink (double.MaxValue when the sink is closed, unavailable, or
    // has seen nothing since it was opened). Returns false if GetLastInputInfo fails (best-effort;
    // caller retries next period).
    private bool TryGetWatchdogAges(out uint systemInputAgeMs, out double keyboardIdleMs, out double mouseIdleMs,
        out double keyboardRawAgeMs, out double mouseRawAgeMs)
    {
        keyboardRawAgeMs = double.MaxValue;
        mouseRawAgeMs = double.MaxValue;

        var lii = new NativeMethods.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref lii))
        {
            systemInputAgeMs = 0;
            keyboardIdleMs = 0;
            mouseIdleMs = 0;
            return false;
        }

        // Both operands are in the 32-bit Environment.TickCount domain; unchecked uint subtraction
        // survives wraparound (~49.7 days).
        systemInputAgeMs = unchecked((uint)Environment.TickCount - lii.dwTime);

        var nowTicks = Stopwatch.GetTimestamp();
        keyboardIdleMs = (nowTicks - Volatile.Read(ref _lastKeyboardEventTick)) * TickToMilliseconds;
        mouseIdleMs = (nowTicks - Volatile.Read(ref _lastMouseEventTick)) * TickToMilliseconds;

        var sink = _rawInputSink;
        if (sink is not null)
        {
            var keyboardRawTick = sink.LastKeyboardRawTick;
            if (keyboardRawTick != 0)
            {
                keyboardRawAgeMs = (nowTicks - keyboardRawTick) * TickToMilliseconds;
            }

            var mouseRawTick = sink.LastMouseRawTick;
            if (mouseRawTick != 0)
            {
                mouseRawAgeMs = (nowTicks - mouseRawTick) * TickToMilliseconds;
            }
        }

        return true;
    }

    internal enum WatchdogAction
    {
        None,
        OpenSink,
        CloseSink,
        Reinstall
    }

    // Pure decision function (P8, unit-tested), two-stage.
    // Sink closed: "hook quiet past the stale threshold while SOMETHING provides fresh system input"
    // is only suspicion — GetLastInputInfo is global, so this is indistinguishable from normal
    // single-device use (mouse-only aiming, keyboard-only typing). Open the per-device sink to find
    // out; never reinstall on suspicion alone.
    // Sink open: raw input for THIS device arriving (fresh rawInputAgeMs) while its hook stays
    // silent is proof the hook was silently removed -> Reinstall. The hook seeing an event again is
    // proof of life -> CloseSink. A quiet device decides nothing — idle is not death — and the open
    // sink costs nothing while no events flow.
    internal static WatchdogAction DecideWatchdogAction(double hookIdleMs, uint systemInputAgeMs, bool sinkOpen,
        double rawInputAgeMs, double staleHookThresholdMs, uint freshInputThresholdMs)
    {
        if (!sinkOpen)
        {
            return systemInputAgeMs < freshInputThresholdMs && hookIdleMs > staleHookThresholdMs
                ? WatchdogAction.OpenSink
                : WatchdogAction.None;
        }

        if (hookIdleMs <= staleHookThresholdMs)
        {
            return WatchdogAction.CloseSink;
        }

        return rawInputAgeMs < freshInputThresholdMs
            ? WatchdogAction.Reinstall
            : WatchdogAction.None;
    }

    // Pure decision function for the near-crash hook-loss report (unit-tested, DecideWatchdogAction
    // precedent). Edge-trigger + bounded re-report: fire on the first tick of an episode (sentinel
    // 0), then at most once per interval while the loss persists. Both operands are
    // Environment.TickCount values, so unchecked subtraction handles the ~49.7-day wrap.
    internal static bool ShouldReportHookLoss(int lastReportedAtTick, int nowTick, int rereportIntervalMs)
        => lastReportedAtTick == 0 || unchecked(nowTick - lastReportedAtTick) >= rereportIntervalMs;

    // Must run on _hookDispatcher, under _profileLock, with _runtime.IsRunning already re-checked by the
    // caller. Install-new-before-unhook-old with a fail-open swap window (see
    // _keyboardReplacementInProgress declaration): both registrations would invoke the SAME kept-alive
    // delegate and LL callbacks receive no registration identity, so overlap idempotency alone is not
    // enough — MouseCallback's side effects are non-suppressing.
    private void ReinstallKeyboardHookLocked()
    {
        _keyboardReplacementInProgress = true;

        var user32Handle = NativeMethods.LoadLibrary("user32.dll");
        if (user32Handle == IntPtr.Zero)
        {
            _keyboardReplacementInProgress = false;
            LogDebug($"Watchdog: keyboard hook re-install failed to load user32.dll (0x{Marshal.GetLastWin32Error():X})");
            return;
        }

        // _runtime.IsRunning re-checked by the caller guarantees _keyboardProc was assigned in Start() and
        // never reset (only the Start()-failure path nulls it, which never sets _runtime.IsRunning true).
        var newHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc!, user32Handle, 0);
        if (newHandle == IntPtr.Zero)
        {
            // Fail open: keep the existing (possibly-dead) handle and do NOT stamp the liveness
            // tick — a false positive can never lose a live hook; the watchdog retries next period.
            var err = Marshal.GetLastWin32Error();
            _keyboardReplacementInProgress = false;
            LogDebug($"ERROR: Watchdog keyboard hook re-install FAILED (0x{err:X}); keeping existing handle, retrying next period");
            return;
        }

        var oldHandle = _keyboardHookHandle;
        NativeMethods.UnhookWindowsHookEx(oldHandle); // ignore result: handle may already be dead
        _keyboardHookHandle = newHandle;
        LogDebug("WARNING: Watchdog re-installed a silently-removed keyboard hook");

        // Missed-release safety: a hook that died mid-press has missed physical UPs (e.g. a combined
        // override never released). Reuse the proven release path — same as a profile switch —
        // rather than reconstruct partial state; this also cleans up anything that passed through
        // unprocessed during the fail-open window above. The desktop is NOT going away here (unlike
        // Stop()/OnSessionSwitch), so re-derive afterward (P9) — a physically-held Alt/RMB must not
        // end up inert just because a false-positive watchdog refresh ran.
        // Sticky arm: preserved across the reinstall; only the in-flight press is cancelled. Recovery
        // is intentionally invisible (no RapidFireArmChanged raise).
        ReleaseAllState(preserveRapidFireArm: true);
        RederivePhysicalModifierState();
        SeedRapidFirePhysicalLeftDown();

        // The fail-open replacement window may have missed W/S transitions. Callbacks remain gated by
        // _keyboardReplacementInProgress until this method returns, so native state is safe as the new
        // event-stream baseline here (unlike from inside HandleAutoRun/ActivateAutoRun).
        _autoRun.SeedMovementPhysicalState();

        Volatile.Write(ref _lastKeyboardEventTick, Stopwatch.GetTimestamp());
        _keyboardReplacementInProgress = false;
    }

    // See ReinstallKeyboardHookLocked — identical sequence, independent hook/flag/handle (per-hook
    // independence: a stall kills only the hook that had an event pending).
    private void ReinstallMouseHookLocked()
    {
        _mouseReplacementInProgress = true;

        var user32Handle = NativeMethods.LoadLibrary("user32.dll");
        if (user32Handle == IntPtr.Zero)
        {
            _mouseReplacementInProgress = false;
            LogDebug($"Watchdog: mouse hook re-install failed to load user32.dll (0x{Marshal.GetLastWin32Error():X})");
            return;
        }

        var newHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc!, user32Handle, 0);
        if (newHandle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            _mouseReplacementInProgress = false;
            LogDebug($"ERROR: Watchdog mouse hook re-install FAILED (0x{err:X}); keeping existing handle, retrying next period");
            return;
        }

        var oldHandle = _mouseHookHandle;
        NativeMethods.UnhookWindowsHookEx(oldHandle);
        _mouseHookHandle = newHandle;
        LogDebug("WARNING: Watchdog re-installed a silently-removed mouse hook");

        // Missed-release safety + re-derive (see ReinstallKeyboardHookLocked): the desktop is NOT
        // going away here, so a physically-held Alt/RMB must not end up inert after this refresh.
        // Sticky arm preserved (see ReinstallKeyboardHookLocked); recovery stays invisible.
        ReleaseAllState(preserveRapidFireArm: true);
        RederivePhysicalModifierState();
        SeedRapidFirePhysicalLeftDown();

        Volatile.Write(ref _lastMouseEventTick, Stopwatch.GetTimestamp());
        _mouseReplacementInProgress = false;
    }

    public void ActivateProfile(Profile profile, long foregroundGeneration)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var changed = false;
        var generationChanged = false;
        lock (_profileLock)
        {
            if (!_runtime.IsRunning)
            {
                return;
            }

            if (ReferenceEquals(_runtime.ActiveProfile, profile))
            {
                // Same-instance republish (same-exe window switch, RepublishLatestForeground,
                // delayed-callback revalidation): the generation bump can settle the sticky arm
                // (gray -> ready). Without this raise the dot would stay gray forever after the
                // focus returned, because the earlier SetForegroundIdentity raised gray against a
                // generation this profile had not caught up to yet. No early return here — the
                // raise lives AFTER the lock.
                generationChanged = _runtime.ActiveProfileGeneration != foregroundGeneration;
                _runtime.SetActiveProfile(profile, foregroundGeneration);
                _antiAfk.CaptureForegroundTarget(profile);
            }
            else
            {
                // Sticky arm: preserved across the switch — it only ever clicks while its owner is
                // the settled active profile, and the SetForegroundIdentity/this-method raises
                // cover the status flips. No arm raise on THIS path (nothing about the arm changed).
                ReleaseAllState(preserveRapidFireArm: true);
                // Publish the incoming profile BEFORE scheduling the re-derivation (the panic-latch
                // closure reads the live _runtime.ActiveProfile at dispatcher-execution time and must see
                // the INCOMING trigger), but keep the GENERATION unsettled until AFTER the physical
                // modifier baseline is restored: the mismatch fences hook handlers
                // (ProfileInputGenerationIsCurrent) so no new-profile action can observe eligible
                // state with stale Alt/right-button modifiers (H6).
                _runtime.SetActiveProfileReference(profile);
                RederivePhysicalModifierState();
                _runtime.SetActiveProfileGeneration(foregroundGeneration);
                // Retain the game window for Anti-AFK's Background/Forced posting. Necessarily runs
                // AFTER the profile/generation are published (the lock-free tick already observes
                // them), which is why the capture's failure path CAS-clears foreign-owned targets
                // and the tick re-checks ownership per step.
                _antiAfk.CaptureForegroundTarget(profile);
                changed = true;

                LogDebug($"Profile activated: {profile.Name}");
            }
        }

        if (changed)
        {
            ActiveProfileChanged?.Invoke(this, profile);
        }
        else if (generationChanged)
        {
            // Same profile, new generation: ActiveProfileChanged deliberately stays silent — only
            // the arm status can have flipped.
            RaiseRapidFireArmChanged();
        }
    }

    public void DeactivateProfile(long foregroundGeneration)
    {
        Profile? previous;

        lock (_profileLock)
        {
            previous = _runtime.ActiveProfile;
            if (previous is null)
            {
                _runtime.SetActiveProfile(null, foregroundGeneration);
                return;
            }

            // Sticky arm: preserved (the owner is simply no longer active -> not ready). The
            // preceding SetForegroundIdentity raise already flipped the dot to gray.
            ReleaseAllState(preserveRapidFireArm: true);
            // Publish the profile first (see ActivateProfile); the generation settles only after
            // the physical baseline is restored. No active profile => no trigger to re-derive.
            _runtime.SetActiveProfileReference(null);
            RederivePhysicalModifierState();
            _runtime.SetActiveProfileGeneration(foregroundGeneration);

            LogDebug("Profile deactivated");
        }

        ActiveProfileChanged?.Invoke(this, null);
    }

    public void ReconcileProfileSettings(Profile profile, ProfileChangeKind changeKind)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (changeKind == ProfileChangeKind.None)
        {
            return;
        }

        var active = ReferenceEquals(_runtime.ActiveProfile, profile);
        var windows = ReferenceEquals(_windowsProfile, profile);
        var hardDeactivate = active &&
            ((changeKind & ProfileChangeKind.Removed) != 0 ||
             ((changeKind & ProfileChangeKind.Master) != 0 && !profile.IsEnabled));

        if (hardDeactivate)
        {
            var notify = false;
            lock (_profileLock)
            {
                if (ReferenceEquals(_runtime.ActiveProfile, profile))
                {
                    if (windows)
                    {
                        // Invalidate queued launcher work before the active Windows profile is
                        // unpublished; ReleaseAllState preserves launcher pairing on this path.
                        _remaps.ReconcileProfileSettings(profile, changeKind);
                    }

                    // Sticky arm: preserved through the in-lock teardown (only the press is
                    // cancelled); the owner release happens post-lock via the single
                    // ReleaseRapidFireOwnedBy authority below. Safe window: the active
                    // generation/profile was already invalidated inside this lock, so no new
                    // Rapid Fire press can start during the handoff.
                    ReleaseAllState(preserveRapidFireArm: true);
                    // Publish the profile first; the generation settles only after the physical
                    // baseline is restored (see ActivateProfile). No active profile => no trigger.
                    _runtime.SetActiveProfileReference(null);
                    RederivePhysicalModifierState();
                    _runtime.SetActiveProfileGeneration(long.MinValue);
                    notify = true;
                }
            }

            _autoRun.ReleaseOwnedBy(profile);
            _antiAfk.ReleaseOwnedBy(profile);
            if (_rapidFire.ReleaseOwnedBy(profile))
            {
                RaiseRapidFireArmChanged();
            }
            if (notify)
            {
                ActiveProfileChanged?.Invoke(this, null);
            }
            return;
        }

        if ((changeKind & ProfileChangeKind.AutoRun) != 0)
        {
            _autoRun.ConfigurationChanged(profile);
        }

        if ((changeKind & (ProfileChangeKind.AutoRun | ProfileChangeKind.Removed)) != 0)
        {
            _autoRun.ReleaseOwnedBy(profile);
        }

        if (active || windows)
        {
            _remaps.ReconcileProfileSettings(profile, changeKind);
        }

        if (active)
        {
            if ((changeKind & ProfileChangeKind.AltMouse) != 0)
            {
                _gestures.ReleaseAltMouse(preserveSuppressedUps: true);
            }
            if ((changeKind & ProfileChangeKind.AltKeyboard) != 0)
            {
                _gestures.ReleaseAltKeyboard(preserveSuppressedUps: true);
            }
            if ((changeKind & ProfileChangeKind.HoldBreath) != 0)
            {
                _gestures.ReleaseHoldBreath();
                // The trigger may have been rebound while its (new or old) key is physically held:
                // re-derive the fresh-edge latch for the live trigger so a held key is not
                // misclassified as a fresh press for the new binding.
                SchedulePanicDerivation(
                    () => _gestures.RederivePanicTriggerPhysicalState(IsPhysicalKeyDown));
            }
        }

        // Owner-scoped (NOT active-scoped): an RF-config edit of the ACTIVE profile must not kill
        // a FOREIGN arm, and an edit of a non-active owner must still disarm it. Identity
        // (executable edit) invalidates the owner because it changes what "its own app" means.
        if ((changeKind & (ProfileChangeKind.RapidFire | ProfileChangeKind.Removed | ProfileChangeKind.Identity)) != 0 ||
            ((changeKind & ProfileChangeKind.Master) != 0 && !profile.IsEnabled))
        {
            if (_rapidFire.ReleaseOwnedBy(profile))
            {
                RaiseRapidFireArmChanged();
            }
        }

        // Anti-AFK's retained background target: released ONLY on the identity/removal/master-disable
        // boundaries (and the hard-deactivation branch above). ProfileChangeKind.AntiAfk is
        // deliberately EXCLUDED — the Mode dropdown and interval edits raise that kind, those
        // settings are read live by the tick, and the target is recaptured only on activation, so
        // releasing here would make a just-selected Background/Forced mode inert until the game is
        // refocused.
        if ((changeKind & (ProfileChangeKind.Removed | ProfileChangeKind.Identity)) != 0 ||
            ((changeKind & ProfileChangeKind.Master) != 0 && !profile.IsEnabled))
        {
            _antiAfk.ReleaseOwnedBy(profile);
        }

    }

    public void SetWindowsProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        
        lock (_profileLock)
        {
            _windowsProfile = profile;
            _remaps.SetWindowsProfile(profile);
            LogDebug($"Windows profile set: {profile.Name}");
        }
    }

    public void SetColorToggleKey(Key? key)
    {
        var vk = key.HasValue ? KeyInteropUtilities.ToVirtualKey(key.Value) : 0;

        // Modifiers can't be the toggle key: their physical-state reconstruction (the dual-Alt sibling check)
        // can't distinguish a "reserved" modifier from a real one, and firing a color toggle off Shift/Ctrl/
        // Alt/Win would be surprising. Treat a modifier assignment as unassigned.
        if (IsModifierVirtualKey(vk))
        {
            vk = 0;
        }

        // Publish ONLY the volatile VK from this (worker/UI) thread; the fire-once latch is owned by the hook
        // thread. Because the key is never suppressed, a stale latch across this change costs at most one
        // missed/extra flip — so no cross-thread latch reset (which would itself be a race) is needed.
        _colorToggleVk = vk;
    }

    public void SetRightButtonObservation(bool enabled)
    {
        _crosshairRightButtonWatch = enabled;

        if (!enabled)
        {
            return;
        }

        // Re-sync on arm: a WM_RBUTTONUP swallowed while we were not watching (secure desktop, hook
        // reinstall, app-start while the button was already held) must not leave the overlay stuck
        // hidden. Publish the CURRENT physical state once. GetAsyncKeyState reports the PHYSICAL
        // button, so honor the swap setting exactly like RederivePhysicalModifierState.
        var physicalRightVk = NativeMethods.GetSystemMetrics(NativeMethods.SM_SWAPBUTTON) != 0
            ? NativeMethods.VK_LBUTTON
            : NativeMethods.VK_RBUTTON;
        var isDown = (NativeMethods.GetAsyncKeyState(physicalRightVk) & 0x8000) != 0;
        RightButtonStateChanged?.Invoke(this, isDown);
    }

    public void SetRapidFireToggleKey(Key? key)
    {
        if (_rapidFire.SetToggleKey(key))
        {
            RaiseRapidFireArmChanged();
        }

    }

    private static bool IsModifierVirtualKey(int vk) =>
        vk is 0x10 or 0x11 or 0x12   // VK_SHIFT / VK_CONTROL / VK_MENU
           or 0xA0 or 0xA1           // VK_LSHIFT / VK_RSHIFT
           or 0xA2 or 0xA3           // VK_LCONTROL / VK_RCONTROL
           or 0xA4 or 0xA5           // VK_LMENU / VK_RMENU (Alt)
           or 0x5B or 0x5C;          // VK_LWIN / VK_RWIN

    public void Dispose()
    {
        if (!_runtime.TryBeginDispose())
        {
            return;
        }

        // Set first so any pool work item racing shutdown becomes a no-op instead of touching
        // torn-down state. ReleaseAllState() (via Stop) releases held keys before we get here.
        Stop();
        // Deliberately do NOT dispose _random: queued FireTapKey/hold-breath work items may still
        // deref _random.Value on a pool thread; ThreadLocal<Random> holds no unmanaged resources.
        _antiAfk.Dispose();
        _gestures.Dispose();
        _rapidFire.Dispose();
        _inputExecutor.Dispose();
    }

    // ==================== KEYBOARD HOOK ====================
    
    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // P8: any invocation proves the hook alive; must be the FIRST statement, before every guard.
        Volatile.Write(ref _lastKeyboardEventTick, Stopwatch.GetTimestamp());

        // P8 fail-open swap window: while a re-install is in flight for THIS hook, pass everything
        // through with zero side effects (see _keyboardReplacementInProgress declaration).
        if (_keyboardReplacementInProgress)
        {
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        if (nCode < 0 || _runtime.IsDisposed || !_runtime.IsRunning)
        {
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        var message = (int)wParam;

        if (message is not (NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN or
                            NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP))
        {
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        // P5: unsafe by-value read instead of Marshal.PtrToStructure<T> (which boxes on .NET 8).
        // KBDLLHOOKSTRUCT is blittable (uint/enum-uint/IntPtr); the copy is taken before
        // CallNextHookEx, so lifetime is safe. Unsafe code confined to this one read.
        NativeMethods.KBDLLHOOKSTRUCT data;
        unsafe
        {
            data = *(NativeMethods.KBDLLHOOKSTRUCT*)lParam;
        }

        // Ignore injected events from our own SendInput calls
        if ((data.flags & NativeMethods.KbdLlFlags.LLKHF_INJECTED) != 0 ||
            data.dwExtraInfo == NativeMethods.INPUT_IGNORE)
        {
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        bool isKeyDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        bool isKeyUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
        int vkCode = (int)data.vkCode;

        return DispatchDecodedKeyboardEvent(vkCode, isKeyDown, isKeyUp)
            ? (IntPtr)1
            : NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    // Hook-thread-only dispatcher. Native callbacks do liveness, replacement, running, message and
    // injected-event filtering before entering this allocation-free feature priority chain.
    internal bool DispatchDecodedKeyboardEvent(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        // Anti-AFK keyboard-idle basis: a genuine PHYSICAL key event (injected events returned above).
        var physicalTimestamp = Stopwatch.GetTimestamp();
        _antiAfk.NotePhysicalKeyboardActivity(physicalTimestamp);

        // Global color-variant toggle: fire on the assigned key (once per physical press). The key is NOT
        // suppressed — it passes through to apps and the feature chain below — so it can never strand a key or
        // create a wrong binding. Modifiers are rejected as toggle keys (SetColorToggleKey), so this never
        // shadows the Alt-tracking that follows.
        HandleColorToggle(vkCode, isKeyDown, isKeyUp);
        if (_rapidFire.HandleToggleKey(vkCode, isKeyDown, isKeyUp))
        {
            RaiseRapidFireArmChanged();
        }

        // Physical W/S observation must precede every feature that may consume/early-return this event.
        // In particular, Hold-Breath Early Cancel can own W-UP; Auto-Run still needs to complete its
        // physical handoff even when that feature ultimately suppresses the same target-visible event.
        var autoRunPhysicalEvent = _autoRun.ObservePhysicalEvent(vkCode, isKeyDown, isKeyUp);

        var suppressEarlyCancelKey = _gestures.HandlePanicKey(
            vkCode,
            isKeyDown,
            isKeyUp,
            _rightButtonPressed);

        // The original W-UP stays suppressed, but downstream paired consumers still need cleanup: a
        // combined mapping sourced from W may own a mapped target DOWN, and Win+W launcher may own its
        // held-key latch. Auto-Run suppression prevents the normal handled-chain from reaching either.
        if (autoRunPhysicalEvent.SuppressPhysicalWHandoffUp)
        {
            _remaps.ReleaseOwnedKeyUp(vkCode);
        }

        _gestures.ObserveAlt(
            vkCode,
            isKeyDown,
            isKeyUp,
            IsPhysicalKeyDown);

        if (suppressEarlyCancelKey)
        {
            // Hold-breath panic won this event before Alt+Keyboard could see it. The key may have an
            // Alt+Keyboard press in flight (same key bound as both trigger and panic trigger): cancel
            // the gesture and reconcile the latches so the orphaned timer can't still fire and the
            // next fresh press isn't swallowed as an owned repeat.
            _gestures.HandleAltKeyboardPanicOverride(vkCode, isKeyDown, isKeyUp);
            return true;
        }

        // Alt+Keyboard gestures (keyboard analog of HandleAltMouse in the mouse hook): while Alt is
        // held, keys arrive as WM_SYSKEYDOWN/WM_SYSKEYUP, both covered by isKeyDown/isKeyUp. A consumed
        // Alt+trigger press wins over remap/auto-run — exactly like an Alt+Right binding there.
        // Unbound keys and presses whose DOWN was not consumed fall through untouched; Alt itself is
        // never a trigger (no state exists for it), so it always passes through.
        if (_gestures.HandleAltKeyboard(vkCode, isKeyDown, isKeyUp))
        {
            return true;
        }

        // Auto-Run runs BEFORE the handled-chain: a cancel key (W/S) may ALSO be a combined-mapping
        // source, so it must be seen for cancel detection even when another feature would consume it.
        // Returns true for the trigger chord and for the one physical W-UP transferred into an active
        // Auto-Run handoff; ordinary W/S/sprint input passes through.
        if (_autoRun.Handle(vkCode, isKeyDown, isKeyUp, autoRunPhysicalEvent))
        {
            return true;
        }

        // Handle features in priority order
        return _remaps.HandleKeyboardEvent(vkCode, isKeyDown, isKeyUp, _rightButtonPressed);
    }

    // Global color-variant toggle. Fires ColorVariantToggleRequested ONCE per physical press (typematic
    // repeats ignored). Deliberately does NOT suppress the key — it passes through to apps — so it holds no
    // paired state and can never strand a key or fabricate a wrong binding across a re-assign / hook restart /
    // watchdog reinstall. (Users should pick a key not otherwise used, since it still reaches the focused app.)
    private void HandleColorToggle(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        var toggleVk = _colorToggleVk;

        // Clear the fire-once latch when the assigned key CHANGES, so the new key's first press fires (parity)
        // even if the old key's UP was never seen. Hook-thread-only; no suppression, so this can't strand a key.
        if (toggleVk != _hookSeenToggleVk)
        {
            _hookSeenToggleVk = toggleVk;
            _colorToggleDownLatched = false;
        }

        if (toggleVk == 0 || vkCode != toggleVk)
        {
            return;
        }

        if (isKeyDown)
        {
            if (!_colorToggleDownLatched)
            {
                _colorToggleDownLatched = true;
                ColorVariantToggleRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (isKeyUp)
        {
            _colorToggleDownLatched = false;
        }
    }

    // ==================== HOLD BREATH PANIC ====================

    // ==================== MOUSE HOOK ====================

    
    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // P8: any invocation proves the hook alive; must be the FIRST statement, before every guard
        // including the P5 message-type early-out below (moves still count as liveness).
        Volatile.Write(ref _lastMouseEventTick, Stopwatch.GetTimestamp());

        // P8 fail-open swap window: while a re-install is in flight for THIS hook, pass everything
        // through with zero side effects (see _mouseReplacementInProgress declaration).
        if (_mouseReplacementInProgress)
        {
            return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        if (nCode < 0 || _runtime.IsDisposed || !_runtime.IsRunning)
        {
            return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        var message = (int)wParam;

        // P5: moves (up to 8 kHz on gaming mice) and wheel exit here, BEFORE lParam is ever touched —
        // only these 8 button messages matter to us, and Marshal.PtrToStructure<T> boxes on .NET 8.
        if (message is not (NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP or
                             NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP or
                             NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP or
                             NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP))
        {
            return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        // Unsafe by-value read instead of Marshal.PtrToStructure<T> (see KeyboardCallback). Unsafe
        // code confined to this one read.
        NativeMethods.MSLLHOOKSTRUCT data;
        unsafe
        {
            data = *(NativeMethods.MSLLHOOKSTRUCT*)lParam;
        }

        // Ignore injected events
        if ((data.flags & NativeMethods.MouseLlFlags.LLMHF_INJECTED) != 0)
        {
            return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        return DispatchDecodedMouseEvent(message, data.mouseData)
            ? (IntPtr)1
            : NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    // Hook-thread-only dispatcher; see DispatchDecodedKeyboardEvent for callback ownership rules.
    internal bool DispatchDecodedMouseEvent(int message, uint mouseData)
    {
        // Track right button state (lock-free). Keep _rightButtonPressed = true HERE — CombinedMappings'
        // RightClickOnly gate reads it — but decide hold-breath AFTER HandleAltMouse (H6).
        if (message == NativeMethods.WM_RBUTTONDOWN)
        {
            _rightButtonPressed = true;

            // Crosshair hide-while-RMB-held: observation only, gated so the disabled case costs one
            // volatile read. The button itself is never suppressed (falls through below).
            if (_crosshairRightButtonWatch)
            {
                RightButtonStateChanged?.Invoke(this, true);
            }
        }
        else if (message == NativeMethods.WM_RBUTTONUP)
        {
            _rightButtonPressed = false;
            if (_crosshairRightButtonWatch)
            {
                RightButtonStateChanged?.Invoke(this, false);
            }
            _remaps.OnRightButtonReleased();
        }

        var handled = _gestures.HandlePanicMouse(message, mouseData, _rightButtonPressed) ||
                      _gestures.HandleAltMouse(message, mouseData);

        // Rapid Fire never consumes the physical click. Existing mouse actions win priority: an Alt+Left
        // binding or panic action may consume DOWN, in which case Rapid Fire only records the held state.
        if (message == NativeMethods.WM_LBUTTONDOWN)
        {
            _rapidFire.HandleLeftButton(isDown: true, allowStart: !handled);
        }
        else if (message == NativeMethods.WM_LBUTTONUP)
        {
            _rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        }

        // H6: only arm hold-breath for a genuine right-click, not one suppressed as an Alt+Right binding.
        if (message == NativeMethods.WM_RBUTTONDOWN)
        {
            if (!handled)
            {
                _gestures.HandleRightButtonDown(_rightButtonPressed);
            }
        }
        else if (message == NativeMethods.WM_RBUTTONUP)
        {
            _gestures.HandleRightButtonUp();
        }

        return handled;
    }

    public RapidFireArmStatus GetRapidFireArmStatus() => _rapidFire.GetStatus();

    public void ReleaseForegroundAutoRun() => _autoRun.Release(includeBackground: false);

    public void ReleaseForegroundState()
    {
        lock (_profileLock)
        {
            if (!_runtime.IsRunning)
            {
                return;
            }

            ReleaseAllState(preserveRapidFireArm: true);
            RederivePhysicalModifierState();
        }
    }

    public void SetForegroundIdentity(
        IntPtr windowHandle,
        uint processId,
        string? normalizedExecutable,
        long foregroundGeneration)
    {
        var generationChanged = _runtime.PublishedForegroundGeneration != foregroundGeneration;
        _runtime.SetForegroundIdentity(
            windowHandle,
            processId,
            normalizedExecutable,
            foregroundGeneration);
        if (generationChanged)
        {
            RaiseRapidFireArmChanged();
        }
    }

    private bool ReleaseAllState(
        bool preservePhysicalPairing = true,
        bool preserveRapidFireArm = false)
    {
        var rapidFireArmCleared = preserveRapidFireArm
            ? CancelRapidFirePressAndKeepArm()
            : _rapidFire.Release(preservePhysicalPairing);

        _remaps.ReleaseCombinedState(preservePhysicalPairing);
        _gestures.ReleaseGestures(preservePhysicalPairing);
        _remaps.ReleaseCapsStateOnly(preservePhysicalPairing);
        _gestures.ReleaseHoldBreath();
        _gestures.ReleasePanic(preservePhysicalPairing);
        _autoRun.Release(includeBackground: false);
        if (!preservePhysicalPairing)
        {
            _autoRun.ClearTriggerLatches();
            _remaps.ClearLauncherState();
        }

        _rightButtonPressed = false;
        LogDebug("All state released");
        return rapidFireArmCleared;
    }

    private bool CancelRapidFirePressAndKeepArm()
    {
        _rapidFire.CancelPress();
        return false;
    }

    private void SeedRapidFirePhysicalLeftDown()
    {
        var physicalLeftVk = NativeMethods.GetSystemMetrics(NativeMethods.SM_SWAPBUTTON) != 0
            ? NativeMethods.VK_RBUTTON
            : NativeMethods.VK_LBUTTON;
        _rapidFire.SeedPhysicalLeftButton(
            (NativeMethods.GetAsyncKeyState(physicalLeftVk) & 0x8000) != 0);
    }

    private void RederivePhysicalModifierState()
    {
        _gestures.SeedAltPressed(IsPhysicalKeyDown(0xA4) || IsPhysicalKeyDown(0xA5));
        SchedulePanicDerivation(() =>
        {
            _gestures.RederiveAltKeyboardPhysicalState(IsPhysicalKeyDown);
            _gestures.RederivePanicTriggerPhysicalState(IsPhysicalKeyDown);
        });
        var physicalRightVk = NativeMethods.GetSystemMetrics(NativeMethods.SM_SWAPBUTTON) != 0
            ? NativeMethods.VK_LBUTTON
            : NativeMethods.VK_RBUTTON;
        _rightButtonPressed = (NativeMethods.GetAsyncKeyState(physicalRightVk) & 0x8000) != 0;
        if (_crosshairRightButtonWatch)
        {
            RightButtonStateChanged?.Invoke(this, _rightButtonPressed);
        }
    }

    private void SchedulePanicDerivation(Action derive)
    {
        var dispatcher = _hookDispatcher;
        if (dispatcher is null)
        {
            derive();
            return;
        }

        // GetAsyncKeyState can lag a DOWN just observed by the low-level hook. Queue the read on
        // the hook dispatcher and fence panic handling until that serialized baseline lands.
        var ticket = _gestures.BeginPanicDerivation();
        try
        {
            var operation = dispatcher.InvokeAsync(() =>
            {
                try
                {
                    derive();
                }
                finally
                {
                    _gestures.RetirePanicDerivation(ticket);
                }
            });

            // A shutdown-aborted operation never invokes its delegate, so it must retire here.
            operation.Aborted += (_, _) => _gestures.RetirePanicDerivation(ticket);
            if (operation.Status == System.Windows.Threading.DispatcherOperationStatus.Aborted)
            {
                _gestures.RetirePanicDerivation(ticket);
            }
        }
        catch
        {
            _gestures.RetirePanicDerivation(ticket);
        }
    }

    private void RaiseRapidFireArmChanged() => RapidFireArmChanged?.Invoke(this, EventArgs.Empty);

    // ==================== LOGGING ====================
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LogDebug(string message)
    {
        _logger.Log(message);
    }
}
