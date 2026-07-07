using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.Services;

/// <summary>
/// High-performance input hook service optimized for gaming applications.
/// Uses lock-free algorithms and pre-allocated timers for sub-millisecond latency.
/// </summary>
public sealed class InputHookService : IInputHookService
{
    private readonly ILoggerService _logger;
    private bool IsDebugEnabled => _logger.IsEnabled;

    // ==================== TIMING CONFIGURATION ====================

    // Key Press Duration (human-like variance)
    private const int KEY_PRESS_DURATION_MIN_MS = 31;
    private const int KEY_PRESS_DURATION_MAX_MS = 53;
    
    // Hold Breath Activation Jitter
    private const int HOLD_BREATH_JITTER_MIN_MS = 15;
    private const int HOLD_BREATH_JITTER_MAX_MS = 36;

    // Hold Breath Toggle-mode tap duration (P6: hoisted out of the old inline worker)
    private const int HOLD_BREATH_TAP_DURATION_MIN_MS = 20;
    private const int HOLD_BREATH_TAP_DURATION_MAX_MS = 30;

    // RNG Warmup (breaks thread-reuse patterns for anti-cheat)
    private const int RNG_WARMUP_MIN_CALLS = 1;
    private const int RNG_WARMUP_MAX_CALLS = 5;
    
    // Lock-free state constants
    private const int TIMER_IDLE = 0;
    private const int TIMER_ARMED = 1;
    private const int TIMER_FIRED = 2;
    private const int TIMER_CANCELLED = 3;

    // ==================== FIELDS ====================
    
    // Primary synchronization (only for profile changes, not hot path)
    private readonly object _profileLock = new();
    
    // Thread-safe random number generator (collision-resistant seeding)
    private readonly ThreadLocal<Random> _random = new(() =>
    {
        // Hybrid seed: nanosecond timestamp XOR thread ID
        // Prevents collisions even with thousands of simultaneous thread spawns
        var timestamp = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        var seed = unchecked((int)(timestamp ^ (threadId << 16)));
        return new Random(seed);
    });
    
    // Mouse button state tracking (pre-allocated, zero-allocation hot path)
    private readonly Dictionary<Models.MouseButton, MouseButtonState> _mouseStates = new()
    {
        { Models.MouseButton.Left, new MouseButtonState() },
        { Models.MouseButton.Right, new MouseButtonState() },
        { Models.MouseButton.Middle, new MouseButtonState() },
        { Models.MouseButton.XButton1, new MouseButtonState() },
        { Models.MouseButton.XButton2, new MouseButtonState() }
    };
    
    private readonly object _combinedOverridesLock = new();
    private readonly Dictionary<Key, CombinedOverrideState> _activeCombinedOverrides = new();
    // Maintained under _combinedOverridesLock on every add/remove; lets the H2 key-up release skip the
    // lock entirely when no overrides are active (the common case on every key-up).
    private volatile int _activeCombinedOverrideCount;
    
    // Profile state
    private volatile Profile? _activeProfile;
    private volatile Profile? _windowsProfile;
    
    // Runtime flags (volatile for lock-free reads)
    private volatile bool _isRunning;
    private volatile bool _disposed;
    private volatile bool _altPressed;
    private volatile bool _rightButtonPressed;
    
    // CapsLock state. Guarded by _capsLockStateLock: the hook thread engages/remaps while
    // ReleaseCapsState runs on the activation worker or SystemEvents thread, and the injected DOWN
    // must never be reorderable with its recorded release (same proven pattern as _holdBreathLock;
    // caps events occur at human frequency, so the lock costs nothing on the hot path).
    private readonly object _capsLockStateLock = new();
    private bool _capsShiftEngaged;
    private Key? _capsRemappedKey;

    // Per-key "already launched while held" latch. Prevents typematic auto-repeat from spawning a
    // launcher process on every repeated WM_KEYDOWN. Guarded by its own lock because ReleaseAllState()
    // (Clear) runs on the activation-worker POOL thread while the keyboard hook thread does Add/Remove.
    private readonly HashSet<Key> _heldLauncherKeys = new();
    private readonly object _heldLauncherKeysLock = new();

    // Keys mid-flight inside an untracked tap (FireTapKey / hold-breath toggle): DOWN sent, UP pending
    // on a pool thread ~20-55ms later. Tracked so ReleaseAllState (profile switch, session switch,
    // Stop) can force the UP — otherwise a shutdown landing inside that window would leave the key
    // stuck system-wide with the hooks already gone. List, not set: two overlapping taps of the same
    // key keep one entry per pending UP.
    // LOCK ORDER (I5, P6): may be taken while _holdBreathLock is already held (the Toggle-mode tap
    // calls FireTapKey from inside ActivateHoldBreathLocked) — one-way nesting _holdBreathLock ->
    // _transientTapLock ONLY. Never acquire _holdBreathLock while already holding this lock.
    private readonly object _transientTapLock = new();
    private readonly List<Key> _transientTapKeys = new();

    // Bumped by ReleaseAllState under _transientTapLock. A tap worker captures the epoch when it is
    // QUEUED and bails if it changed by the time it runs: a tap queued before a profile/session
    // release boundary must not inject after it (the drain above only covers taps already mid-flight).
    private volatile int _tapReleaseEpoch;

    // Hold-breath state. All fields below are guarded by _holdBreathLock. Every hold-breath event
    // (arm, fire, cancel, release) already paid this lock for Timer.Change, and events occur at
    // human click frequency — so guarding the whole state machine including the SendInput calls
    // costs nothing extra while guaranteeing the UP release can never overtake the DOWN press.
    // LOCK ORDER (I5, P6): the Toggle-mode tap path calls FireTapKey (which takes _transientTapLock)
    // while this lock is held — one-way nesting _holdBreathLock -> _transientTapLock ONLY. Safe
    // because ReleaseAllState takes the two locks sequentially, never nested, and the tap worker
    // only ever takes _transientTapLock alone. Never acquire this lock while holding _transientTapLock.
    private readonly object _holdBreathLock = new();
    private readonly System.Threading.Timer _holdBreathTimer;
    private bool _holdBreathPending;
    private Key? _holdBreathInjectedKey;    // key sent DOWN in Hold mode and not yet released
    private Key _holdBreathArmedKey;        // settings snapshot at arm time (UI mutates settings in place)
    private HoldBreathMode _holdBreathArmedMode;
    private long _holdBreathArmedTick;      // arm timestamp for the stale-fire guard
    private int _holdBreathArmedDelayMs;
    
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

    // P8 watchdog thresholds/period.
    private const int WATCHDOG_PERIOD_MS = 10_000;
    private const double WATCHDOG_STALE_HOOK_THRESHOLD_MS = 30_000;
    private const uint WATCHDOG_FRESH_INPUT_THRESHOLD_MS = 2_000;

    // Performance metrics
    private static readonly double TickToMilliseconds = 1000.0 / Stopwatch.Frequency;

    // Cached once: Marshal.SizeOf<T>() is not free, and every SendInput call needs it.
    private static readonly int InputStructSize = Marshal.SizeOf<NativeMethods.INPUT>();

    // Tolerance for the hold-timer elapsed-time guard. Windows timers fire on-time or late, never
    // meaningfully early, so a callback firing more than this many ms early is a stale queued elapse.
    private const double HOLD_FIRE_TOLERANCE_MS = 2.0;

    public InputHookService(ILoggerService logger)
    {
        _logger = logger;

        // Initialize hold breath timer (pre-allocated, reused throughout lifetime)
        _holdBreathTimer = new System.Threading.Timer(_ => OnHoldBreathTimerFired(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler<Profile?>? ActiveProfileChanged;

    // ==================== LIFECYCLE ====================
    
    public void Start()
    {
        lock (_profileLock)
        {
            if (_isRunning)
            {
                return;
            }

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

                // P8: hook-loss watchdog. 10s period is coarse on purpose — this only needs to catch
                // the rare silent hook removal (UI stall > LowLevelHooksTimeout), not run hot.
                _hookWatchdogTimer = new System.Threading.Timer(_ => WatchdogTick(), null, WATCHDOG_PERIOD_MS, WATCHDOG_PERIOD_MS);
            }
            catch
            {
                // Full rollback before rethrow: _isRunning is still false here, so Stop() will never
                // run to unhook — and a retried Start() would otherwise stack a second pair of LL
                // hooks on top of these. Mirrors the hook-install-failure branch above.
                _hookWatchdogTimer?.Dispose();
                _hookWatchdogTimer = null;

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

            _isRunning = true;
            LogDebug("InputHookService started");
        }
    }

    public void Stop()
    {
        lock (_profileLock)
        {
            if (!_isRunning)
            {
                return;
            }

            _hookWatchdogTimer?.Dispose();
            _hookWatchdogTimer = null;

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
            // passed its entry check re-validates _isRunning under the subsystem locks, so it can no
            // longer inject AFTER ReleaseAllState ran — with the hooks gone, nothing would ever
            // release such a key and it would stay stuck system-wide beyond process exit.
            _isRunning = false;

            ReleaseAllState();

            // P7 pairing: winmm requires matched Begin/End calls. Stop() is already idempotent via
            // the _isRunning guard above, so this fires exactly once per successful Start().
            if (_timerResolutionRaised)
            {
                NativeMethods.timeEndPeriod(1);
                _timerResolutionRaised = false;
            }

            LogDebug("InputHookService stopped");
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        // When the desktop switches away mid-press, the low-level hook never sees the button-up,
        // which would leave injected keys (hold-breath, combined overrides) stuck down.
        if (e.Reason is not (SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff or
                             SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect))
        {
            return;
        }

        lock (_profileLock)
        {
            if (!_isRunning)
            {
                return;
            }

            ReleaseAllState();
            LogDebug($"Session switch ({e.Reason}): released all injected state");
        }
    }

    // ==================== HOOK-LOSS WATCHDOG (P8) ====================

    // Windows (Win7+) silently removes an LL hook whose callback exceeds LowLevelHooksTimeout (HKCU,
    // ~300ms class default, hard-capped 1000ms since Win10 1709) — no notification, no recovery
    // without this. Detection runs on the timer thread (cheap, lock-free reads); the actual
    // re-install is marshaled onto _hookDispatcher and takes _profileLock, same as every other
    // lifecycle mutation.
    private void WatchdogTick()
    {
        if (!_isRunning)
        {
            return;
        }

        if (!TryGetHookFreshness(out var systemInputAgeMs, out var keyboardIdleMs, out var mouseIdleMs))
        {
            return; // best-effort; try again next period
        }

        var reinstallKeyboard = ShouldReinstallHook(keyboardIdleMs, systemInputAgeMs, WATCHDOG_STALE_HOOK_THRESHOLD_MS, WATCHDOG_FRESH_INPUT_THRESHOLD_MS);
        var reinstallMouse = ShouldReinstallHook(mouseIdleMs, systemInputAgeMs, WATCHDOG_STALE_HOOK_THRESHOLD_MS, WATCHDOG_FRESH_INPUT_THRESHOLD_MS);

        if (!reinstallKeyboard && !reinstallMouse)
        {
            return;
        }

        LogDebug($"Watchdog: possible silent hook loss (keyboard={reinstallKeyboard}, mouse={reinstallMouse}, systemInputAge={systemInputAgeMs}ms)");

        if (!_canReinstallHooks || _hookDispatcher is null)
        {
            LogDebug("Watchdog: re-install is disabled (hooks were not installed on a dispatcher-pumped thread) — detection only");
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
                    if (!_isRunning)
                    {
                        return;
                    }

                    // This closure may have sat queued on a stalled dispatcher — exactly the scenario
                    // the pending-guard exists for — so the booleans captured above can be stale by
                    // now. Recompute freshness and only reinstall hooks that are STILL stale.
                    if (!TryGetHookFreshness(out var freshSystemInputAgeMs, out var freshKeyboardIdleMs, out var freshMouseIdleMs))
                    {
                        return; // best-effort; the periodic tick will retry
                    }

                    if (ShouldReinstallHook(freshKeyboardIdleMs, freshSystemInputAgeMs, WATCHDOG_STALE_HOOK_THRESHOLD_MS, WATCHDOG_FRESH_INPUT_THRESHOLD_MS))
                    {
                        ReinstallKeyboardHookLocked();
                    }

                    if (ShouldReinstallHook(freshMouseIdleMs, freshSystemInputAgeMs, WATCHDOG_STALE_HOOK_THRESHOLD_MS, WATCHDOG_FRESH_INPUT_THRESHOLD_MS))
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

    // Shared by WatchdogTick's preliminary check and its dispatcher-marshaled recheck: system-wide
    // input age (GetLastInputInfo) plus each hook's idle time from its liveness tick. Returns false
    // if GetLastInputInfo fails (best-effort; caller retries next period).
    private bool TryGetHookFreshness(out uint systemInputAgeMs, out double keyboardIdleMs, out double mouseIdleMs)
    {
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
        return true;
    }

    // Pure decision function (P8, unit-tested): a hook is presumed silently removed when the system
    // is receiving fresh input but that specific hook has not seen an event in a long time. If system
    // input is ALSO stale (e.g. lock screen), "hook died" is indistinguishable from "nobody is
    // providing input" — don't reinstall.
    internal static bool ShouldReinstallHook(double hookIdleMs, uint systemInputAgeMs, double staleHookThresholdMs, uint freshInputThresholdMs)
    {
        return systemInputAgeMs < freshInputThresholdMs && hookIdleMs > staleHookThresholdMs;
    }

    // Must run on _hookDispatcher, under _profileLock, with _isRunning already re-checked by the
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

        // _isRunning re-checked by the caller guarantees _keyboardProc was assigned in Start() and
        // never reset (only the Start()-failure path nulls it, which never sets _isRunning true).
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
        ReleaseAllState();
        RederivePhysicalModifierState();

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
        ReleaseAllState();
        RederivePhysicalModifierState();

        Volatile.Write(ref _lastMouseEventTick, Stopwatch.GetTimestamp());
        _mouseReplacementInProgress = false;
    }

    public void ActivateProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (_profileLock)
        {
            if (!_isRunning)
            {
                return;
            }

            if (ReferenceEquals(_activeProfile, profile))
            {
                return;
            }

            ReleaseAllState();
            RederivePhysicalModifierState();
            _activeProfile = profile;

            LogDebug($"Profile activated: {profile.Name}");
        }

        ActiveProfileChanged?.Invoke(this, profile);
    }

    public void DeactivateProfile()
    {
        Profile? previous;

        lock (_profileLock)
        {
            previous = _activeProfile;
            if (previous is null)
            {
                return;
            }

            ReleaseAllState();
            RederivePhysicalModifierState();
            _activeProfile = null;

            LogDebug("Profile deactivated");
        }

        ActiveProfileChanged?.Invoke(this, null);
    }

    public void SetWindowsProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        
        lock (_profileLock)
        {
            _windowsProfile = profile;
            LogDebug($"Windows profile set: {profile.Name}");
        }
    }

    public void Dispose()
    {
        // Set first so any pool work item racing shutdown becomes a no-op instead of touching
        // torn-down state. ReleaseAllState() (via Stop) releases held keys before we get here.
        _disposed = true;
        Stop();
        // Deliberately do NOT dispose _random: queued FireTapKey/hold-breath work items may still
        // deref _random.Value on a pool thread; ThreadLocal<Random> holds no unmanaged resources.
        _holdBreathTimer.Dispose();
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

        if (nCode < 0 || !_isRunning)
        {
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        var message = (int)wParam;

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

        // Track Alt key state (lock-free)
        if (vkCode is 0xA4 or 0xA5 or 0x12)  // VK_LMENU, VK_RMENU, VK_MENU
        {
            if (isKeyDown)
            {
                _altPressed = true;
            }
            else if (isKeyUp)
            {
                // Releasing one Alt must not clear the flag while the OTHER Alt is still held.
                // Query only the sibling key: from inside an LL hook the released key itself may
                // not yet be reflected in the async key state.
                _altPressed = vkCode switch
                {
                    0xA4 => (NativeMethods.GetAsyncKeyState(0xA5) & 0x8000) != 0,
                    0xA5 => (NativeMethods.GetAsyncKeyState(0xA4) & 0x8000) != 0,
                    _ => false // generic VK_MENU up: LL hooks deliver L/R codes, treat as full release
                };
            }
        }

        // Handle features in priority order
        var handled = HandleCapsLock(vkCode, isKeyDown, isKeyUp) ||
                      HandleCombinedMappings(vkCode, isKeyDown, isKeyUp);

        if (!handled)
        {
            handled = HandleWindowsLauncher(vkCode, isKeyDown, isKeyUp);
        }

        return handled 
            ? (IntPtr)1 
            : NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

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

        if (nCode < 0 || !_isRunning)
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

        // Track right button state (lock-free). Keep _rightButtonPressed = true HERE — CombinedMappings'
        // RightClickOnly gate reads it — but decide hold-breath AFTER HandleAltMouse (H6).
        if (message == NativeMethods.WM_RBUTTONDOWN)
        {
            _rightButtonPressed = true;
        }
        else if (message == NativeMethods.WM_RBUTTONUP)
        {
            _rightButtonPressed = false;
            ReleaseRightClickOverrides();
        }

        var handled = HandleAltMouse(message, data.mouseData);

        // H6: only arm hold-breath for a genuine right-click, not one suppressed as an Alt+Right binding.
        if (message == NativeMethods.WM_RBUTTONDOWN)
        {
            if (!handled)
            {
                HandleRightClickHoldBreathDown();
            }
        }
        else if (message == NativeMethods.WM_RBUTTONUP)
        {
            HandleRightClickHoldBreathUp();
        }

        return handled
            ? (IntPtr)1
            : NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    // ==================== ALT+MOUSE HANDLING (LOCK-FREE) ====================
    
    private bool HandleAltMouse(int message, uint mouseData)
    {
        var profile = _activeProfile;
        if (profile is null || !profile.AltMouse.IsEnabled)
        {
            return false;
        }

        var button = message switch
        {
            NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP => Models.MouseButton.Left,
            NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP => Models.MouseButton.Right,
            NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP => Models.MouseButton.Middle,
            NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP => GetXButton(mouseData),
            _ => (Models.MouseButton?)null
        };

        if (!button.HasValue || !profile.AltMouse.Bindings.TryGetValue(button.Value, out var binding))
        {
            return false;
        }

        var state = _mouseStates[button.Value];
        var isUp = message is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_RBUTTONUP or 
                              NativeMethods.WM_MBUTTONUP or NativeMethods.WM_XBUTTONUP;

        if (!_altPressed)
        {
            // If Alt is released, we normally don't handle the event.
            // However, if we have stale state (DownTick) from a previous "Alt+Down" that wasn't completed,
            // we must clear it now to prevent it from interfering with future clicks.
            if (isUp && Interlocked.Exchange(ref state.DownTick, 0L) != 0L)
            {
                LogDebug($"[{button}] Stale state cleared (Alt released)");
                CancelHoldTimer(state);
                Interlocked.Exchange(ref state.TimerState, TIMER_IDLE);
            }
            return false;
        }

        if (binding is null || (!binding.TapKey.HasValue && !binding.HoldKey.HasValue))
        {
            return false;
        }

        var isDown = message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or 
                                NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN;

        if (isDown)
        {
            return HandleMouseDown(button.Value, state, binding, profile);
        }

        if (isUp)
        {
            return HandleMouseUp(button.Value, state, binding, profile);
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HandleMouseDown(Models.MouseButton button, MouseButtonState state,
        MouseButtonBinding binding, Profile profile)
    {
        // Cancel any pending timer
        CancelHoldTimer(state);

        // Record timestamp (Interlocked — ResetMouseStates clears it from another thread)
        var downTick = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref state.DownTick, downTick);

        // Atomically arm the state machine
        Interlocked.Exchange(ref state.TimerState, TIMER_ARMED);

        if (IsDebugEnabled)
        {
            LogDebug($"[{button}] DOWN - Tap={binding.TapKey}, Hold={binding.HoldKey}, " +
                     $"Threshold={profile.AltMouse.HoldThresholdMilliseconds}ms");
        }

        // Schedule hold timer if configured
        if (binding.HoldKey.HasValue)
        {
            var holdKey = binding.HoldKey.Value;
            // Deterministic threshold (no jitter)
            var holdThreshold = Math.Max(10, profile.AltMouse.HoldThresholdMilliseconds);
            if (IsDebugEnabled) LogDebug($"[{button}] Hold timer: {holdThreshold}ms");

            // Only capture the state reference (not runtime flags). Capture the down-tick as a local
            // long so the callback can measure the real elapsed time of THIS press.
            var stateRef = state;
            var downTickAtArm = downTick;

            // Assign the callback BEFORE arming the timer: the shared timer root re-reads the HoldCallback
            // FIELD at fire time, so a stale elapse from a previous press would otherwise run the newest
            // closure. The elapsed-time guard below is what actually rejects that stale firing.
            state.HoldCallback = _ =>
            {
                // ✅ Check CURRENT runtime state via volatile fields (read at execution, not scheduling).
                if (!_isRunning)
                {
                    if (IsDebugEnabled) LogDebug($"[{button}] Hold timer blocked - service stopped");
                    return;
                }

                if (!_altPressed)
                {
                    if (IsDebugEnabled) LogDebug($"[{button}] Hold timer blocked - Alt released");
                    return;
                }

                // ✅ H3: reject a stale/premature firing. An elapse queued by a PREVIOUS press carries an
                // earlier down-tick; if the real elapsed time is below threshold this is not a genuine hold,
                // so no-op WITHOUT flipping to FIRED (otherwise the current quick tap would be suppressed).
                var elapsedMs = (Stopwatch.GetTimestamp() - downTickAtArm) * TickToMilliseconds;
                if (elapsedMs < holdThreshold - HOLD_FIRE_TOLERANCE_MS)
                {
                    if (IsDebugEnabled) LogDebug($"[{button}] Hold timer rejected - stale/premature ({elapsedMs:0}ms < {holdThreshold}ms)");
                    return;
                }

                // ✅ Atomic state check: only fire if still ARMED
                if (Interlocked.CompareExchange(ref stateRef.TimerState, TIMER_FIRED, TIMER_ARMED) != TIMER_ARMED)
                {
                    if (IsDebugEnabled) LogDebug($"[{button}] Hold timer blocked - state changed (cancelled or already fired)");
                    return;
                }

                if (IsDebugEnabled) LogDebug($"[{button}] Hold timer FIRED - sending {holdKey}");
                FireTapKey(holdKey, KEY_PRESS_DURATION_MIN_MS, KEY_PRESS_DURATION_MAX_MS);
            };

            state.HoldTimer.Change(holdThreshold, Timeout.Infinite); // arm AFTER assigning the callback
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool HandleMouseUp(Models.MouseButton button, MouseButtonState state,
        MouseButtonBinding binding, Profile profile)
    {
        // If we didn't track the down event, we shouldn't suppress the up event.
        // This happens if Alt was pressed AFTER the mouse button was already down.
        // Exchange atomically claims the down: ResetMouseStates (activation worker) can otherwise
        // clear the field between a has-value check and its read, which would throw inside the
        // mouse hook callback and crash the process.
        var downTick = Interlocked.Exchange(ref state.DownTick, 0L);
        if (downTick == 0L)
        {
            return false;
        }

        // Calculate hold duration
        var elapsedMs = (Stopwatch.GetTimestamp() - downTick) * TickToMilliseconds;

        var threshold = profile.AltMouse.HoldThresholdMilliseconds;

        // Atomically read and reset state: * → IDLE (read current state BEFORE cancelling timer)
        var finalState = Interlocked.Exchange(ref state.TimerState, TIMER_IDLE);

        // Cancel timer after reading state (prevents overwriting TIMER_FIRED)
        state.HoldTimer.Change(Timeout.Infinite, Timeout.Infinite);

        if (IsDebugEnabled) LogDebug($"[{button}] UP - Elapsed={elapsedMs:F1}ms, Threshold={threshold}ms, State={finalState}");

        if (finalState == TIMER_FIRED)
        {
            // Timer already sent the hold key - don't send again
            if (IsDebugEnabled) LogDebug($"[{button}] Hold was triggered by timer (not re-triggering)");
        }
        else if (binding.HoldKey.HasValue && elapsedMs >= threshold)
        {
            // We beat the timer, but threshold was met - send hold key
            if (IsDebugEnabled) LogDebug($"[{button}] Hold threshold met manually");
            FireTapKey(binding.HoldKey.Value, KEY_PRESS_DURATION_MIN_MS, KEY_PRESS_DURATION_MAX_MS);
        }
        else if (binding.TapKey.HasValue)
        {
            // Quick tap - send tap key
            if (IsDebugEnabled) LogDebug($"[{button}] Quick tap");
            FireTapKey(binding.TapKey.Value, KEY_PRESS_DURATION_MIN_MS, KEY_PRESS_DURATION_MAX_MS);
        }

        // Consume the release
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Models.MouseButton GetXButton(uint mouseData)
    {
        var xButton = (mouseData >> 16) & 0xFFFF;
        return xButton switch
        {
            1 => Models.MouseButton.XButton1,
            2 => Models.MouseButton.XButton2,
            _ => Models.MouseButton.XButton1
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CancelHoldTimer(MouseButtonState state)
    {
        // Atomically mark as cancelled
        Interlocked.Exchange(ref state.TimerState, TIMER_CANCELLED);

        // Disable timer (non-blocking)
        state.HoldTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    // ==================== COMBINED KEY MAPPINGS ====================
    
    private bool HandleCombinedMappings(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        var sourceKey = KeyInteropUtilities.FromVirtualKey(vkCode);

        // H2: release by held source key at the TOP, before any enable/entry guard. If the mapping was
        // disabled or the row deleted while its source key was held, this still removes the override and
        // releases the injected target key so it can't stick system-wide. The count fast-path skips the
        // lock entirely when no overrides are active (relies on the single-threaded keyboard hook, §2).
        if (isKeyUp && sourceKey is not null && _activeCombinedOverrideCount > 0)
        {
            CombinedOverrideState? held;
            lock (_combinedOverridesLock)
            {
                _activeCombinedOverrides.Remove(sourceKey.Value, out held);
                _activeCombinedOverrideCount = _activeCombinedOverrides.Count;
            }

            if (held is not null)
            {
                SendKey(held.TargetKey, false);
                if (IsDebugEnabled) LogDebug($"Combined mapping released: {sourceKey.Value}");
                return held.SuppressOriginal;
            }
        }

        var profile = _activeProfile;
        if (profile is null || !profile.CombinedMappings.IsEnabled)
        {
            return false;
        }

        if (sourceKey is null)
        {
            return false;
        }

        // Optimization: Use manual loop instead of LINQ to avoid allocation on every key press
        CombinedMappingEntry? entry = null;
        foreach (var m in profile.CombinedMappings.Mappings)
        {
            if (m.SourceKey == sourceKey.Value)
            {
                entry = m;
                break;
            }
        }

        if (entry is null)
        {
            return false;
        }

        var suppressOriginal = entry.SuppressOriginalKey;
        var targetKey = entry.TargetKey;
        var requiresRightClick = entry.RightClickOnly;

        if (isKeyDown)
        {
            if (requiresRightClick && !_rightButtonPressed)
            {
                return false;
            }

            // Safety: do nothing if mapping is a no-op (source == target)
            if (targetKey == sourceKey.Value)
            {
                return false;
            }

            var newState = new CombinedOverrideState
            {
                TargetKey = targetKey,
                SuppressOriginal = suppressOriginal,
                RightClickOnly = requiresRightClick
            };

            lock (_combinedOverridesLock)
            {
                // Re-check under the lock: Stop() flips _isRunning before ReleaseAllOverrides, so an
                // in-flight callback can't add + inject a key that nothing would ever release.
                if (!_isRunning)
                {
                    return false;
                }

                if (_activeCombinedOverrides.ContainsKey(sourceKey.Value))
                {
                    return suppressOriginal;
                }

                _activeCombinedOverrides[sourceKey.Value] = newState;
                _activeCombinedOverrideCount = _activeCombinedOverrides.Count;
            }

            SendKey(targetKey, true);
            if (!IsCombinedOverrideActive(sourceKey.Value, newState))
            {
                SendKey(targetKey, false);
            }

            if (IsDebugEnabled) LogDebug($"Combined mapping: {sourceKey.Value} → {targetKey} (suppress={suppressOriginal})");

            return suppressOriginal;
        }

        // Key-up release is handled at the top of this method (H2), before the enable/entry guards.
        return false;
    }

    

        private bool HandleCapsLock(int vkCode, bool isKeyDown, bool isKeyUp)
        {
        // Only handle CapsLock key events (VK_CAPITAL = 0x14)
        if (vkCode != 0x14)
        {
            return false;
        }

        var settings = GetEffectiveCapsLockSettings();
        if (settings is not { IsEnabled: true })
        {
            return false;
        }

        switch (settings.Mode)
        {
            case CapsLockMode.Normal:
                return false;

            case CapsLockMode.Disabled:
                LogDebug("CapsLock suppressed (Disabled mode)");
                return true;

            case CapsLockMode.Hold:
                lock (_capsLockStateLock)
                {
                    // _isRunning re-check under the lock: an in-flight callback racing Stop() must
                    // not toggle CapsLock after ReleaseCapsState already ran.
                    if (isKeyDown && !_capsShiftEngaged && _isRunning)
                    {
                        _capsShiftEngaged = true;
                        ForceCapsLockState(true);
                        LogDebug("CapsLock → FORCED ON (Hold mode)");
                    }
                    else if (isKeyUp && _capsShiftEngaged)
                    {
                        _capsShiftEngaged = false;
                        ForceCapsLockState(false);
                        LogDebug("CapsLock → FORCED OFF (Hold mode)");
                    }
                }
                return true;

            case CapsLockMode.Remap:
                var target = settings.RemapTarget;
                if (!target.HasValue)
                {
                    return true;
                }

                lock (_capsLockStateLock)
                {
                    if (isKeyDown)
                    {
                        // A repeat after the remap target changed would overwrite the recorded key
                        // and orphan the previously injected one — release it before injecting anew.
                        if (_capsRemappedKey is { } previous && previous != target.Value)
                        {
                            SendKey(previous, false);
                            _capsRemappedKey = null;
                            LogDebug($"CapsLock remap retarget: released {previous}");
                        }

                        // _isRunning re-check under the lock (see Hold mode above): never inject a
                        // DOWN that a completed Stop() can no longer pair with a release.
                        if (_isRunning)
                        {
                            _capsRemappedKey = target;
                            SendKey(target.Value, true);
                            LogDebug($"CapsLock → {target.Value} DOWN (Remap mode)");
                        }
                    }
                    else if (isKeyUp && _capsRemappedKey.HasValue)
                    {
                        SendKey(_capsRemappedKey.Value, false);
                        LogDebug($"CapsLock → {_capsRemappedKey.Value} UP (Remap mode)");
                        _capsRemappedKey = null;
                    }
                }
                return true;

            default:
                return false;
        }
    }

    private CapsLockSettings? GetEffectiveCapsLockSettings()
    {
        var active = _activeProfile?.CapsLock;
        if (active is { IsEnabled: true } enabledActive && enabledActive.Mode != CapsLockMode.Normal)
        {
            return enabledActive;
        }

        var global = _windowsProfile?.CapsLock;
        if (global is { IsEnabled: true } enabledGlobal && enabledGlobal.Mode != CapsLockMode.Normal)
        {
            return enabledGlobal;
        }

        if (active?.IsEnabled == true)
        {
            return active;
        }

        if (global?.IsEnabled == true)
        {
            return global;
        }

        return null;
    }

    private void ReleaseCapsState()
    {
        lock (_capsLockStateLock)
        {
            if (_capsShiftEngaged)
            {
                _capsShiftEngaged = false;

                // Check if we're in Hold mode
                var settings = GetEffectiveCapsLockSettings();
                if (settings is { IsEnabled: true, Mode: CapsLockMode.Hold })
                {
                    ForceCapsLockState(false);
                    LogDebug("Force-release CapsLock (Hold → OFF)");
                }
                else
                {
                    // Legacy Shift emulation path (if mode was changed during hold)
                    SendKey(Key.LeftShift, false);
                    LogDebug("Force-release CapsLock Shift");
                }
            }

            if (_capsRemappedKey.HasValue)
            {
                SendKey(_capsRemappedKey.Value, false);
                LogDebug($"Force-release CapsLock remap: {_capsRemappedKey.Value}");
                _capsRemappedKey = null;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCapsLockOn()
    {
        return (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ForceCapsLockState(bool enabled)
    {
        // Check current Caps Lock state
        bool currentlyOn = IsCapsLockOn();
        
        // Only toggle if state doesn't match desired state
        if (currentlyOn != enabled)
        {
            // Send Caps Lock key tap to toggle it (down + up). Using VK code directly for Caps
            // Lock (0x14). Batched as ONE SendInput(2, ...) call: an atomic down/up pair can no
            // longer be interleaved with other injected input from elsewhere in the process.
            var down = new NativeMethods.INPUT
            {
                type = NativeMethods.InputType.INPUT_KEYBOARD,
                U = new NativeMethods.InputUnion
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = NativeMethods.VK_CAPITAL,
                        wScan = (ushort)NativeMethods.MapVirtualKey(NativeMethods.VK_CAPITAL, 0),
                        dwFlags = 0,  // Key down
                        time = 0,
                        dwExtraInfo = NativeMethods.INPUT_IGNORE
                    }
                }
            };

            var up = down;
            up.U.ki.dwFlags = NativeMethods.KeyEventFlags.KEYEVENTF_KEYUP;

            NativeMethods.SendInput(2, new[] { down, up }, InputStructSize);

            LogDebug($"ForceCapsLockState: Toggled Caps Lock {(enabled ? "ON" : "OFF")}");
        }
    }

    // ==================== HOLD BREATH HANDLING ====================

    private void HandleRightClickHoldBreathDown()
    {
        var profile = _activeProfile;
        if (profile is null || !profile.RightClickHoldBreath.IsEnabled)
        {
            return;
        }

        var settings = profile.RightClickHoldBreath;
        var baseDelay = Math.Max(0, settings.DelayMilliseconds);

        // Add human-like jitter using thread-local RNG with warmup
        var rng = _random.Value!;

        // Warmup RNG to break thread-reuse patterns (anti-cheat protection)
        var warmupCalls = rng.Next(RNG_WARMUP_MIN_CALLS, RNG_WARMUP_MAX_CALLS + 1);
        for (int i = 0; i < warmupCalls; i++)
        {
            rng.Next();
        }

        var jitter = baseDelay > 0
            ? rng.Next(HOLD_BREATH_JITTER_MIN_MS, HOLD_BREATH_JITTER_MAX_MS + 1)
            : 0;
        var totalDelay = baseDelay + jitter;

        if (IsDebugEnabled) LogDebug($"HoldBreath DOWN: base={baseDelay}ms, jitter=+{jitter}ms, total={totalDelay}ms, warmup={warmupCalls}");

        lock (_holdBreathLock)
        {
            // A missed WM_RBUTTONUP (hook timeout, UAC secure desktop, Win+L) can leave the previous
            // press's key down; release it before starting a new cycle so it can never stay stuck.
            ReleaseInjectedHoldBreathKeyLocked();

            // After Dispose the shared timer is gone — touching it would throw inside the hook
            // callback. The orphan release above still ran; nothing new may be armed.
            if (_disposed)
            {
                _holdBreathPending = false;
                return;
            }

            _holdBreathPending = true;
            // Snapshot settings: the UI mutates the live settings object in place, and the release
            // must pair with exactly the key that was pressed.
            _holdBreathArmedKey = settings.HoldBreathKey;
            _holdBreathArmedMode = settings.Mode;

            if (totalDelay > 0)
            {
                _holdBreathArmedTick = Stopwatch.GetTimestamp();
                _holdBreathArmedDelayMs = totalDelay;
                _holdBreathTimer.Change(totalDelay, Timeout.Infinite);
            }
            else if (_isRunning && _rightButtonPressed)
            {
                // Immediate activation, synchronous on the hook thread
                ActivateHoldBreathLocked();
            }
        }
    }

    private void HandleRightClickHoldBreathUp()
    {
        // No profile/IsEnabled gate here: the release must pair with whatever was actually
        // injected, even if settings changed or the profile switched mid-hold.
        lock (_holdBreathLock)
        {
            _holdBreathPending = false;
            if (!_disposed)
            {
                _holdBreathTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            ReleaseInjectedHoldBreathKeyLocked();
        }
    }

    private void OnHoldBreathTimerFired()
    {
        lock (_holdBreathLock)
        {
            if (!_holdBreathPending || !_isRunning)
            {
                return;
            }

            // Timer.Change cannot recall an already-dispatched callback, so a cancel followed by an
            // immediate re-arm can be followed by the PREVIOUS press's elapse. Timers fire on-time
            // or late, never meaningfully early — an early fire is that stale elapse.
            var elapsedMs = (Stopwatch.GetTimestamp() - _holdBreathArmedTick) * TickToMilliseconds;
            if (elapsedMs < _holdBreathArmedDelayMs - HOLD_FIRE_TOLERANCE_MS)
            {
                if (IsDebugEnabled) LogDebug($"HoldBreath stale timer fire ignored: elapsed={elapsedMs:F1}ms of {_holdBreathArmedDelayMs}ms");
                return;
            }

            // Button already up (its WM_RBUTTONUP is in flight but hasn't reached our lock yet):
            // activating now would inject a pointless phantom tap.
            if (!_rightButtonPressed)
            {
                _holdBreathPending = false;
                return;
            }

            var profile = _activeProfile;
            if (profile?.RightClickHoldBreath.IsEnabled != true)
            {
                _holdBreathPending = false;
                return;
            }

            ActivateHoldBreathLocked();
        }
    }

    // Must be called while holding _holdBreathLock. Injecting the DOWN inside the lock is what
    // guarantees the UP handler's release can never be reordered before this press lands.
    private void ActivateHoldBreathLocked()
    {
        _holdBreathPending = false;

        if (IsDebugEnabled) LogDebug($"HoldBreath ACTIVATED: mode={_holdBreathArmedMode}, key={_holdBreathArmedKey}");

        if (_holdBreathArmedMode == HoldBreathMode.Hold)
        {
            SendKey(_holdBreathArmedKey, true);
            _holdBreathInjectedKey = _holdBreathArmedKey;
        }
        else if (_holdBreathArmedMode == HoldBreathMode.Toggle)
        {
            // P6: DOWN happens synchronously right here (we already hold _holdBreathLock), which is
            // the documented _holdBreathLock -> _transientTapLock nesting (see both lock declarations).
            // Only duration + UP defer to the pool, same as every other FireTapKey call site.
            FireTapKey(_holdBreathArmedKey, HOLD_BREATH_TAP_DURATION_MIN_MS, HOLD_BREATH_TAP_DURATION_MAX_MS);
        }
    }

    // Must be called while holding _holdBreathLock.
    private void ReleaseInjectedHoldBreathKeyLocked()
    {
        if (_holdBreathInjectedKey is { } key)
        {
            SendKey(key, false);
            _holdBreathInjectedKey = null;
            if (IsDebugEnabled) LogDebug($"HoldBreath released: {key}");
        }
    }

    private void ReleaseHoldBreathState()
    {
        // Unconditional: releases the recorded injected key, so it works even if the feature was
        // disabled, the key was rebound, or the profile changed while the key was held.
        lock (_holdBreathLock)
        {
            _holdBreathPending = false;
            if (!_disposed)
            {
                _holdBreathTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            ReleaseInjectedHoldBreathKeyLocked();
        }
    }

    // ==================== WINDOWS LAUNCHER ====================
    
    private bool HandleWindowsLauncher(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        var profile = _windowsProfile;
        if (profile is null || !profile.WindowsLauncher.IsEnabled)
        {
            return false;
        }

        var key = KeyInteropUtilities.FromVirtualKey(vkCode);
        if (key is null)
        {
            return false;
        }

        // On key-up, clear the latch. Suppress the lone up iff we latched its down, so the foreground app
        // never receives a stray key-up for a hotkey we consumed.
        if (isKeyUp)
        {
            lock (_heldLauncherKeysLock)
            {
                return _heldLauncherKeys.Remove(key.Value);
            }
        }

        if (!isKeyDown)
        {
            return false;
        }

        // P3: cheap dictionary/null checks first — saves 2 GetAsyncKeyState syscalls on every
        // unhandled keydown while a launcher profile is enabled. Pure conjunction, so reordering
        // against the Win-key check below cannot change the truth table.
        if (!profile.WindowsLauncher.Launchers.TryGetValue(key.Value, out var binding))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(binding.Path))
        {
            return false;
        }

        // Check if Windows key is pressed
        bool winPressed = (NativeMethods.GetAsyncKeyState(KeyInteropUtilities.ToVirtualKey(Key.LWin)) & 0x8000) != 0 ||
                          (NativeMethods.GetAsyncKeyState(KeyInteropUtilities.ToVirtualKey(Key.RWin)) & 0x8000) != 0;

        if (!winPressed)
        {
            return false;
        }

        // H1: launch only on the FIRST key-down of a physical press. Typematic auto-repeat re-delivers
        // WM_KEYDOWN; the latch ensures exactly one launch until the key is released, while still
        // suppressing the repeats.
        lock (_heldLauncherKeysLock)
        {
            if (!_heldLauncherKeys.Add(key.Value))
            {
                return true;
            }
        }

        // The shell never sees any key while Win is held (we consume the whole chord), so a bare
        // Win press+release would pop the Start menu right after the launch and steal focus.
        // Inject one benign tagged dummy-key event on the first latch so the shell marks the
        // chord as used (same technique as PowerToys/AutoHotkey).
        SendDummyKeyEvent();

        // Snapshot the binding on the hook thread (serialized with UI edits) so the pool-thread
        // launch can't read a half-edited Path/Arguments/RunAsAdmin combination.
        var path = binding.Path;
        var arguments = binding.Arguments;
        var runAsAdmin = binding.RunAsAdmin;

        LogDebug($"WindowsLauncher: Win+{key.Value} → {path}");

        // Launch asynchronously (don't block hook)
        ThreadPool.QueueUserWorkItem(_ => LaunchProcess(path, arguments, runAsAdmin));

        return true;
    }

    private void LaunchProcess(string path, string arguments, bool runAsAdmin)
    {
        try
        {
            ProcessLauncher.Launch(path, arguments, runAsAdmin, _logger);

            LogDebug($"Launch successful: {path}");
        }
        catch (Exception ex)
        {
            LogDebug($"Launch failed: {path} - {ex.Message}");
        }
    }

    // ==================== KEY INJECTION ====================
    
    // P6: DOWN is synchronous and takes place on the CALLER's thread (hook thread, or a hold-breath
    // timer thread already holding _holdBreathLock) — deterministic, ordered ahead of whatever the
    // user does next. Only the human-like duration + UP defer to the pool. Parameterized so Alt+Mouse
    // (31-53ms) and hold-breath Toggle (20-31ms) share one implementation with unchanged distributions.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FireTapKey(Key key, int minDurationMs, int maxDurationMs)
    {
        var epochAtCall = _tapReleaseEpoch;
        lock (_transientTapLock)
        {
            // Under the lock so the DOWN can never land after ReleaseAllState drained the list; the
            // epoch check drops a tap that was decided before a release boundary. For hook-thread
            // callers this is near-vacuous (the hook thread can't interleave with itself); for
            // timer-fired holds it still closes the decision->injection gap against a concurrent
            // ReleaseAllState.
            if (_disposed || !_isRunning || epochAtCall != _tapReleaseEpoch)
            {
                return;
            }

            _transientTapKeys.Add(key);
            SendKey(key, true);
        }

        ThreadPool.QueueUserWorkItem(_ =>
        {
            var rng = _random.Value!;

            // Warmup RNG to break thread-reuse patterns (anti-cheat)
            var warmupCalls = rng.Next(RNG_WARMUP_MIN_CALLS, RNG_WARMUP_MAX_CALLS + 1);
            for (int i = 0; i < warmupCalls; i++)
            {
                rng.Next();
            }

            // Human-like key press duration with jitter
            var duration = rng.Next(minDurationMs, maxDurationMs + 1);
            Thread.Sleep(duration);

            lock (_transientTapLock)
            {
                // If a release path already forced the UP (and removed the entry), don't double-send.
                // No epoch/_disposed/_isRunning check needed here (I3): once the DOWN landed, the UP
                // is unconditional — the Remove-guard alone is sufficient to prevent a double-send.
                if (_transientTapKeys.Remove(key))
                {
                    SendKey(key, false);
                }
            }

            if (IsDebugEnabled) LogDebug($"FireTapKey: {key}, duration={duration}ms, warmup={warmupCalls}");
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SendKey(Key key, bool isKeyDown)
    {
        var virtualKey = KeyInteropUtilities.ToVirtualKey(key);
        if (virtualKey == 0)
        {
            LogDebug($"SendKey FAILED: {key} ({(isKeyDown ? "DOWN" : "UP")}) - VirtualKey=0");
            return;
        }

        var flags = isKeyDown ? 0u : NativeMethods.KeyEventFlags.KEYEVENTF_KEYUP;
        
        if (IsExtendedKey(key))
        {
            flags |= NativeMethods.KeyEventFlags.KEYEVENTF_EXTENDEDKEY;
        }

        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.InputType.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = (ushort)NativeMethods.MapVirtualKey(virtualKey, 0),
                    dwFlags = flags,
                    time = 0,
                    // Always tag injected input so our own hook ignores it (LLKHF_INJECTED + INPUT_IGNORE).
                    dwExtraInfo = NativeMethods.INPUT_IGNORE
                }
            }
        };

        var result = NativeMethods.SendInput(1, new[] { input }, InputStructSize);

        if (result == 0)
        {
            LogDebug($"SendKey FAILED: {key} ({(isKeyDown ? "DOWN" : "UP")}) - SendInput returned 0, VK=0x{virtualKey:X2}");
        }
    }

    // Sends a single key-up for the unassigned virtual key 0xFF, tagged INPUT_IGNORE. Apps and
    // games cannot observe it as a real key; the shell only uses it to mark the current Win chord
    // as used, which suppresses the Start menu after a consumed Win+<key> hotkey.
    private static void SendDummyKeyEvent()
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.InputType.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = 0xFF,
                    wScan = 0,
                    dwFlags = NativeMethods.KeyEventFlags.KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = NativeMethods.INPUT_IGNORE
                }
            }
        };

        NativeMethods.SendInput(1, new[] { input }, InputStructSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsExtendedKey(Key key)
    {
        // P4: Key.Apps (VK_APPS / 0x5D, the context-menu key) is E0-extended; KeyCatalog offers it
        // as a mappable target, so scan-code-reading games need the flag to see the right physical key.
        return key is Key.RightAlt or Key.RightCtrl or Key.Insert or Key.Delete or
                      Key.Home or Key.End or Key.PageUp or Key.PageDown or
                      Key.Up or Key.Down or Key.Left or Key.Right or
                      Key.NumLock or Key.PrintScreen or Key.Divide or Key.Apps;
    }

    private bool IsCombinedOverrideActive(Key sourceKey, CombinedOverrideState expectedState)
    {
        lock (_combinedOverridesLock)
        {
            return _activeCombinedOverrides.TryGetValue(sourceKey, out var currentState) &&
                   ReferenceEquals(currentState, expectedState);
        }
    }

    private void ReleaseRightClickOverrides()
    {
        List<KeyValuePair<Key, CombinedOverrideState>>? overridesToRelease = null;

        lock (_combinedOverridesLock)
        {
            if (_activeCombinedOverrides.Count == 0)
            {
                return;
            }

            foreach (var kvp in _activeCombinedOverrides)
            {
                if (kvp.Value.RightClickOnly)
                {
                    overridesToRelease ??= new List<KeyValuePair<Key, CombinedOverrideState>>();
                    overridesToRelease.Add(kvp);
                }
            }

            if (overridesToRelease is null)
            {
                return;
            }

            foreach (var kvp in overridesToRelease)
            {
                _activeCombinedOverrides.Remove(kvp.Key);
            }

            _activeCombinedOverrideCount = _activeCombinedOverrides.Count;
        }

        foreach (var (key, state) in overridesToRelease)
        {
            SendKey(state.TargetKey, false);
            if (IsDebugEnabled) LogDebug($"Force-release right-click override key: {key}");
        }
    }

    private void ReleaseAllOverrides()
    {
        List<KeyValuePair<Key, CombinedOverrideState>>? overridesToRelease;

        lock (_combinedOverridesLock)
        {
            if (_activeCombinedOverrides.Count == 0)
            {
                return;
            }

            overridesToRelease = new List<KeyValuePair<Key, CombinedOverrideState>>(_activeCombinedOverrides.Count);
            foreach (var kvp in _activeCombinedOverrides)
            {
                overridesToRelease.Add(kvp);
            }

            _activeCombinedOverrides.Clear();
            _activeCombinedOverrideCount = 0;
        }

        foreach (var (key, state) in overridesToRelease)
        {
            SendKey(state.TargetKey, false);
            LogDebug($"Force-release combined override key: {key} -> {state.TargetKey}");
        }
    }
// ==================== STATE MANAGEMENT ====================
    
    private void ReleaseAllState()
    {
        
        ReleaseAllOverrides();
        ResetMouseStates();
        ReleaseCapsState();
        ReleaseHoldBreathState();

        // Force-complete untracked taps mid-flight (DOWN sent, UP pending on a pool thread). The tap
        // worker skips its own UP when its entry is gone, so this never double-sends. The epoch bump
        // additionally invalidates taps queued before this boundary but not yet started.
        lock (_transientTapLock)
        {
            _tapReleaseEpoch++;

            foreach (var key in _transientTapKeys)
            {
                SendKey(key, false);
                LogDebug($"Force-release transient tap key: {key}");
            }

            _transientTapKeys.Clear();
        }

        lock (_heldLauncherKeysLock)
        {
            _heldLauncherKeys.Clear();
        }

        _altPressed = false;
        _rightButtonPressed = false;

        LogDebug("All state released");
    }

    // P9: ReleaseAllState() above force-clears both flags unconditionally, which is correct for
    // Stop()/OnSessionSwitch (the desktop itself is going away). Called ONLY from
    // ActivateProfile/DeactivateProfile, immediately after ReleaseAllState() and still inside
    // _profileLock: re-derives what the user is STILL physically holding across the switch so
    // AltMouse / RightClickOnly combined mappings don't go inert until the user releases and
    // re-presses. Re-deriving _rightButtonPressed=true does NOT re-arm hold-breath (arming only
    // happens on a real WM_RBUTTONDOWN) — it only lets RightClickOnly mappings work immediately,
    // which matches physical reality.
    private void RederivePhysicalModifierState()
    {
        _altPressed = (NativeMethods.GetAsyncKeyState(0xA4) & 0x8000) != 0 ||   // VK_LMENU
                      (NativeMethods.GetAsyncKeyState(0xA5) & 0x8000) != 0;    // VK_RMENU

        // GetAsyncKeyState reports the PHYSICAL button; the LL hook's WM_RBUTTONDOWN reports the
        // LOGICAL (post-swap) button. Query whichever physical VK currently maps to "right" so this
        // stays consistent with what HandleAltMouse/HandleCombinedMappings actually see on the hook.
        var physicalRightVk = NativeMethods.GetSystemMetrics(NativeMethods.SM_SWAPBUTTON) != 0
            ? NativeMethods.VK_LBUTTON
            : NativeMethods.VK_RBUTTON;
        _rightButtonPressed = (NativeMethods.GetAsyncKeyState(physicalRightVk) & 0x8000) != 0;
    }

    private void ResetMouseStates()
    {
        foreach (var (button, state) in _mouseStates)
        {
            CancelHoldTimer(state);
            Interlocked.Exchange(ref state.TimerState, TIMER_IDLE);
            Interlocked.Exchange(ref state.DownTick, 0L);
            
            LogDebug($"Reset mouse state: {button}");
        }
    }

    // ==================== STATE CLASSES ====================
    
    private sealed class MouseButtonState
    {
        // Atomic state machine
        public int TimerState = TIMER_IDLE;
        
        // Down timestamp; 0 = not tracked (Stopwatch.GetTimestamp() is never 0). Accessed via
        // Interlocked because ResetMouseStates clears it from the activation-worker/SystemEvents
        // threads while the mouse hook reads and writes it — a torn Nullable<long> here could
        // fabricate a huge elapsed time and inject a phantom hold key.
        public long DownTick;
        
        // Pre-allocated timer (reused for every click)
        public readonly System.Threading.Timer HoldTimer;
        public TimerCallback? HoldCallback;

        public MouseButtonState()
        {
            // Pre-allocate timer - will be reused throughout lifetime
            HoldTimer = new System.Threading.Timer(_ => HoldCallback?.Invoke(null), null, Timeout.Infinite, Timeout.Infinite);
        }
    }

    private sealed class CombinedOverrideState
    {
        public required Key TargetKey { get; init; }
        public required bool SuppressOriginal { get; init; }
        public required bool RightClickOnly { get; init; }
    }

    // ==================== LOGGING ====================
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void LogDebug(string message)
    {
        _logger.Log(message);
    }
}
