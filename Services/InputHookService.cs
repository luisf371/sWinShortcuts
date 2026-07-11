using System;
using System.Collections.Concurrent;
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
    // F-011: how many active source keys currently drive each TARGET key (guarded by _combinedOverridesLock).
    // Target DOWN is sent only on 0→1 and target UP only on 1→0, so two sources mapped to one target don't
    // release it prematurely when the first source is released.
    private readonly Dictionary<Key, int> _combinedTargetCounts = new();
    // Maintained under _combinedOverridesLock on every add/remove; lets the H2 key-up release skip the
    // lock entirely when no overrides are active (the common case on every key-up).
    private volatile int _activeCombinedOverrideCount;
    
    // Profile state
    private volatile Profile? _activeProfile;
    private volatile Profile? _windowsProfile;

    // A1: foreground identity published off-hook by the foreground watcher. A volatile reference gives an
    // atomic whole-snapshot publish/read (no torn {hwnd,pid,exe}); Auto-Run activation confirms the live
    // foreground against it with cheap non-blocking calls, keeping Process.GetProcessById off the hook thread.
    private sealed record ForegroundIdentitySnapshot(IntPtr Hwnd, uint Pid, string? Exe);
    private volatile ForegroundIdentitySnapshot? _foregroundIdentity;
    
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
    // F-012 (codex #4): the suppression decision recorded at the Caps key-DOWN, replayed on the matching
    // key-UP so the UP is suppressed iff the DOWN was — even if the mode/enable changed while Caps was held.
    private bool _capsDownSuppressed;
    // codex-final #2: true from the INITIAL physical Caps DOWN until its UP. Typematic auto-repeat re-fires
    // WM_KEYDOWN while the key is held; without this latch each repeat re-decided suppression and overwrote
    // _capsDownSuppressed, so a mode/enable change mid-hold desynced the UP from its DOWN — leaking a
    // physical Caps to Windows (stuck CapsLock) or stranding the injected remap. Latched ONCE per press.
    private bool _capsPhysicallyDown;

    // Global color-variant toggle (Primary<->Secondary). _colorToggleVk is the assigned key's virtual-key
    // code (0 = unassigned), published (volatile) from any thread via SetColorToggleKey and read on the hook
    // thread. The key is NOT suppressed — it passes through to apps like any key — so there is no down/up
    // pairing to get wrong across a re-assign / hook restart / watchdog reinstall: the worst case is one
    // missed or extra color flip, never a stuck key or a wrong binding. _colorToggleDownLatched
    // (hook-thread-only) simply makes the toggle fire ONCE per physical press, not on every typematic repeat.
    private volatile int _colorToggleVk;
    private bool _colorToggleDownLatched;
    private int _hookSeenToggleVk; // hook-thread-only; used only to clear the fire-once latch on a re-assign

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
    // LOCK ORDER (I5, P6): historically taken while _holdBreathLock was held (Toggle-mode taps went
    // through FireTapKey); Toggle now uses the hold-breath injector queue instead, so no call path
    // nests these locks anymore. The rule stands for future code: if nesting is ever reintroduced,
    // it must be one-way _holdBreathLock -> _transientTapLock ONLY, and never the reverse.
    private readonly object _transientTapLock = new();
    private readonly List<Key> _transientTapKeys = new();

    // Bumped by ReleaseAllState under _transientTapLock. A tap worker captures the epoch when it is
    // QUEUED and bails if it changed by the time it runs: a tap queued before a profile/session
    // release boundary must not inject after it (the drain above only covers taps already mid-flight).
    private volatile int _tapReleaseEpoch;

    // Hold-breath state. All fields below are guarded by _holdBreathLock. Every hold-breath event
    // (arm, fire, cancel, release) already paid this lock for Timer.Change, and events occur at
    // human click frequency — so guarding the whole state machine costs nothing extra.
    //
    // SendInput is deliberately NOT under this lock (measured: an injected event's synchronous trip
    // through a stalled foreign LL hook can take ~300ms = LowLevelHooksTimeout; holding the lock
    // across it blocked the WM_RBUTTONUP handler INSIDE the mouse hook and froze all pointer input
    // for the duration — the "right-click stutter"). Injection is decided under the lock but
    // executed by the single FIFO injector thread below, whose ordering guarantees the UP can never
    // overtake its DOWN — the property the lock used to provide.
    private readonly object _holdBreathLock = new();
    private readonly System.Threading.Timer _holdBreathTimer;
    private bool _holdBreathPending;
    private Key? _holdBreathInjectedKey;    // key whose DOWN is enqueued/sent in Hold mode and not yet released
    private Key _holdBreathArmedKey;        // settings snapshot at arm time (UI mutates settings in place)
    private HoldBreathMode _holdBreathArmedMode;
    private long _holdBreathArmedTick;      // arm timestamp for the stale-fire guard
    private int _holdBreathArmedDelayMs;
    private long _holdBreathGeneration;
    private bool _holdBreathPanicSuppressed;
    // Hook-thread-owned paired suppression state. Lifecycle boundaries clear these atomically so a
    // rebind/profile switch cannot swallow a future unrelated key or button press.
    private int _holdBreathPanicConsumedKeyVk;
    private int _holdBreathPanicConsumedMouseButton;

    // Hold-breath injector: a dedicated thread draining a FIFO queue so no hook callback and no
    // lock-holding path ever waits on SendInput's foreign-hook dispatch. One consumer = strict FIFO
    // = every enqueued DOWN is released by the UP enqueued behind it (release paths are
    // unconditional, so pairing survives profile switches, disable, and Stop's final drain).
    // PreSleepMs implements the Toggle-mode tap duration between a DOWN and its own UP.
    // Shared key-injector item (hold-breath + auto-run Foreground + anti-afk). Sequence, when non-null,
    // carries a self-contained atomic tap sequence (anti-afk WASD) executed as ONE queue item so a
    // paired DOWN/UP can never be split across a shutdown drain (see HoldBreathInjectionLoop). Record
    // name retained for stability; the §10 rename to KeyInjection is a separate optional step.
    // AutoRunGeneration/ExpectedForegroundExe (A2): a NON-zero AutoRunGeneration marks a foreground-guarded
    // Auto-Run DOWN — the injector fires it only if the generation is still current AND the foreground
    // window still belongs to ExpectedForegroundExe, so a queued W-down can't drain into a window you
    // alt-tabbed to. Zero (the default for hold-breath / UPs / anti-afk) means "unguarded", i.e. today's
    // behavior. Appended after the existing optional fields so positional callers remain unchanged.
    private readonly record struct HoldBreathInjection(Key Key, bool IsDown, int PreSleepMs, TapStep[]? Sequence = null, long AutoRunGeneration = 0, string? ExpectedForegroundExe = null, long HoldBreathGeneration = 0);

    // One paired tap in an atomic Anti-AFK sequence: DownMs held, then GapMs before the next tap.
    private readonly record struct TapStep(Key Key, int DownMs, int GapMs);
    private BlockingCollection<HoldBreathInjection>? _holdBreathInjectionQueue;
    private Thread? _holdBreathInjectionThread;
    
    // ==================== AUTO-RUN STATE ====================
    // Auto-Run holds W (and optionally sprint) via the shared injector, toggled by a modifier+key
    // chord, cancelled by a FRESH physical down-edge of W/S/sprint. Active-state records are guarded by
    // _autoRunLock; _autoRunActive is volatile so the keyboard hot path reads it lock-free (its write
    // is release-ordered AFTER the record + sprint-snapshot writes, so a reader seeing true also sees
    // them). Physical down-state is keyboard-hook-thread-only and lock-free (like _altPressed),
    // re-seeded from GetAsyncKeyState at each activation.
    private const int VK_W = 0x57;
    private const int VK_S = 0x53;
    private const int AUTO_RUN_REPEAT_MS = 35; // Background re-post cadence (see BackgroundInputLoop)
    // Background sprint activation timing (user spec): after W-down, wait this before pressing the sprint
    // key — posting it back-to-back with W is read "too soon" by GZW and skipped. Press/toggle then holds
    // the tap this long before releasing so the game registers a clean press.
    private const int BG_SPRINT_PREDELAY_MIN_MS = 40;
    private const int BG_SPRINT_PREDELAY_MAX_MS = 60;
    private const int BG_SPRINT_TAP_MIN_MS = 40;
    private const int BG_SPRINT_TAP_MAX_MS = 60;
    // On focus-REGAIN, re-post W then wait this quiet window before re-engaging a Background Hold sprint
    // (mirrors the activation "W → 40-60ms → sprint" ordering so the game doesn't read sprint as "too soon
    // after W"). W re-posts are suppressed during the window. See BackgroundInputLoop.
    private const int BG_SPRINT_REENGAGE_QUIET_MS = 50;

    private readonly object _autoRunLock = new();
    private volatile bool _autoRunActive;
    // A2 foreground-guard epoch. Incremented (Interlocked) on each successful FOREGROUND activation and on
    // EVERY terminal release of an active run; a queued guarded Auto-Run DOWN carries the generation it was
    // stamped with, and the injector fires it only while that generation is still current (see
    // HoldBreathInjectionLoop). _activeAutoRunInjectionGeneration is the current Foreground run's stamp,
    // reused when a mid-run sprint re-engage enqueues a fresh DOWN.
    private long _autoRunInjectionGeneration;
    private long _activeAutoRunInjectionGeneration;
    private string? _autoRunForegroundGuardExe; // A2 expected-exe for a Foreground run's guarded DOWNs (incl. sprint re-engage); null for Background
    private bool _autoRunMoveInjected;          // injected W recorded (I2); guarded by _autoRunLock
    private bool _autoRunSprintInjected;        // guarded by _autoRunLock
    private Key _autoRunSprintInjectedKey;      // guarded by _autoRunLock
    private Key _autoRunSprintKey;              // sprint-key snapshot for the run; written under _autoRunLock, read lock-free after the volatile _autoRunActive publish
    private bool _autoRunSprintToggleable;      // snapshot: SprintEnabled && Hold mode — the sprint key acts as the sustained-sprint stamina toggle (never cancels)
    private bool _autoRunSprintEnabled;         // Background only: sprint pending the delayed activation on the Background thread (see DoDelayedBackgroundSprintActivation); guarded by _autoRunLock
    // Sprint state is SPLIT (codex sol/xhigh): _autoRunSprintIntendedHeld = the user WANTS sprint held for
    // this run; _autoRunSprintInjected = best-effort belief the target CURRENTLY treats sprint as down in
    // the current focus epoch. They diverge after alt-tab (the game clears sprint on focus-loss → current
    // goes false, intent stays true), which is what lets the first physical press re-engage instead of
    // being eaten, and lets the Background loop re-engage once on focus-regain. Both guarded by _autoRunLock.
    private bool _autoRunSprintIntendedHeld;

    // Background-transport run state (guarded by _autoRunLock). A Background run posts to the game's
    // HWND (survives alt-tab) and is DECOUPLED from _activeProfile (§11.6): ReleaseAllState skips it;
    // only the hard-teardown sites (Stop / OnSessionSwitch / Advanced-off), an explicit chord toggle-off,
    // a focused physical cancel, or a per-post validation failure release it.
    private bool _autoRunIsBackground;
    private IntPtr _autoRunTargetHwnd;
    private string? _autoRunTargetExe;          // normalized exe snapshot for per-post validation + focus check
    // Background-transport re-post / modifier-state-hold runs on a DEDICATED thread (not a threadpool
    // timer): the sprint-modifier state-hold needs a STABLE thread to keep an AttachThreadInput alive
    // across ticks (threadpool callbacks hop threads, and the attach is per-thread). See BackgroundInputLoop.
    // Lifecycle fields are read/written under _autoRunLock; the thread is NEVER joined on the hook thread
    // (chord toggle-off releases there — I5) — release only signals _backgroundInputRun=false; Stop/Dispose
    // join off-hook via JoinBackgroundInputThread. A run-identity check (_backgroundInputThread == self)
    // lets a stale thread exit if a new run starts before it winds down.
    private Thread? _backgroundInputThread;
    private bool _backgroundInputRun;
    private uint _autoRunTargetPid;             // Background target process id (for the debug heartbeat's foreground check)
    private int _autoRunRepostTicks;            // Background: re-post tick counter for the throttled debug heartbeat

    // Trigger-chord latches — keyboard-hook-thread ONLY, lock-free, keyed by VK (0 = none) so a profile
    // switch to a DIFFERENT trigger key mid-press can't confuse them. _autoRunConsumedTriggerVk
    // suppresses the repeats + matching keyup of a chord press that already fired, handled UNGATED so
    // turning Advanced Mode off or switching profiles mid-press can't leak a stray trigger key or
    // strand the latch (codex P3a #1). _triggerKeyDownVk rejects auto-repeat so only a FRESH trigger
    // down forms a chord.
    private int _autoRunConsumedTriggerVk;
    private int _triggerKeyDownVk;

    // Trigger snapshot for TOGGLE-OFF (hook-thread-only). A decoupled background run outlives its
    // profile, so toggle-off must match the trigger that STARTED the run, not the live _activeProfile
    // (which may be null/different after alt-tab). Set at activation, read on toggle-off.
    private int _autoRunSnapshotTriggerVk;
    private System.Windows.Input.ModifierKeys _autoRunSnapshotModifier;

    // Physical key down-state — keyboard-hook-thread ONLY, lock-free. NOT cleared on cancel/release
    // (tracks physical reality); RE-SEEDED from GetAsyncKeyState at each activation.
    private bool _wPhysicallyDown;
    private bool _sPhysicallyDown;
    private bool _sprintPhysicallyDown;

    // ==================== ANTI-AFK STATE ====================
    // One always-ticking timer (fixed coarse period). No arm/disarm — each tick reads live conditions,
    // so a UI toggle on the ALREADY-active profile takes effect within one period, and there is no
    // ActivateProfile ordering hazard. Fires ONE atomic WASD tap-sequence on the shared injector when
    // idle + focused on the active game profile.
    private System.Threading.Timer? _antiAfkTimer;
    private int _antiAfkTickRunning;        // single-flight (Interlocked), like _watchdogTickRunning
    private uint _antiAfkLastFireTick;      // (uint)Environment.TickCount of the last fire, for cadence
    private int _antiAfkDiagTicks;          // throttle counter for the idle-check diagnostic log
    private const int ANTI_AFK_PERIOD_MS = 5_000;
    private const int ANTI_AFK_GAP_MIN_MS = 90;   // inter-tap gap jitter
    private const int ANTI_AFK_GAP_MAX_MS = 160;

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

    // Anti-AFK idle basis: last PHYSICAL (non-injected) keyboard event, stamped AFTER the
    // LLKHF_INJECTED / INPUT_IGNORE filter (unlike _lastKeyboardEventTick, which counts injected
    // events too so it stays valid as hook-liveness proof). Keyboard-ONLY on purpose: GetLastInputInfo
    // is global (all devices), so mouse/peripheral noise kept the system "fresh" and Anti-AFK's idle
    // guard never tripped (debug.log: 69 "system input is fresh" / 0 idle). Volatile: written on the
    // hook thread, read on the Anti-AFK timer thread.
    private long _lastPhysicalKeyboardTick;

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

    // Advanced Mode: global [App] gate for non-1:1 automation (Auto-Run, Anti-AFK, Hold-Breath, and
    // un-suppressed key mappings). Mirrors HookWatchdogEnabled end-to-end; live-togglable from Settings.
    // volatile for the lock-free gating reads on the hook thread (and the injector thread).
    private volatile bool _advancedModeEnabled;

    public bool AdvancedModeEnabled
    {
        get => _advancedModeEnabled;
        set
        {
            if (_advancedModeEnabled == value)
            {
                return;
            }

            _advancedModeEnabled = value;
            LogDebug($"Advanced Mode {(value ? "enabled" : "disabled")} via settings");

            // true→false: release every gated held state so nothing keeps injecting under a now-off
            // gate. This setter runs on the UI dispatcher — which IS the hook thread — so each release
            // MUST be enqueue-only / non-blocking (a synchronous SendInput here could stall the
            // dispatcher on a foreign LL hook, the very freeze the injector exists to prevent). Each
            // release takes only its own leaf lock; they are never nested (I5). Anti-AFK needs no
            // action — its tick self-gates on _advancedModeEnabled. (Auto-Run release is wired in P3a.)
            if (!value)
            {
                ReleaseAutoRunState(includeBackground: true); // gate closed — release Background too
                ReleaseHoldBreathState();
                ReleaseUnsuppressedCombinedOverrides();
            }
        }
    }

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

    public event EventHandler? ColorVariantToggleRequested;

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
            // Seed Anti-AFK's keyboard-idle basis too, so a freshly-started app accumulates idle from
            // 0 (not "infinitely idle" → an immediate spurious fire).
            Volatile.Write(ref _lastPhysicalKeyboardTick, startTick);
            // Seed the cadence baseline explicitly (not the default 0) so first-fire cadence is correct even
            // across the rare 49.7-day Environment.TickCount rollover where 0 is a real recent value.
            _antiAfkLastFireTick = unchecked((uint)Environment.TickCount);

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

                // Hold-breath injector thread: drains the FIFO injection queue so SendInput's
                // foreign-hook dispatch (measured up to ~LowLevelHooksTimeout) never runs on a hook
                // callback or under _holdBreathLock. Background so process exit can never hang on it.
                _holdBreathInjectionQueue = new BlockingCollection<HoldBreathInjection>();
                _holdBreathInjectionThread = new Thread(HoldBreathInjectionLoop)
                {
                    IsBackground = true,
                    Name = "HoldBreathInjector"
                };
                _holdBreathInjectionThread.Start();

                // P8: hook-loss watchdog. 10s period is coarse on purpose — this only needs to catch
                // the rare silent hook removal (UI stall > LowLevelHooksTimeout), not run hot.
                _hookWatchdogTimer = new System.Threading.Timer(_ => WatchdogTick(), null, WATCHDOG_PERIOD_MS, WATCHDOG_PERIOD_MS);

                // Anti-AFK: one always-ticking timer at a fixed coarse period; the tick reads live
                // conditions each time (no arm/disarm). Rolled back below on a failed Start.
                _antiAfkTimer = new System.Threading.Timer(_ => AntiAfkTick(), null, ANTI_AFK_PERIOD_MS, ANTI_AFK_PERIOD_MS);
            }
            catch
            {
                // Full rollback before rethrow: _isRunning is still false here, so Stop() will never
                // run to unhook — and a retried Start() would otherwise stack a second pair of LL
                // hooks on top of these. Mirrors the hook-install-failure branch above.
                _hookWatchdogTimer?.Dispose();
                _hookWatchdogTimer = null;

                _antiAfkTimer?.Dispose();
                _antiAfkTimer = null;

                // Ends the injector loop; nothing was enqueued yet (hooks never went live).
                _holdBreathInjectionQueue?.CompleteAdding();
                _holdBreathInjectionThread = null;

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

            // codex-final #2: a genuine (re)start is the only place to (re)seed the physical-Caps latch — a
            // mid-hold profile switch / watchdog reinstall keep the hook running and never call Start(), so
            // they PRESERVE the latch (a held Caps's UP still pairs with its original DOWN). SEED from the
            // ACTUAL physical key state, NOT blindly to false: if Caps is already held across Stop->Start (or
            // at initial launch) Windows already received that DOWN, so mark the press in-progress and NOT
            // suppressed — the carryover repeats + UP then PASS THROUGH and pair with Windows' DOWN instead
            // of a suppressed orphan UP (= stuck CapsLock). A not-held start seeds false -> next press is
            // fresh. Hooks are installed above but _isRunning is still false, so no callback is honored yet.
            lock (_capsLockStateLock)
            {
                _capsPhysicallyDown = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CAPITAL) & 0x8000) != 0;
                _capsDownSuppressed = false;
            }

            // Fresh session: SEED the color-toggle fire-once latch from the ACTUAL physical key state (mirrors
            // the Caps seed above). If the toggle key is still held across Stop->Start its press already fired,
            // so latch TRUE so its post-restart typematic repeats DON'T re-fire (a blind clear would
            // double-fire); its UP then clears it. Not held -> false -> the next press fires. Sync
            // _hookSeenToggleVk so HandleColorToggle's reconciliation doesn't immediately clear this seed.
            var colorToggleVk = _colorToggleVk;
            _colorToggleDownLatched = colorToggleVk != 0 && (NativeMethods.GetAsyncKeyState(colorToggleVk) & 0x8000) != 0;
            _hookSeenToggleVk = colorToggleVk;

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

            // Stop new Anti-AFK ticks. An in-flight tick re-checks _isRunning (flipped false below) and
            // the injector drain skips any sequence item it enqueued.
            _antiAfkTimer?.Dispose();
            _antiAfkTimer = null;

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
            // passed its entry check re-validates _isRunning under the subsystem locks, so it can no
            // longer inject AFTER ReleaseAllState ran — with the hooks gone, nothing would ever
            // release such a key and it would stay stuck system-wide beyond process exit.
            _isRunning = false;

            ReleaseAllState();
            // §11.6: ReleaseAllState skips a decoupled Background Auto-Run; Stop() (app exit) must still
            // release it — post the final UP before the injector drains below.
            ReleaseAutoRunState(includeBackground: true);
            // Off-hook (app lifecycle): join the Background thread so its AttachThreadInput is undone
            // before we tear down further. ReleaseAutoRunState above only SIGNALS stop (hook-safe).
            JoinBackgroundInputThread();

            // Drain the hold-breath injector AFTER ReleaseAllState so the release it enqueued still
            // executes; bounded join because a drain item can be mid-flight through a stalled foreign
            // hook (~300ms class). Dispose the queue only on a clean join — a still-draining worker
            // must not have it yanked out from under GetConsumingEnumerable (it exits on its own at
            // CompleteAdding; the thread is background, so process exit never hangs on it).
            _holdBreathInjectionQueue?.CompleteAdding();
            if (_holdBreathInjectionThread is null || _holdBreathInjectionThread.Join(2000))
            {
                _holdBreathInjectionQueue?.Dispose();
            }
            _holdBreathInjectionQueue = null;
            _holdBreathInjectionThread = null;

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
            // §11.6: ReleaseAllState skips a decoupled Background Auto-Run; the desktop is going away
            // (lock/logoff), so release it here too.
            ReleaseAutoRunState(includeBackground: true);
            LogDebug($"Session switch ({e.Reason}): released all injected state");
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
        if (!_isRunning)
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
                return;
            }

            LogDebug($"Watchdog: hook loss CONFIRMED by raw-input sink (keyboard={reinstallKeyboard}, mouse={reinstallMouse}, " +
                     $"kbIdle={keyboardIdleMs:F0}ms, mouseIdle={mouseIdleMs:F0}ms, kbRawAge={keyboardRawAgeMs:F0}ms, mouseRawAge={mouseRawAgeMs:F0}ms)");

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
                        if (!_isRunning || !_hookWatchdogEnabled)
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

    private static bool IsModifierVirtualKey(int vk) =>
        vk is 0x10 or 0x11 or 0x12   // VK_SHIFT / VK_CONTROL / VK_MENU
           or 0xA0 or 0xA1           // VK_LSHIFT / VK_RSHIFT
           or 0xA2 or 0xA3           // VK_LCONTROL / VK_RCONTROL
           or 0xA4 or 0xA5           // VK_LMENU / VK_RMENU (Alt)
           or 0x5B or 0x5C;          // VK_LWIN / VK_RWIN

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

        // Anti-AFK keyboard-idle basis: a genuine PHYSICAL key event (injected events returned above).
        Volatile.Write(ref _lastPhysicalKeyboardTick, Stopwatch.GetTimestamp());

        bool isKeyDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        bool isKeyUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
        int vkCode = (int)data.vkCode;

        // Global color-variant toggle: fire on the assigned key (once per physical press). The key is NOT
        // suppressed — it passes through to apps and the feature chain below — so it can never strand a key or
        // create a wrong binding. Modifiers are rejected as toggle keys (SetColorToggleKey), so this never
        // shadows the Alt-tracking that follows.
        HandleColorToggle(vkCode, isKeyDown, isKeyUp);

        var suppressEarlyCancelKey = HandleHoldBreathPanicKey(vkCode, isKeyDown, isKeyUp);

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

        if (suppressEarlyCancelKey)
        {
            return (IntPtr)1;
        }

        // Auto-Run runs BEFORE the handled-chain: a cancel key (W/S) may ALSO be a combined-mapping
        // source, so it must be seen for cancel detection even when another feature would consume it.
        // Returns true only to suppress the trigger chord key; W/S/sprint pass through (false).
        if (HandleAutoRun(vkCode, isKeyDown, isKeyUp))
        {
            return (IntPtr)1;
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
    private bool HandleHoldBreathPanicKey(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        var consumedKeyVk = Volatile.Read(ref _holdBreathPanicConsumedKeyVk);
        if (consumedKeyVk != 0 && consumedKeyVk == vkCode)
        {
            if (isKeyUp)
            {
                Volatile.Write(ref _holdBreathPanicConsumedKeyVk, 0);
            }

            return true;
        }

        if (!isKeyDown)
        {
            return false;
        }

        var profile = _activeProfile;
        if (profile is null)
        {
            return false;
        }

        var settings = profile.RightClickHoldBreath;
        var trigger = settings.PanicTrigger;
        if (trigger.Kind != InputTriggerKind.KeyboardKey ||
            KeyInteropUtilities.ToVirtualKey(trigger.Key) != vkCode ||
            !_rightButtonPressed)
        {
            return false;
        }

        PanicHoldBreath();

        if (!settings.SuppressEarlyCancelInput)
        {
            return false;
        }

        Volatile.Write(ref _holdBreathPanicConsumedKeyVk, vkCode);
        return true;
    }

    private bool HandleHoldBreathPanicMouse(int message, uint mouseData)
    {
        var button = message switch
        {
            NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP => Models.MouseButton.Left,
            NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP => Models.MouseButton.Right,
            NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP => Models.MouseButton.Middle,
            _ => GetXButton(mouseData)
        };

        var consumedButton = Volatile.Read(ref _holdBreathPanicConsumedMouseButton);
        if (consumedButton != 0 && consumedButton == (int)button)
        {
            if (message is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_RBUTTONUP or
                NativeMethods.WM_MBUTTONUP or NativeMethods.WM_XBUTTONUP)
            {
                Volatile.Write(ref _holdBreathPanicConsumedMouseButton, 0);
            }

            return true;
        }

        if (message is not (NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or
                            NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN))
        {
            return false;
        }

        var profile = _activeProfile;
        if (profile is null)
        {
            return false;
        }

        var settings = profile.RightClickHoldBreath;
        var trigger = settings.PanicTrigger;
        if (trigger.Kind != InputTriggerKind.MouseButton ||
            trigger.MouseButton != button ||
            !_rightButtonPressed)
        {
            return false;
        }

        PanicHoldBreath();

        if (!settings.SuppressEarlyCancelInput)
        {
            return false;
        }

        Volatile.Write(ref _holdBreathPanicConsumedMouseButton, (int)button);
        return true;
    }

    private void PanicHoldBreath()
    {
        var lockWaitStart = Stopwatch.GetTimestamp();
        lock (_holdBreathLock)
        {
            LogHoldBreathLockWait("PANIC", lockWaitStart);

            if (_disposed || !_rightButtonPressed)
            {
                return;
            }

            if (!_holdBreathPanicSuppressed)
            {
                _holdBreathPanicSuppressed = true;
                CancelHoldBreathStateLocked();
                if (IsDebugEnabled) LogDebug("HoldBreath panic: canceled and suppressed until right-button-up");
            }
        }
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

        var handled = HandleHoldBreathPanicMouse(message, data.mouseData) ||
                      HandleAltMouse(message, data.mouseData);

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
            var sendUp = false;
            lock (_combinedOverridesLock)
            {
                if (_activeCombinedOverrides.Remove(sourceKey.Value, out held) && held is not null)
                {
                    // F-011: only the LAST source driving this target sends its UP.
                    sendUp = DecrementCombinedTarget(held.TargetKey);
                }
                _activeCombinedOverrideCount = _activeCombinedOverrides.Count;
            }

            if (held is not null)
            {
                if (sendUp)
                {
                    SendKey(held.TargetKey, false);
                }
                if (IsDebugEnabled) LogDebug($"Combined mapping released: {sourceKey.Value} (targetUp={sendUp})");
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

        // Advanced Mode off forces suppression on every mapping (1:1, source consumed) regardless of
        // the saved per-entry value — disabling Suppress is the gated non-1:1 capability. This is the
        // runtime belt; the XAML IsEnabled binding on the Suppress checkbox is the UI suspenders.
        var suppressOriginal = entry.SuppressOriginalKey || !_advancedModeEnabled;
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

            var sendDown = false;
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

                // F-011: refcount the target — only the FIRST source to drive it sends its DOWN.
                var count = _combinedTargetCounts.GetValueOrDefault(targetKey);
                _combinedTargetCounts[targetKey] = count + 1;
                sendDown = count == 0;
            }

            if (sendDown)
            {
                // codex #2: the old post-DOWN compensation had its own TOCTOU (a release UP landing between
                // the DOWN and the recheck → double UP). Removed. The residual concurrency race — a
                // pool-thread release interleaving between this add and the send, reordering DOWN/UP — is a
                // DOCUMENTED residual: the fully race-free fix routes ALL combined-target emissions through
                // one ordered injector (F-002, deferred). The common sequential multi-source case (two keys
                // → one target, released in turn) is correct with the refcount alone.
                SendKey(targetKey, true);
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

        // F-012: on key-up, release any RECORDED caps state FIRST — before consulting the current
        // enable/mode gates. If the feature was disabled, or the mode/target changed, while Caps was
        // physically held, this still releases the engaged state (Caps-forced-ON and/or the injected remap
        // key) by the RECORDED state, so it can never stick. Mirrors the H2 combined-mapping pattern.
        if (isKeyUp)
        {
            lock (_capsLockStateLock)
            {
                // Release any RECORDED injected state FIRST — regardless of current mode.
                if (_capsShiftEngaged)
                {
                    _capsShiftEngaged = false;
                    ForceCapsLockState(false);
                    LogDebug("CapsLock → FORCED OFF (recorded Hold release)");
                }

                if (_capsRemappedKey is { } recorded)
                {
                    SendKey(recorded, false);
                    _capsRemappedKey = null;
                    LogDebug($"CapsLock → {recorded} UP (recorded Remap release)");
                }

                // Return the PAIRED suppress decision latched at the matching DOWN (codex #4), so the UP's
                // suppression matches the DOWN's even if the mode/enable changed while held.
                var suppressUp = _capsDownSuppressed;
                _capsDownSuppressed = false;
                _capsPhysicallyDown = false; // codex-final #2: physical press ended — next DOWN re-latches.
                return suppressUp;
            }
        }

        // Key-DOWN: on the INITIAL physical press, decide suppression from CURRENT settings and LATCH it for
        // EVERY later event of this press (typematic repeats + the matching UP). Repeats must NOT re-decide:
        // a mode/enable change while Caps is held would otherwise desync the UP from its DOWN — leaking a
        // physical Caps to Windows (stuck CapsLock) or stranding the injected remap (codex-final #2).
        var settings = GetEffectiveCapsLockSettings();

        bool suppressDown;
        bool isInitialPress;
        lock (_capsLockStateLock)
        {
            isInitialPress = !_capsPhysicallyDown;
            if (isInitialPress)
            {
                _capsPhysicallyDown = true;
                _capsDownSuppressed = settings is { IsEnabled: true } && settings.Mode != CapsLockMode.Normal;
            }

            suppressDown = _capsDownSuppressed;
        }

        // Typematic repeat: mirror the latched decision WITHOUT re-running the mode switch. The initial press
        // already engaged the mode (and injected any remap DOWN, which stays held until the UP releases it);
        // re-entering the switch here with possibly-changed settings could fire a different mode's action
        // mid-hold. Trades Remap auto-repeat of the target for a guaranteed matched DOWN/UP pair.
        if (!isInitialPress)
        {
            return suppressDown;
        }

        if (!suppressDown)
        {
            return false;
        }

        switch (settings!.Mode)
        {
            case CapsLockMode.Disabled:
                LogDebug("CapsLock suppressed (Disabled mode)");
                return true;

            case CapsLockMode.Hold:
                lock (_capsLockStateLock)
                {
                    // _isRunning re-check under the lock: an in-flight callback racing Stop() must not
                    // toggle CapsLock after ReleaseCapsState already ran. Key-UP release is handled by the
                    // recorded-state block at the top of this method (F-012).
                    if (isKeyDown && !_capsShiftEngaged && _isRunning)
                    {
                        _capsShiftEngaged = true;
                        ForceCapsLockState(true);
                        LogDebug("CapsLock → FORCED ON (Hold mode)");
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
                    // Key-UP release is handled by the recorded-state block at the top of this method (F-012).
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
                // F-012: ALWAYS the matching Caps-off action. The engaged state is Caps-forced-ON regardless
                // of what the mode currently says (it may have changed since engage). The old mode-dependent
                // else sent an unrelated LeftShift UP, which left Caps stuck ON.
                ForceCapsLockState(false);
                LogDebug("Force-release CapsLock (forced OFF)");
            }

            if (_capsRemappedKey.HasValue)
            {
                SendKey(_capsRemappedKey.Value, false);
                LogDebug($"Force-release CapsLock remap: {_capsRemappedKey.Value}");
                _capsRemappedKey = null;
            }

            // codex-final #2: do NOT reset _capsPhysicallyDown / _capsDownSuppressed here. ReleaseCapsState
            // runs on every profile switch AND watchdog reinstall while the hook keeps running and Caps may
            // still be PHYSICALLY held — clearing the latch there would reclassify the next typematic repeat
            // as a fresh (suppressed) press whose UP no longer pairs with the original DOWN, leaking a Caps
            // DOWN to Windows with a suppressed UP = stuck CapsLock. The latch is cleared only in Start()
            // (the genuine fresh-session boundary; the reinstall path re-hooks WITHOUT calling Start()).
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

    private void CancelHoldBreathStateLocked()
    {
        _holdBreathPending = false;
        if (!_disposed)
        {
            _holdBreathTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        Interlocked.Increment(ref _holdBreathGeneration);
        ReleaseInjectedHoldBreathKeyLocked();
    }

    private void HandleRightClickHoldBreathDown()
    {
        var profile = _activeProfile;
        // Advanced-Mode-gated feature: an off gate blocks NEW activations only. The UP/release path
        // (HandleRightClickHoldBreathUp / ReleaseHoldBreathState) stays ungated (I3) so a hold in
        // flight when the flag flips still releases.
        if (profile is null || !profile.RightClickHoldBreath.IsEnabled || !_advancedModeEnabled)
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

        // M3 instrumentation: this runs INSIDE the mouse hook callback — any wait for _holdBreathLock
        // (held by the timer thread across its SendInput) stalls system-wide input delivery.
        var lockWaitStart = Stopwatch.GetTimestamp();
        lock (_holdBreathLock)
        {
            LogHoldBreathLockWait("DOWN", lockWaitStart);

            // A missed WM_RBUTTONUP (hook timeout, UAC secure desktop, Win+L) can leave the previous
            // press's key down; release it before starting a new cycle so it can never stay stuck.
            CancelHoldBreathStateLocked();

            // After cancellation, a disposed service or panic-vetoed press cannot arm.
            if (_disposed || _holdBreathPanicSuppressed)
            {
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
                // Immediate activation: the DECISION is synchronous on the hook thread; the
                // injection itself rides the injector queue, so this cannot stall the hook.
                ActivateHoldBreathLocked();
            }
        }
    }

    private void HandleRightClickHoldBreathUp()
    {
        // No profile/IsEnabled gate here: the release must pair with whatever was actually
        // injected, even if settings changed or the profile switched mid-hold.
        // M3 instrumentation: this runs INSIDE the mouse hook callback — any wait for _holdBreathLock
        // (held by the timer thread across its SendInput) stalls system-wide input delivery.
        var lockWaitStart = Stopwatch.GetTimestamp();
        lock (_holdBreathLock)
        {
            LogHoldBreathLockWait("UP", lockWaitStart);

            CancelHoldBreathStateLocked();
            _holdBreathPanicSuppressed = false;
        }
    }

    // M3 instrumentation: the mouse-hook thread waiting >=1ms on _holdBreathLock is direct evidence
    // of the timer-thread-SendInput contention window; below that it's noise not worth a log line.
    private void LogHoldBreathLockWait(string site, long lockWaitStart)
    {
        if (!IsDebugEnabled)
        {
            return;
        }

        var waitedMs = (Stopwatch.GetTimestamp() - lockWaitStart) * TickToMilliseconds;
        if (waitedMs >= 1.0)
        {
            LogDebug($"HoldBreath {site}: hook thread waited {waitedMs:F1}ms for _holdBreathLock (M3 contention)");
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

    // Must be called while holding _holdBreathLock. The DOWN is only ENQUEUED here — the injector's
    // FIFO ordering is what guarantees the UP handler's release (always enqueued behind it) can
    // never be reordered before the press lands. Nothing slow ever runs under the lock: the
    // measured ~300ms foreign-hook SendInput stall used to happen right here and froze the pointer
    // whenever a WM_RBUTTONUP blocked on this lock inside the mouse hook.
    private void ActivateHoldBreathLocked()
    {
        _holdBreathPending = false;
        var holdBreathGeneration = Interlocked.Increment(ref _holdBreathGeneration);

        if (_holdBreathArmedMode == HoldBreathMode.Hold)
        {
            _holdBreathInjectedKey = _holdBreathArmedKey;
            EnqueueHoldBreathInjection(new HoldBreathInjection(_holdBreathArmedKey, IsDown: true, PreSleepMs: 0, HoldBreathGeneration: holdBreathGeneration));

            if (IsDebugEnabled) LogDebug($"HoldBreath ACTIVATED: mode={_holdBreathArmedMode}, key={_holdBreathArmedKey} (queued)");
        }
        else if (_holdBreathArmedMode == HoldBreathMode.Toggle)
        {
            // Toggle = a self-releasing tap: DOWN now, UP enqueued behind it with a human-like
            // duration as its pre-sleep. Both ride the injector (FireTapKey previously injected the
            // DOWN synchronously right here, paying the foreign-hook dispatch under this lock). The
            // queued UP is unconditional, so no _transientTapKeys tracking is needed on this path —
            // Stop()'s drain executes it even mid-shutdown.
            var rng = _random.Value!;

            // Warmup RNG to break thread-reuse patterns (anti-cheat)
            var warmupCalls = rng.Next(RNG_WARMUP_MIN_CALLS, RNG_WARMUP_MAX_CALLS + 1);
            for (int i = 0; i < warmupCalls; i++)
            {
                rng.Next();
            }

            var duration = rng.Next(HOLD_BREATH_TAP_DURATION_MIN_MS, HOLD_BREATH_TAP_DURATION_MAX_MS + 1);
            EnqueueHoldBreathInjection(new HoldBreathInjection(_holdBreathArmedKey, IsDown: true, PreSleepMs: 0, HoldBreathGeneration: holdBreathGeneration));
            EnqueueHoldBreathInjection(new HoldBreathInjection(_holdBreathArmedKey, IsDown: false, PreSleepMs: duration, HoldBreathGeneration: holdBreathGeneration));

            if (IsDebugEnabled) LogDebug($"HoldBreath ACTIVATED: mode={_holdBreathArmedMode}, key={_holdBreathArmedKey} (queued tap, duration={duration}ms)");
        }
    }

    // Must be called while holding _holdBreathLock. Enqueue-only: the injector's FIFO places this
    // UP behind its matching DOWN even when that DOWN is still queued or mid-SendInput — and the
    // mouse hook callback (WM_RBUTTONUP path) returns in microseconds instead of paying the
    // foreign-hook dispatch (measured 2-15ms typical, ~300ms when a foreign hook stalls).
    private void ReleaseInjectedHoldBreathKeyLocked()
    {
        if (_holdBreathInjectedKey is { } key)
        {
            _holdBreathInjectedKey = null;
            EnqueueHoldBreathInjection(new HoldBreathInjection(key, IsDown: false, PreSleepMs: 0));

            if (IsDebugEnabled) LogDebug($"HoldBreath released: {key} (queued)");
        }
    }

    private void ReleaseHoldBreathState()
    {
        // Unconditional: releases the recorded injected key, so it works even if the feature was
        // disabled, the key was rebound, or the profile changed while the key was held.
        lock (_holdBreathLock)
        {
            CancelHoldBreathStateLocked();
        }
    }

    // Enqueue-only; callers may hold _holdBreathLock (Add on an unbounded BlockingCollection never
    // blocks). The two shutdown races — CompleteAdding or Dispose landing between the null-check and
    // Add — are swallowed: in both cases Stop() already ran ReleaseAllState-then-drain, so any
    // recorded key still gets released before the queue closed.
    private void EnqueueHoldBreathInjection(HoldBreathInjection injection)
    {
        var queue = _holdBreathInjectionQueue;
        if (queue is null)
        {
            return;
        }

        try
        {
            queue.Add(injection);
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding or Dispose raced us (ObjectDisposedException derives from this):
            // shutting down, and Stop()'s ReleaseAllState-then-drain already handled the release.
        }
    }

    // Injector thread body: strict-FIFO single consumer, exits when Stop()/Start-rollback calls
    // CompleteAdding and the queue drains. Takes NO locks — that is the point: SendInput's
    // synchronous trip through every process's LL hook chain (measured ~300ms when a foreign hook
    // stalls, e.g. the game's own hook while its UI opens a context menu) lands here, where it can
    // stall nothing but the next queued hold-breath injection.
    private void HoldBreathInjectionLoop()
    {
        var queue = _holdBreathInjectionQueue;
        if (queue is null)
        {
            return;
        }

        try
        {
            foreach (var injection in queue.GetConsumingEnumerable())
            {
                try
                {
                    // Anti-AFK atomic tap-sequence (§3.4): executed here as ONE queue item so a DOWN/UP
                    // pair can never be split across a shutdown drain. Per-step abort at the TOP of each
                    // iteration, BEFORE that key's DOWN (never between a DOWN and its UP — the finally
                    // guarantees the UP), so a mid-sequence Stop/alt-tab/profile-switch leaves the
                    // remaining keys UNPRESSED rather than leaking into a new window or overrunning
                    // shutdown (bounds the post-Stop drain to one key-pair, not a whole 4-step sequence).
                    if (injection.Sequence is { } steps)
                    {
                        foreach (var step in steps)
                        {
                            // Gate on _advancedModeEnabled too (C1): turning Advanced Mode off must
                            // stop a still-queued ripple. An already-started step still completes its
                            // paired UP (the finally below) — that one pair is the accepted residual.
                            if (!_isRunning || !_advancedModeEnabled || queue.IsAddingCompleted || !ForegroundMatchesActiveProfile())
                            {
                                break;
                            }

                            try
                            {
                                SendKey(step.Key, true);
                                Thread.Sleep(step.DownMs);
                            }
                            finally
                            {
                                SendKey(step.Key, false);
                            }

                            Thread.Sleep(step.GapMs);
                        }
                        continue;
                    }

                    // Shutdown drain: once CompleteAdding was called (Stop/Start-rollback), any
                    // still-queued DOWN is stale pre-shutdown work — emitting a NEW press after the
                    // hooks are gone (or into a later session, if Stop's bounded join timed out and
                    // this worker outlived it) is never wanted. Releases still execute so recorded
                    // state always pairs; an UP whose DOWN was skipped is a harmless no-op. The
                    // queue's terminal state doubles as the per-session drain signal: a fresh
                    // Start() gets a fresh queue, while this one stays completed forever.
                    if (injection.IsDown && queue.IsAddingCompleted)
                    {
                        if (IsDebugEnabled) LogDebug($"HoldBreath inject DOWN skipped (shutdown drain): {injection.Key}");
                        continue;
                    }

                    // A panic/release boundary invalidates queued hold-breath DOWNs. The paired UP
                    // remains in FIFO and is still allowed to drain.
                    if (injection.IsDown && injection.HoldBreathGeneration != 0
                        && Volatile.Read(ref _holdBreathGeneration) != injection.HoldBreathGeneration)
                    {
                        if (IsDebugEnabled) LogDebug($"HoldBreath inject DOWN skipped (stale generation): {injection.Key}");
                        continue;
                    }
                    // A2: a foreground-guarded Auto-Run DOWN (AutoRunGeneration != 0) fires only while its
                    // epoch is still current AND the foreground window still belongs to the run's exe — so a
                    // queued W/sprint DOWN can't drain into a window you alt-tabbed to (or after the run
                    // ended). This runs on the injector thread, so the WindowBelongsToExe GetProcessById is
                    // OFF the hook thread. UPs (gen 0) are never guarded, so the skipped DOWN's paired UP
                    // still drains as a harmless no-op and FIFO pairing is preserved.
                    if (injection.IsDown && injection.AutoRunGeneration != 0
                        && (Volatile.Read(ref _autoRunInjectionGeneration) != injection.AutoRunGeneration
                            || !WindowBelongsToExe(NativeMethods.GetForegroundWindow(), injection.ExpectedForegroundExe)))
                    {
                        if (IsDebugEnabled) LogDebug($"AutoRun DOWN skipped (foreground guard): {injection.Key}");
                        continue;
                    }

                    // Toggle-mode tap duration rides as pre-sleep on the UP entry.
                    if (injection.PreSleepMs > 0)
                    {
                        Thread.Sleep(injection.PreSleepMs);
                    }

                    var sendStart = Stopwatch.GetTimestamp();
                    SendKey(injection.Key, injection.IsDown);

                    if (IsDebugEnabled)
                    {
                        var sendMs = (Stopwatch.GetTimestamp() - sendStart) * TickToMilliseconds;
                        LogDebug($"HoldBreath inject {(injection.IsDown ? "DOWN" : "UP")}: {injection.Key}, sendMs={sendMs:F1}");
                    }
                }
                catch (Exception ex)
                {
                    // A dead injector would strand every future hold-breath key; log and keep draining.
                    LogDebug($"HoldBreath injector error: {ex.Message}");
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Stop() disposed the queue after the final item; clean exit.
        }
    }

    // ==================== AUTO-RUN ====================

    // Called UNCONDITIONALLY at the top of the keyboard dispatch region, BEFORE the handled-chain, so
    // a cancel key (W/S) that is also a combined-mapping source is still seen for cancel detection.
    // Returns true ONLY to suppress the trigger chord key; W/S/sprint always pass through (false).
    private bool HandleAutoRun(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        // 1) Physical down-state bookkeeping (lock-free, hook-thread-only). Our own injected W/sprint
        //    are INPUT_IGNORE-filtered upstream (I1), so these flags track PHYSICAL reality only.
        //    Capture the pre-update value: a held key auto-repeats with wasDown == true (not fresh).
        bool freshW = false, freshS = false, freshSprint = false;

        if (vkCode == VK_W)
        {
            if (isKeyDown) { freshW = !_wPhysicallyDown; _wPhysicallyDown = true; }
            else if (isKeyUp) { _wPhysicallyDown = false; }
        }
        else if (vkCode == VK_S)
        {
            if (isKeyDown) { freshS = !_sPhysicallyDown; _sPhysicallyDown = true; }
            else if (isKeyUp) { _sPhysicallyDown = false; }
        }

        var active = _autoRunActive; // volatile read — lock-free hot path
        var sprintVk = active ? KeyInteropUtilities.ToVirtualKey(_autoRunSprintKey) : 0;

        // Sprint physical tracking (for the stamina toggle in 2b). Only while a run is active; the W/S
        // guard avoids double-handling if sprint == W/S was configured (W/S still cancel via freshW/S).
        if (active && sprintVk != 0 && vkCode == sprintVk && vkCode != VK_W && vkCode != VK_S)
        {
            if (isKeyDown) { freshSprint = !_sprintPhysicallyDown; _sprintPhysicallyDown = true; }
            else if (isKeyUp) { _sprintPhysicallyDown = false; }
        }

        // 2) Cancel on a FRESH physical down-edge of W or S (sprint NO LONGER cancels — see 2b). Pass the
        //    key THROUGH (return false) so pressing S to cancel still moves you back. A key held THROUGH
        //    activation produces no fresh edge (auto-repeat has wasDown == true), so releasing it keeps
        //    you running. A Background run cancels on physical W/S ONLY while the game is focused (user's
        //    choice: chord-only-while-unfocused, so stray W/S in another app can't kill it); a Foreground
        //    run always cancels. The GetForegroundWindow cost is paid only on a fresh W/S edge while a
        //    Background run is active.
        // E3 exclusion: if this fresh W/S is ALSO the run's trigger key WITH its modifier physically down,
        // it is a TOGGLE-OFF chord, not a bare cancel — skip step 2 so it falls to 3c (which toggles off
        // AND consumes the key, so a W/S trigger doesn't leak). Without this, a Background run whose trigger
        // is W/S can't be chord-toggled-off while unfocused (step 2's cancel is focus-gated and returns
        // before 3c). Bare W/S (no modifier) still cancels + passes through unchanged.
        if (active && isKeyDown && (freshW || freshS)
            && !(vkCode == _autoRunSnapshotTriggerVk && IsTriggerModifierDown(_autoRunSnapshotModifier)))
        {
            if (!_autoRunIsBackground || ForegroundMatchesAutoRunTarget())
            {
                ReleaseAutoRunState(includeBackground: true);
                if (IsDebugEnabled) LogDebug($"AutoRun CANCEL on fresh physical down: vk=0x{vkCode:X2}");
            }
            return false;
        }

        // 2b) Sprint stamina toggle (Hold mode only). The sprint key NEVER cancels auto-run; instead,
        //     while a run with a sustained sprint is active, a FRESH physical sprint press toggles that
        //     sprint OFF/ON (release to let in-game stamina recharge, press again to re-engage) and the
        //     key is CONSUMED so the game never sees a raw sprint tap. A Background run acts on the sprint
        //     key only while the game is focused (like cancel); otherwise it passes through to the focused
        //     app. On auto-run exit, any still-held sprint is released by ReleaseAutoRunState (no stuck
        //     sprint). Auto-run's W is untouched throughout — only the sprint hold toggles.
        //     If the sprint key is ALSO this run's trigger key (a shared-key config), the TRIGGER wins:
        //     the sprint-toggle is skipped so the chord still toggles auto-run OFF (codex tweak #1).
        if (active && _autoRunSprintToggleable && sprintVk != 0 && vkCode == sprintVk
            && vkCode != VK_W && vkCode != VK_S && vkCode != _autoRunSnapshotTriggerVk)
        {
            if (!_autoRunIsBackground || ForegroundMatchesAutoRunTarget())
            {
                if (isKeyDown && freshSprint)
                {
                    ToggleAutoRunSprintHold();
                }
                return true; // consume the sprint key's down / repeat / up
            }
        }

        // 3) Trigger chord. The consumed-press cleanup (3a) and the fresh-edge keyup latch (3b) are
        //    UNGATED (release-style, I3): once a chord press is in flight, its repeats + matching keyup
        //    must be swallowed and the latches cleared even if Advanced Mode was turned off or the
        //    profile changed (to a different/absent trigger) mid-press — otherwise a stray trigger key
        //    leaks to the game (codex P3a #1) or a stale latch blocks a later activation.

        // 3a. Suppress + clean up a chord press that already fired, matched by the STORED consumed VK
        //     (the configured trigger key may have changed since the press).
        if (_autoRunConsumedTriggerVk != 0 && vkCode == _autoRunConsumedTriggerVk)
        {
            if (isKeyUp)
            {
                _autoRunConsumedTriggerVk = 0;
                if (_triggerKeyDownVk == vkCode) _triggerKeyDownVk = 0;
                return true; // swallow the matching keyup so no dangling trigger keyup leaks
            }
            return isKeyDown; // swallow auto-repeats
        }

        // 3b. A non-consumed trigger key's keyup clears the fresh-edge latch UNGATED (so a stale latch
        //     can't block a later activation) and passes through to the game.
        if (isKeyUp && _triggerKeyDownVk == vkCode)
        {
            _triggerKeyDownVk = 0;
            return false;
        }

        // 3c. A run is ACTIVE → the only chord action is TOGGLE-OFF, matched by the SNAPSHOT trigger so
        //     it works even when _activeProfile is null/changed (essential for a decoupled Background
        //     run that outlives its profile). Releases whichever transport is active.
        if (_autoRunActive)
        {
            if (_autoRunSnapshotTriggerVk == 0 || vkCode != _autoRunSnapshotTriggerVk || !isKeyDown)
            {
                return false;
            }
            if (_triggerKeyDownVk == vkCode)
            {
                return false; // auto-repeat
            }
            _triggerKeyDownVk = vkCode;
            if (!IsTriggerModifierDown(_autoRunSnapshotModifier))
            {
                // E1: shared-key config — this trigger key is ALSO the run's (Hold) sprint key. Without a
                // modifier it is not a toggle-off chord, but it must NOT leak to the game: a raw sprint tap
                // would release in-game sprint while we still track it as injected (state desync). Consume
                // BOTH edges (3a swallows the repeats + matching keyup via the consumed latch). The
                // stamina-toggle-via-tap is intentionally unavailable for a shared-key config; the
                // modifier+key chord still toggles the run off. Distinct trigger keys pass through unchanged.
                if (_autoRunSprintToggleable && sprintVk != 0 && sprintVk == vkCode && vkCode != VK_W && vkCode != VK_S)
                {
                    _autoRunConsumedTriggerVk = vkCode;
                    return true;
                }
                return false;
            }

            ReleaseAutoRunState(includeBackground: true);
            _autoRunConsumedTriggerVk = vkCode;
            return true;
        }

        // 3d. No run active → TOGGLE-ON, gated on the feature being usable, matched by the LIVE profile
        //     trigger. The game is foreground here (you press the chord while playing), so _activeProfile
        //     and its exe are valid to snapshot for a Background run.
        var profile = _activeProfile; // benign lock-free read (like HandleRightClickHoldBreathDown)
        if (!_advancedModeEnabled || profile is null || !profile.AutoRun.IsEnabled)
        {
            return false;
        }

        var settings = profile.AutoRun;
        var triggerVk = KeyInteropUtilities.ToVirtualKey(settings.TriggerKey);
        if (triggerVk == 0 || vkCode != triggerVk || !isKeyDown)
        {
            return false;
        }

        // Trigger keydown + feature usable. Only a FRESH down (not an auto-repeat) forms a chord.
        if (_triggerKeyDownVk == vkCode)
        {
            return false; // auto-repeat — pass through
        }
        _triggerKeyDownVk = vkCode;

        // Require the single side-agnostic modifier physically down.
        if (!IsTriggerModifierDown(settings.TriggerModifier))
        {
            return false; // trigger key without its modifier — pass through to the game
        }

        // Chord — activate; consume this press (repeats + keyup handled by 3a) ONLY if the run actually
        // started (A1). If activation failed closed (foreground not the game), do NOT swallow the chord —
        // pass it through (return false) so it isn't eaten in the wrong window. The fresh-edge latch
        // (_triggerKeyDownVk) is cleared ungated by 3b on the trigger's keyup either way.
        if (ActivateAutoRun(settings, profile))
        {
            _autoRunConsumedTriggerVk = vkCode;
            return true;
        }
        return false;
    }

    // Single side-agnostic modifier check (VK_CONTROL/VK_MENU/VK_SHIFT report either side; Windows has
    // no combined VK, so check both). Combined modifiers are intentionally unsupported (AutoRunSettings).
    private static bool IsTriggerModifierDown(System.Windows.Input.ModifierKeys modifier)
    {
        return modifier switch
        {
            System.Windows.Input.ModifierKeys.Control => (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0, // VK_CONTROL
            System.Windows.Input.ModifierKeys.Alt => (NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0,     // VK_MENU
            System.Windows.Input.ModifierKeys.Shift => (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0,   // VK_SHIFT
            System.Windows.Input.ModifierKeys.Windows =>
                (NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0 || // VK_LWIN
                (NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) != 0,   // VK_RWIN
            _ => false
        };
    }

    // Under _autoRunLock, _isRunning re-checked. Seeds the physical flags from GetAsyncKeyState (ground
    // truth) so a held-through-activation W/S/sprint is seen as already-down (no false cancel) and a
    // genuinely-up key is fresh on its next press (cancels). Enqueue-only (I5): only the injector and
    // GetAsyncKeyState run under the lock — no SendInput, no nested subsystem lock.
    // Returns true iff a run was started (chord consumed). Returns false on any abort (service stopped,
    // already active, foreground not the game, or a refused Background W post) — the caller must NOT
    // consume the chord on false, so it passes through instead of being swallowed in the wrong window.
    private bool ActivateAutoRun(AutoRunSettings settings, Profile profile)
    {
        lock (_autoRunLock)
        {
            if (!_isRunning || _autoRunActive)
            {
                return false;
            }

            // A1: FAIL CLOSED on foreground ownership — for BOTH transports — with NO Process.GetProcessById
            // on the hook thread. _activeProfile lags real foreground during ProfileActivationService's
            // color work, so a chord pressed right after an alt-tab could otherwise activate and inject W
            // into the NEW foreground app. Confirm against the off-hook foreground snapshot: the LIVE
            // foreground window must still equal the snapshot HWND, the snapshot PID must be resolved, and
            // the snapshot exe must be THIS profile's game. Any mismatch (incl. a stale/absent snapshot)
            // aborts — never a Foreground fallback into the wrong window. Only cheap non-blocking calls.
            var hwnd = NativeMethods.GetForegroundWindow();
            var exe = profile.NormalizedExecutable;
            var snapshot = _foregroundIdentity;
            // Resolve the LIVE owning PID of the foreground window and require it to equal the snapshot PID
            // (codex sol/xhigh #3): matching the HWND alone is not enough — under HWND reuse the same handle
            // could now belong to a DIFFERENT process, and a Background run would then target that foreign
            // process. GetWindowThreadProcessId is cheap/non-blocking (safe on the hook thread).
            NativeMethods.GetWindowThreadProcessId(hwnd, out var livePid);
            if (snapshot is null || hwnd == IntPtr.Zero || hwnd != snapshot.Hwnd || snapshot.Pid == 0
                || livePid == 0 || livePid != snapshot.Pid
                || string.IsNullOrEmpty(exe)
                || !string.Equals(snapshot.Exe, exe, StringComparison.OrdinalIgnoreCase))
            {
                LogDebug("AutoRun: foreground not confirmed as the profile game (cached identity / live PID); activation aborted (fail closed)");
                return false;
            }

            // Transport: Background posts key messages to the game's HWND (survives alt-tab) via a
            // non-blocking PostMessage; Foreground injects via the shared FIFO injector.
            var background = settings.SendMode == AutoRunSendMode.Background;
            if (background)
            {
                // Match AutoHotkey ControlSend's blank-control target: post to the window's TOPMOST CHILD
                // control (the keyboard-input surface for many games, incl. GZW) rather than the top-level
                // frame — but only if that child is in the SAME PROCESS (compare PIDs, not just the exe
                // name: a CEF/helper child can share the exe name under a DIFFERENT PID). Focus-independent.
                // framePid is the just-verified livePid (== snapshot.Pid).
                var framePid = livePid;
                var child = NativeMethods.GetWindow(hwnd, NativeMethods.GW_CHILD);
                var childSameProcess = false;
                if (child != IntPtr.Zero && framePid != 0)
                {
                    NativeMethods.GetWindowThreadProcessId(child, out var childPid);
                    childSameProcess = childPid == framePid;
                }
                _autoRunTargetHwnd = childSameProcess ? child : hwnd;
                _autoRunTargetExe = exe;
                _autoRunTargetPid = framePid;
                LogDebug($"AutoRun Background target=0x{_autoRunTargetHwnd.ToInt64():X} (child={childSameProcess}), exe={exe}");
            }
            _autoRunIsBackground = background;

            // Snapshot the sprint key for the run's lifetime (a mid-run UI edit can't retarget it).
            _autoRunSprintKey = settings.SprintKey;
            // In Hold mode the sprint key becomes the sustained-sprint stamina toggle for this run.
            _autoRunSprintToggleable = settings.SprintEnabled && settings.SprintMode == SprintActivation.Hold;
            // Intent to hold sprint for the run (Hold mode). Current (_autoRunSprintInjected) is set false
            // here and flipped true only when a DOWN actually lands; a fresh activation is a fresh epoch.
            _autoRunSprintIntendedHeld = _autoRunSprintToggleable;
            _autoRunSprintInjected = false;

            // Snapshot the trigger for TOGGLE-OFF so it works even when _activeProfile is gone (bg run).
            _autoRunSnapshotTriggerVk = KeyInteropUtilities.ToVirtualKey(settings.TriggerKey);
            _autoRunSnapshotModifier = settings.TriggerModifier;

            // Re-seed physical down-state from ground truth (codex R1 #2). Hook-thread-only fields
            // written here on the hook thread (ActivateAutoRun is only ever reached from the hook).
            // Both transports cancel on a fresh physical edge, so both need this.
            _wPhysicallyDown = (NativeMethods.GetAsyncKeyState(VK_W) & 0x8000) != 0;
            _sPhysicallyDown = (NativeMethods.GetAsyncKeyState(VK_S) & 0x8000) != 0;
            var sprintVk = KeyInteropUtilities.ToVirtualKey(_autoRunSprintKey);
            _sprintPhysicallyDown = sprintVk != 0 && (NativeMethods.GetAsyncKeyState(sprintVk) & 0x8000) != 0;

            // W-down: post (Background) or enqueue (Foreground) and record it (I2). If physical W is also
            // down, both are down (harmless); the delivered W is what survives the physical release.
            // Background: if the target died between capture-validation and now, the post is REFUSED —
            // abort activation and clear state so no zombie active run is left for ReleaseAllState to
            // skip (per-post validation failure must clear state, §11.5; codex P3b #1).
            // A2: a FOREGROUND run opens a new injector-guard epoch and stamps its queued DOWNs with that
            // generation + the profile exe, so a still-queued W/sprint DOWN can't drain into a window you
            // alt-tabbed to (the injector re-checks both at fire time). Background posts (not enqueues) and
            // needs no stamp. gen==0 leaves the DOWN unguarded (hold-breath/anti-afk behaviour).
            long gen = 0;
            string? guardExe = null;
            if (!background)
            {
                gen = Interlocked.Increment(ref _autoRunInjectionGeneration);
                _activeAutoRunInjectionGeneration = gen;
                guardExe = exe;
            }
            _autoRunForegroundGuardExe = guardExe; // used by a later Foreground sprint re-engage (null for Background)

            _autoRunMoveInjected = true;
            if (!PostOrEnqueueAutoRunDown(Key.W, background, gen, guardExe))
            {
                _autoRunMoveInjected = false;
                _autoRunIsBackground = false;
                _autoRunTargetHwnd = IntPtr.Zero;
                _autoRunTargetExe = null;
                _autoRunTargetPid = 0;
                _autoRunSprintIntendedHeld = false; // no run → no sprint intent/current
                _autoRunSprintInjected = false;
                LogDebug("AutoRun Background: target window invalid at W post; activation aborted");
                return false; // _autoRunActive stays false — no zombie run
            }

            if (settings.SprintEnabled)
            {
                if (background)
                {
                    // Background sprint is posted by the dedicated thread AFTER a short delay — NOT here.
                    // Posting the sprint key back-to-back with W is read "too soon" by GZW and skipped
                    // (user-confirmed in-game). The thread does: hold W → wait 40-60ms → sprint DOWN, and
                    // for Press/toggle → DOWN → 40-60ms → UP (one clean tap that toggles in-game sprint).
                    // _autoRunSprintToggleable already records Hold vs Press for this run.
                    _autoRunSprintEnabled = true;
                }
                else if (settings.SprintMode == SprintActivation.Hold)
                {
                    // Foreground Hold: enqueue shift-down (held) via the injector; record for release.
                    if (PostOrEnqueueAutoRunDown(_autoRunSprintKey, background, gen, guardExe))
                    {
                        _autoRunSprintInjected = true;
                        _autoRunSprintInjectedKey = _autoRunSprintKey;
                    }
                }
                else
                {
                    // Foreground Press: a self-releasing tap (DOWN + UP-with-presleep), not marked held —
                    // same shape as the Toggle hold-breath tap, so Stop()'s drain runs the UP.
                    var rng = _random.Value!;
                    var warmupCalls = rng.Next(RNG_WARMUP_MIN_CALLS, RNG_WARMUP_MAX_CALLS + 1);
                    for (int i = 0; i < warmupCalls; i++) rng.Next();
                    var duration = rng.Next(HOLD_BREATH_TAP_DURATION_MIN_MS, HOLD_BREATH_TAP_DURATION_MAX_MS + 1);
                    EnqueueHoldBreathInjection(new HoldBreathInjection(_autoRunSprintKey, IsDown: true, PreSleepMs: 0, AutoRunGeneration: gen, ExpectedForegroundExe: guardExe));
                    EnqueueHoldBreathInjection(new HoldBreathInjection(_autoRunSprintKey, IsDown: false, PreSleepMs: duration));
                }
            }

            _autoRunActive = true; // volatile write LAST — publishes records + snapshots to readers

            // Background: start the dedicated thread (see BackgroundInputLoop). It does the delayed sprint
            // activation (hold W → wait 40-60ms → press sprint) then re-posts W every tick so the run
            // survives the game clearing its input on focus-loss (a single focused down does not persist
            // through alt-tab). Started AFTER _autoRunActive so it sees the published run; Thread.Start is
            // non-blocking and the thread waits on _autoRunLock (held here) before its first action.
            // Overwrites any stale ref from a prior run. Foreground uses the injector and needs no thread.
            if (background)
            {
                _backgroundInputRun = true;
                var bgThread = new Thread(BackgroundInputLoop) { IsBackground = true, Name = "sWinBgInput" };
                _backgroundInputThread = bgThread;
                bgThread.Start();
            }
            if (IsDebugEnabled) LogDebug($"AutoRun ACTIVATED: transport={(background ? "Background" : "Foreground")}, sprintEnabled={settings.SprintEnabled}, mode={settings.SprintMode}, sprintKey={_autoRunSprintKey}");
            return true; // run started — caller consumes the chord
        }
    }

    // Routes a held-key DOWN to the active transport. Caller holds _autoRunLock. Returns whether the
    // key was delivered: Foreground always true (enqueue can't refuse); Background = the post's
    // validation result (false if the target window died — the caller must not record/keep the run).
    private bool PostOrEnqueueAutoRunDown(Key key, bool background, long gen, string? guardExe)
    {
        if (background)
        {
            return PostAutoRunKey(key, isDown: true);
        }

        // Foreground DOWN carries the A2 foreground-guard (gen + expected exe); UPs are never guarded.
        EnqueueHoldBreathInjection(new HoldBreathInjection(key, IsDown: true, PreSleepMs: 0, AutoRunGeneration: gen, ExpectedForegroundExe: guardExe));
        return true;
    }

    // Unconditional release (I3) of the active run's W/sprint. includeBackground gates the DECOUPLING
    // (§11.6): ordinary teardown (ReleaseAllState from profile/session switch + watchdog reinstall, and
    // the eager focus-loss release) passes false and MUST skip a Background run — that run is meant to
    // outlive profile churn. The hard-teardown sites (Stop / OnSessionSwitch / Advanced-off), an
    // explicit chord toggle-off, and a focused physical cancel pass true. Enqueue-only (Foreground) or
    // non-blocking PostMessage (Background), so safe on the hook / pool / dispatcher threads.
    private void ReleaseAutoRunState(bool includeBackground)
    {
        lock (_autoRunLock)
        {
            if (!_autoRunActive)
            {
                return;
            }

            if (_autoRunIsBackground && !includeBackground)
            {
                return; // decoupled: a Background run ignores ordinary profile/hook churn
            }

            // Terminal release of an active run → open a new injector-guard epoch (A2) so any still-queued
            // guarded Foreground DOWN is invalidated and skipped by the injector. NOT reached for a
            // decoupled Background run left active by includeBackground:false (returned above).
            Interlocked.Increment(ref _autoRunInjectionGeneration);

            // Release the sprint UP if it is CURRENT or merely INTENDED-held (a Background Hold sprint whose
            // current bit was cleared on focus-loss still has intent true — post an UP so nothing is left
            // held even in that epoch). Use the injected key if current, else the run's snapshot key.
            bool releaseSprint = _autoRunSprintInjected || _autoRunSprintIntendedHeld;
            var sprintUpKey = _autoRunSprintInjected ? _autoRunSprintInjectedKey : _autoRunSprintKey;

            if (_autoRunIsBackground)
            {
                // Signal the Background thread to stop BEFORE posting the UPs. We do NOT join here: a chord
                // toggle-off reaches this on the hook thread and I5 forbids blocking it. Clearing the run
                // flag makes the thread exit on its next tick (it also re-checks _autoRunActive, which we
                // set false below, both under _autoRunLock which the thread takes each tick), so no re-posted
                // DOWN can land after the UP. The ref is left set for a Stop/Dispose off-hook join or the
                // next activation to overwrite. Stop/Dispose additionally join via JoinBackgroundInputThread.
                _backgroundInputRun = false;

                // Best-effort post the UPs (each re-validates the target first). PostMessage is
                // non-blocking, so this is safe under the lock. If the window is gone the UP can't
                // land — the in-game "held" state is the documented Background residual (OS input
                // stays clean; nothing is held system-wide).
                if (_autoRunMoveInjected) PostAutoRunKey(Key.W, isDown: false);
                if (releaseSprint) PostAutoRunKey(sprintUpKey, isDown: false);
                _autoRunTargetHwnd = IntPtr.Zero;
                _autoRunTargetExe = null;
                _autoRunTargetPid = 0;
            }
            else
            {
                if (_autoRunMoveInjected) EnqueueHoldBreathInjection(new HoldBreathInjection(Key.W, IsDown: false, PreSleepMs: 0));
                if (releaseSprint) EnqueueHoldBreathInjection(new HoldBreathInjection(sprintUpKey, IsDown: false, PreSleepMs: 0));
            }

            _autoRunMoveInjected = false;
            _autoRunSprintInjected = false;
            _autoRunSprintIntendedHeld = false;
            _autoRunSprintToggleable = false;
            _autoRunSprintEnabled = false;
            _autoRunSprintKey = Key.None;
            _autoRunSprintInjectedKey = Key.None;
            _autoRunIsBackground = false;
            _autoRunForegroundGuardExe = null;
            _autoRunActive = false;
        }
    }

    // Toggles the sustained sprint of an active Hold-mode run for stamina management: release it if
    // held, else re-engage it. Routes by transport exactly like ActivateAutoRun/ReleaseAutoRunState
    // (enqueue for Foreground, non-blocking PostMessage for Background), under _autoRunLock; re-checks
    // _autoRunActive (the caller read it lock-free). Auto-run's W is untouched — only sprint toggles,
    // and ReleaseAutoRunState still releases whatever sprint is held on exit (no stuck sprint).
    private void ToggleAutoRunSprintHold()
    {
        lock (_autoRunLock)
        {
            if (!_autoRunActive)
            {
                return;
            }

            if (_autoRunSprintInjected)
            {
                // Sprint currently held → turn it OFF (in-game stamina recharges; W keeps running). Intent
                // goes false (the user chose to stop). CURRENT clears only once the UP actually lands: a
                // transient Background PostMessage failure keeps current=true so terminal release still
                // retries the UP (no stuck sprint). Foreground enqueue cannot fail (codex sol/xhigh tweak).
                _autoRunSprintIntendedHeld = false;
                if (_autoRunIsBackground)
                {
                    if (PostAutoRunKey(_autoRunSprintInjectedKey, isDown: false))
                    {
                        _autoRunSprintInjected = false;
                    }
                }
                else
                {
                    EnqueueHoldBreathInjection(new HoldBreathInjection(_autoRunSprintInjectedKey, IsDown: false, PreSleepMs: 0));
                    _autoRunSprintInjected = false;
                }
                if (IsDebugEnabled) LogDebug("AutoRun sprint toggled OFF (stamina)");
            }
            else
            {
                // Sprint currently off → engage it. Intent goes true unconditionally (the user wants sprint,
                // so a later focus-regain also re-engages); current goes true only if the DOWN actually
                // lands (Foreground always; Background could refuse if the target window died). This branch
                // also handles the first physical press after an alt-tab (intent=true, current=false): it
                // re-engages instead of the old stale "release + eaten" behaviour.
                _autoRunSprintIntendedHeld = true;
                if (_autoRunIsBackground)
                {
                    if (PostAutoRunKey(_autoRunSprintKey, isDown: true))
                    {
                        _autoRunSprintInjected = true;
                        _autoRunSprintInjectedKey = _autoRunSprintKey;
                    }
                }
                else
                {
                    // Foreground sprint re-engage: stamp with the run's active epoch + exe (A2) so a stale
                    // re-engage DOWN is skipped if the run ended / focus left before it drains.
                    EnqueueHoldBreathInjection(new HoldBreathInjection(_autoRunSprintKey, IsDown: true, PreSleepMs: 0, AutoRunGeneration: _activeAutoRunInjectionGeneration, ExpectedForegroundExe: _autoRunForegroundGuardExe));
                    _autoRunSprintInjected = true;
                    _autoRunSprintInjectedKey = _autoRunSprintKey;
                }
                if (IsDebugEnabled) LogDebug("AutoRun sprint toggled ON");
            }
        }
    }

    // Eager release when foreground LEAVES the active profile — called by ProfileActivationService
    // BEFORE its color work, so a held Foreground W can't briefly leak into the incoming window during
    // that window (the profile switch also releases via ReleaseAllState, but only after color work).
    // includeBackground:false — a Background run is SUPPOSED to keep going while unfocused (§11.6).
    public void ReleaseForegroundAutoRun()
    {
        ReleaseAutoRunState(includeBackground: false);
    }

    // A1: off-hook publish of the foreground identity (see field). Called by ProfileActivationService on
    // every foreground change, on the watcher thread — never the hook thread. Atomic reference swap.
    public void SetForegroundIdentity(IntPtr windowHandle, uint processId, string? normalizedExecutable)
    {
        _foregroundIdentity = new ForegroundIdentitySnapshot(windowHandle, processId, normalizedExecutable);
    }

    // Posts one WM_KEYDOWN/WM_KEYUP to the Background target after re-validating it via the CHEAP PID
    // compare (BackgroundTargetValid: GetWindowThreadProcessId == snapshot PID). This catches BOTH "window
    // gone" (pid 0) AND "handle reused by another process" (pid mismatch) with no Process.GetProcessById —
    // so it is safe on the HOOK thread (activation / toggle-off / cancel), where the heavier
    // WindowBelongsToExe could stall past LowLevelHooksTimeout (B2). On validation failure it posts
    // nothing (a same-process window swap remains the documented residual). PostMessage is
    // async/non-blocking, so this never stalls on a foreign hook. Caller holds _autoRunLock. Returns
    // whether the target was still valid.
    private bool PostAutoRunKey(Key key, bool isDown)
    {
        if (!BackgroundTargetValid(_autoRunTargetHwnd))
        {
            return false;
        }

        var posted = PostKeyToWindow(_autoRunTargetHwnd, key, isDown, repeat: false);
        if (IsDebugEnabled) LogDebug($"AutoRun Background post {(isDown ? "DOWN" : "UP")}: {key} to hwnd=0x{_autoRunTargetHwnd.ToInt64():X} posted={posted}");
        return posted;
    }

    // Posts one WM_KEY* to hwnd using AutoHotkey ControlSend's technique: attach our (posting) thread to
    // the target window's input thread around the post — the one thing ControlSend does that a bare
    // PostMessage does not, and WITHOUT which games like GZW ignore a posted WM_KEYDOWN. AttachThreadInput
    // is non-blocking and we detach right after the post, so the shared-input-queue coupling lasts only
    // microseconds. Guards: skip if the target thread is unknown or is our own thread, and never attach
    // to a hung window's queue (IsHungAppWindow is a non-blocking status check, not a SendMessage). A
    // failed attach still posts (best-effort). Runs under _autoRunLock but stays non-blocking (I5 holds).
    // `repeat` sets the previous-state bit for a sustained-key re-post (see BackgroundInputLoop).
    private bool PostKeyToWindow(IntPtr hwnd, Key key, bool isDown, bool repeat)
    {
        var vk = KeyInteropUtilities.ToVirtualKey(key);
        if (vk == 0)
        {
            return true;
        }

        var scan = NativeMethods.MapVirtualKey((uint)vk, 0);
        // Alt (VK_MENU / L/R) and F10 are SYSTEM keys — Windows delivers them as WM_SYSKEY*; a game reading
        // an Alt sprint off WM_SYSKEYDOWN would never see a WM_KEYDOWN (D1). We only ever post a SINGLE key
        // (never a non-Alt key while holding Alt), so the WM_SYSKEY* context bit (29) is always clear here.
        var isSysKey = vk is 0x12 or 0xA4 or 0xA5 or 0x79; // VK_MENU, VK_LMENU, VK_RMENU, VK_F10
        var lParam = BuildKeyLParam(scan, isDown, IsExtendedKey(key), repeat, altContext: false);
        var msg = (uint)(isDown
            ? (isSysKey ? NativeMethods.WM_SYSKEYDOWN : NativeMethods.WM_KEYDOWN)
            : (isSysKey ? NativeMethods.WM_SYSKEYUP : NativeMethods.WM_KEYUP));

        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var thisThread = NativeMethods.GetCurrentThreadId();
        // Confine AttachThreadInput to the dedicated Background thread (B1(b)): attaching couples the
        // caller's input queue to the game's, which must NEVER happen on the hook/dispatcher thread (a
        // hung game could then stall hook dispatch). Hook-thread posts (activation first-down, toggle-off /
        // cancel UPs, sprint toggle) go BARE and best-effort; the bg thread's attached ~35ms repost
        // re-asserts a held key. Only the bg thread attaches (and thus only it touches the key-state table).
        var onBackgroundThread = ReferenceEquals(Thread.CurrentThread, _backgroundInputThread);
        var willAttach = onBackgroundThread && targetThread != 0 && targetThread != thisThread && !NativeMethods.IsHungAppWindow(hwnd);

        // AttachThreadInput RESETS the calling thread's GetKeyState/GetKeyboardState table (per MSDN).
        // Snapshot it first and restore it after, so a Background post can't corrupt a same-thread
        // key-state reader — notably CapsLock Hold mode's GetKeyState(VK_CAPITAL) on the dispatcher/hook
        // thread (codex). The reset window is confined to this method, which completes on the thread
        // before any other key-state reader can run.
        byte[]? savedKeyState = null;
        if (willAttach)
        {
            savedKeyState = new byte[256];
            if (!NativeMethods.GetKeyboardState(savedKeyState))
            {
                // No snapshot → do NOT attach: attach would reset a key-state table we can't restore,
                // corrupting a same-thread reader (CapsLock-Hold). A bare best-effort post is safer. B3.
                savedKeyState = null;
                willAttach = false;
            }
        }

        // Exception-safe (B3): detach and restore ALWAYS run — even on an asynchronous exception between
        // attach and post — so we never leak the UI/game input-queue attachment or leave the key-state
        // table reset.
        bool attached = false;
        try
        {
            attached = willAttach && NativeMethods.AttachThreadInput(thisThread, targetThread, true);
            return NativeMethods.PostMessage(hwnd, msg, (IntPtr)vk, lParam);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(thisThread, targetThread, false);
            }
            if (savedKeyState != null)
            {
                NativeMethods.SetKeyboardState(savedKeyState);
            }
        }
    }

    // Dedicated Background input thread. First does the one-time delayed sprint activation (user spec: hold
    // W → wait 40-60ms → press the sprint key; posting it back-to-back with W is read "too soon" by GZW and
    // skipped), then re-posts W every AUTO_RUN_REPEAT_MS. W (movement) is STATE-based and the game clears
    // input on focus-loss, so a single focused down does not persist through alt-tab — re-posting keeps the
    // run alive. Sprint is NOT re-posted PER TICK (a per-tick modifier re-post reads as a fresh
    // "dash"/fast-sprint, and SetKeyboardState is ignored by GZW). Instead a Hold sprint is re-asserted
    // ONCE on each focus-REGAIN transition (W → BG_SPRINT_REENGAGE_QUIET_MS quiet window → one sprint DOWN),
    // gated on intent so it survives alt-tab without the dash; current is cleared on focus-loss so the
    // intent/current split stays accurate. A Press/toggle sprint persists as the game's OWN toggle state
    // with no re-post — that is the working background-sprint path for GZW. (Regain re-assert needs in-game
    // verification per game; see BG_SPRINT_REENGAGE_QUIET_MS.)
    // Concurrency: every tick's shared-state read + posts happen under _autoRunLock (serializes with
    // activate/release, like the old timer); Sleeps are OUTSIDE the lock. The thread exits when
    // _backgroundInputRun is cleared, the run ends, or it is no longer the current run's thread
    // (_backgroundInputThread identity — lets a stale thread bow out if a new run started).
    private void BackgroundInputLoop()
    {
        try
        {
            // One-time: hold W (posted at activation) → wait → press sprint. Safe to block here (dedicated
            // thread, never the hook thread).
            DoDelayedBackgroundSprintActivation();

            // Focus-transition state, OWNED by this loop (single thread → no cross-thread field needed).
            // Activation fails closed unless the game was foreground (A1), so the run starts focused.
            bool wasTargetForeground = true;
            bool sprintReengagePending = false;
            int sprintReengageDueTick = 0;

            while (true)
            {
                bool stop = false;
                lock (_autoRunLock)
                {
                    if (!_backgroundInputRun || _backgroundInputThread != Thread.CurrentThread
                        || !_autoRunActive || !_autoRunIsBackground || !_isRunning || _disposed)
                    {
                        stop = true;
                    }
                    else if (!BackgroundTargetValid(_autoRunTargetHwnd))
                    {
                        // Target window died OR its HWND was reused by ANOTHER process (pid mismatch): do NOT
                        // keep spinning or post into a reused/foreign window — hard-release the run (a per-post
                        // validation failure is a teardown path, §11.5). Its UPs re-validate via
                        // WindowBelongsToExe, so nothing lands on a foreign/dead window. codex R1 #1.
                        if (IsDebugEnabled) LogDebug("AutoRun Background: target invalid (dead/reused pid) — hard-releasing the run");
                        ReleaseAutoRunState(includeBackground: true); // re-entrant on _autoRunLock (held here)
                        stop = true;
                    }
                    else
                    {
                        bool foreground = ForegroundIsAutoRunTargetProcess();

                        if (wasTargetForeground && !foreground)
                        {
                            // Focus LOST: the game clears its input, so a Hold sprint is no longer CURRENT
                            // (intent is preserved). Cancel any pending re-engage. W keeps being re-posted
                            // so movement resumes on return.
                            if (_autoRunSprintInjected)
                            {
                                _autoRunSprintInjected = false;
                                if (IsDebugEnabled) LogDebug("AutoRun Background: focus lost — sprint no longer current (intent preserved)");
                            }
                            sprintReengagePending = false;
                        }
                        else if (!wasTargetForeground && foreground
                                 && _autoRunSprintToggleable && _autoRunSprintIntendedHeld && !_autoRunSprintInjected)
                        {
                            // Focus REGAINED and sprint should be held but isn't current → re-assert W once,
                            // then arm ONE sprint DOWN after a fixed quiet window (mirrors activation timing;
                            // regain-only avoids a fresh-Shift "dash"). W re-posts are suppressed until due.
                            if (_autoRunMoveInjected) PostKeyToWindow(_autoRunTargetHwnd, Key.W, isDown: true, repeat: true);
                            sprintReengagePending = true;
                            sprintReengageDueTick = unchecked(Environment.TickCount + BG_SPRINT_REENGAGE_QUIET_MS);
                            if (IsDebugEnabled) LogDebug("AutoRun Background: focus regained — sprint re-engage armed");
                        }
                        wasTargetForeground = foreground;

                        if (sprintReengagePending && unchecked(Environment.TickCount - sprintReengageDueTick) >= 0)
                        {
                            // Due: post exactly one sprint DOWN, re-checked under this held lock. Clear the
                            // pending attempt whether it lands or not (no per-tick retries). A physical
                            // stamina toggle that engaged sprint first is seen here via _autoRunSprintInjected.
                            sprintReengagePending = false;
                            if (_autoRunSprintToggleable && _autoRunSprintIntendedHeld && !_autoRunSprintInjected
                                && foreground && BackgroundTargetValid(_autoRunTargetHwnd))
                            {
                                if (PostAutoRunKey(_autoRunSprintKey, isDown: true))
                                {
                                    _autoRunSprintInjected = true;
                                    _autoRunSprintInjectedKey = _autoRunSprintKey;
                                    if (IsDebugEnabled) LogDebug("AutoRun Background: sprint re-engaged on focus regain");
                                }
                            }
                        }
                        else if (!sprintReengagePending)
                        {
                            // Movement: re-post W each tick (suppressed only during the quiet window above).
                            bool wPosted = _autoRunMoveInjected && PostKeyToWindow(_autoRunTargetHwnd, Key.W, isDown: true, repeat: true);
                            if (IsDebugEnabled && (++_autoRunRepostTicks % 28) == 0)
                            {
                                LogDebug($"AutoRun Background heartbeat: W posted={wPosted}, foreground={foreground}, sprintInjected={_autoRunSprintInjected}");
                            }
                        }
                    }
                }
                if (stop) break;

                // Normally tick every 35ms; while a re-engage is pending, sleep only until it is due so the
                // 50ms quiet window is honoured without busy-waiting. Sleep is OUTSIDE the lock.
                int sleepMs = AUTO_RUN_REPEAT_MS;
                if (sprintReengagePending)
                {
                    int remaining = unchecked(sprintReengageDueTick - Environment.TickCount);
                    sleepMs = Math.Max(1, Math.Min(AUTO_RUN_REPEAT_MS, remaining));
                }
                Thread.Sleep(sleepMs);
            }
        }
        catch (Exception ex)
        {
            if (IsDebugEnabled) LogDebug($"BackgroundInputLoop exception: {ex}");

            // F2: an exception must NOT leave the run marked active (a zombie only clearable by
            // toggle-off/Stop/session/Advanced-off, still "moving" per Anti-AFK's guard 4). Terminalize
            // and post the paired UPs while STILL HOLDING _autoRunLock (codex): PostMessage is non-blocking
            // and this is the dedicated bg thread (I5 holds), and serializing the UP under the lock stops a
            // replacement activation from posting its W-DOWN first and having our stale UP cancel it. We do
            // NOT call ReleaseAutoRunState here (its native posts could throw before it clears state).
            lock (_autoRunLock)
            {
                if (_backgroundInputThread == Thread.CurrentThread && _autoRunActive && _autoRunIsBackground)
                {
                    var hwnd = _autoRunTargetHwnd;
                    var releaseW = _autoRunMoveInjected;
                    // Release sprint if CURRENT or INTENDED-held (same predicate as ReleaseAutoRunState), using
                    // the injected key if current, else the run's snapshot key.
                    var releaseSprint = _autoRunSprintInjected || _autoRunSprintIntendedHeld;
                    var sprintKey = _autoRunSprintInjected ? _autoRunSprintInjectedKey : _autoRunSprintKey;

                    // Terminal release of an active run → invalidate any in-flight guarded injector DOWNs (A2).
                    Interlocked.Increment(ref _autoRunInjectionGeneration);
                    _backgroundInputRun = false;
                    _autoRunMoveInjected = false;
                    _autoRunSprintInjected = false;
                    _autoRunSprintIntendedHeld = false;
                    _autoRunSprintToggleable = false;
                    _autoRunSprintEnabled = false;
                    _autoRunSprintKey = Key.None;
                    _autoRunSprintInjectedKey = Key.None;
                    _autoRunIsBackground = false;
                    _autoRunForegroundGuardExe = null;
                    _autoRunActive = false;
                    _autoRunTargetHwnd = IntPtr.Zero;
                    _autoRunTargetExe = null;
                    _autoRunTargetPid = 0;

                    // Best-effort paired UPs UNDER the lock (non-blocking). PostKeyToWindow is exception-safe
                    // (B3); wrap anyway so a second failure can't escape a thread that is exiting.
                    try
                    {
                        if (hwnd != IntPtr.Zero && releaseW) PostKeyToWindow(hwnd, Key.W, isDown: false, repeat: false);
                        if (hwnd != IntPtr.Zero && releaseSprint) PostKeyToWindow(hwnd, sprintKey, isDown: false, repeat: false);
                    }
                    catch { /* run already terminalized; nothing more we can do */ }
                }
            }
        }
    }

    // One-time delayed sprint activation for a Background run. After the activation W-down, wait 40-60ms
    // before touching the sprint key (GZW skips a sprint key posted back-to-back with W). Hold mode leaves
    // the key held (recorded so ReleaseAutoRunState posts the paired UP — no stuck sprint); Press/toggle
    // mode taps it (DOWN → 40-60ms → UP) to flip the game's sprint toggle, which then persists across
    // alt-tab with no re-post. Sleeps are OUTSIDE _autoRunLock; every post re-checks the run + target under
    // the lock. Runs on the Background thread, so blocking sleeps are safe (never the hook thread).
    private void DoDelayedBackgroundSprintActivation()
    {
        bool hold;
        Key sprintKey;
        lock (_autoRunLock)
        {
            if (!_backgroundInputRun || _backgroundInputThread != Thread.CurrentThread
                || !_autoRunActive || !_autoRunIsBackground || !_autoRunSprintEnabled)
            {
                return;
            }
            hold = _autoRunSprintToggleable; // Hold mode (else Press/toggle)
            sprintKey = _autoRunSprintKey;
        }

        Thread.Sleep(RandomBackgroundDelay(BG_SPRINT_PREDELAY_MIN_MS, BG_SPRINT_PREDELAY_MAX_MS));

        lock (_autoRunLock)
        {
            if (!_backgroundInputRun || _backgroundInputThread != Thread.CurrentThread
                || !_autoRunActive || !_autoRunIsBackground || !BackgroundTargetValid(_autoRunTargetHwnd))
            {
                return;
            }
            // Consume the one-time pending flag UNCONDITIONALLY now (codex sol/xhigh #1): whatever we decide
            // below, this delayed activation has run once.
            if (!_autoRunSprintEnabled) return;
            _autoRunSprintEnabled = false;

            if (hold)
            {
                // Hold: only post the initial DOWN if the user STILL wants sprint held (a stamina toggle
                // during the pre-delay window may have turned it off → intent=false; do NOT resurrect it),
                // it is not already engaged, and the game is STILL foreground (if focus left during the
                // delay the game cleared its input — leave intent=true,current=false and let the loop's
                // focus-regain path do the W → 50ms → sprint sequence instead of posting into a stale epoch).
                // RESIDUAL (codex sol/xhigh round 2): if focus LEFT and RETURNED entirely within this 40-60ms
                // pre-delay, the foreground check below passes and the loop (edge-triggered on the boolean)
                // may not register the crossed epoch. Benign for the INITIAL activation — sprint was never
                // engaged, so this DOWN is a clean first engage (no prior sprint to "dash"). A strict epoch
                // guarantee would need a monotonic foreground-generation token; not worth the hot-path
                // complexity for a sub-60ms, non-human-reproducible blip.
                if (!_autoRunSprintIntendedHeld) return;
                if (_autoRunSprintInjected) return;
                if (!ForegroundIsAutoRunTargetProcess()) return;

                if (!PostAutoRunKey(sprintKey, isDown: true)) return; // sprint DOWN
                if (IsDebugEnabled) LogDebug("AutoRun Background delayed sprint DOWN posted (mode=Hold)");
                // Held: record CURRENT so ReleaseAutoRunState posts the paired UP (no stuck sprint). Intent
                // was set at activation; current now matches.
                _autoRunSprintInjected = true;
                _autoRunSprintInjectedKey = sprintKey;
                return;
            }

            // Press/toggle: a one-shot tap that flips the game's OWN sprint toggle (no intent/current
            // model). Post the DOWN best-effort.
            if (!PostAutoRunKey(sprintKey, isDown: true)) return; // sprint DOWN
            if (IsDebugEnabled) LogDebug("AutoRun Background delayed sprint DOWN posted (mode=Press)");
        }

        // Press/toggle: hold the tap briefly, then release. Post the UP ONLY if THIS worker still owns the
        // current run (identity + active) — otherwise a stale tap could post into a REPLACEMENT run and
        // truncate/release its sprint (codex R2 #1). If our run ended first, the unpaired DOWN is the
        // documented residual (PostMessage-only: the game's toggle already fired on the DOWN edge and
        // nothing is held system-wide).
        Thread.Sleep(RandomBackgroundDelay(BG_SPRINT_TAP_MIN_MS, BG_SPRINT_TAP_MAX_MS));
        lock (_autoRunLock)
        {
            if (_backgroundInputRun && _backgroundInputThread == Thread.CurrentThread
                && _autoRunActive && _autoRunIsBackground && BackgroundTargetValid(_autoRunTargetHwnd))
            {
                PostAutoRunKey(sprintKey, isDown: false); // sprint UP (completes the tap)
                if (IsDebugEnabled) LogDebug("AutoRun Background delayed sprint UP posted (tap complete)");
            }
        }
    }

    // Random dwell in [minMs, maxMs] for the Background sprint activation timing, on the Background thread's
    // own ThreadLocal Random.
    private int RandomBackgroundDelay(int minMs, int maxMs) => _random.Value!.Next(minMs, maxMs + 1);

    // Signals the Background thread to stop and joins it (bounded), off the hook thread — called only from
    // Stop/Dispose (app lifecycle). Signals under _autoRunLock, then joins OUTSIDE it so the thread can take
    // _autoRunLock to finish its final tick and exit. A chord toggle-off must NOT call this (I5):
    // ReleaseAutoRunState signals only.
    private void JoinBackgroundInputThread()
    {
        Thread? t;
        lock (_autoRunLock)
        {
            _backgroundInputRun = false;
            t = _backgroundInputThread;
            _backgroundInputThread = null;
        }
        if (t != null && t != Thread.CurrentThread && t.IsAlive)
        {
            t.Join(300);
        }
    }

    // Cheap foreground-process check for the sprint re-establish edge (no Process.GetProcessById on the
    // 28Hz path). True iff the current foreground window belongs to the Background run's target process.
    private bool ForegroundIsAutoRunTargetProcess()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || _autoRunTargetPid == 0)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(fg, out var fgPid);
        return fgPid == _autoRunTargetPid;
    }

    // Cheap hot-path validity check for the Background target (called every ~35ms on the Background
    // thread): valid iff the HWND still resolves to the SAME process instance we exe-validated at
    // activation. GetWindowThreadProcessId returns pid 0 for a dead handle and the reusing process's pid
    // for a reused one, so this single non-blocking call catches BOTH "window gone" AND "handle reused by
    // another process" (the case that must never receive an attach / SetKeyboardState / post) with NO
    // Process.GetProcessById on the 28Hz path (WindowBelongsToExe does that heavier check, kept for the
    // teardown UPs). A same-process window swap remains the documented residual. Caller holds _autoRunLock.
    private bool BackgroundTargetValid(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _autoRunTargetPid == 0)
        {
            return false;
        }
        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return pid != 0 && pid == _autoRunTargetPid;
    }

    // WM_KEY*/WM_SYSKEY* lParam: bits 0-15 repeat count (1), 16-23 scan code, 24 extended-key, 29 context
    // (ALT held, for WM_SYSKEY*), 30 previous-state (1 on keyup), 31 transition (1 on keyup). Zero-extended
    // into the pointer-sized LPARAM.
    private static IntPtr BuildKeyLParam(uint scanCode, bool isDown, bool extended, bool repeat = false, bool altContext = false)
    {
        uint lp = 1u;
        lp |= (scanCode & 0xFFu) << 16;
        if (extended) lp |= 1u << 24;
        if (altContext) lp |= 1u << 29;             // WM_SYSKEY* context bit: ALT down when the key was pressed
        if (!isDown) lp |= (1u << 30) | (1u << 31); // keyup: previous-state + transition bits
        else if (repeat) lp |= 1u << 30;            // auto-repeat keydown: previous-state = down
        return (IntPtr)(long)lp;
    }

    // True iff the current foreground window belongs to the Background run's target PROCESS. Gates the
    // physical W/S/sprint cancel of a Background run to "only while the game is focused" (§11.6). This is
    // reached on the HOOK thread (cancel / sprint-toggle), so it uses the CHEAP PID compare
    // (ForegroundIsAutoRunTargetProcess) — never Process.GetProcessById (WindowBelongsToExe), which could
    // stall past LowLevelHooksTimeout and freeze all input (B2). The exe→PID binding was established
    // off-hot-path at activation; the PID compare here is equivalent for a live run (same process).
    private bool ForegroundMatchesAutoRunTarget()
    {
        return ForegroundIsAutoRunTargetProcess();
    }

    // Validates that a window is alive and its owning process's exe equals the given normalized name.
    // Mirrors ForegroundWatcher.ResolveProcessName + ExecutableName.Normalize. Does NOT catch a
    // same-process window swap (the stale handle still passes IsWindow + PID→exe) — documented residual.
    private static bool WindowBelongsToExe(IntPtr hwnd, string? normalizedExe)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(normalizedExe) || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return string.Equals(ExecutableName.Normalize(process.ProcessName), normalizedExe, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // process exited between the PID query and the open — treat as invalid
        }
    }

    // ==================== ANTI-AFK ====================

    // Fixed-period tick (single-flight). Fires ONE atomic WASD sequence iff ALL guards hold. Runs on a
    // threadpool timer thread; reads volatile runtime state + native idle/foreground, enqueues on the
    // shared injector. No locks (guard 4 reads _autoRunActive volatile; the injector serializes work).
    private void AntiAfkTick()
    {
        if (!_isRunning || _disposed)
        {
            return;
        }

        // Single-flight: a slow injector must not let ticks overlap (Interlocked, like the watchdog).
        if (Interlocked.CompareExchange(ref _antiAfkTickRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            // One throttled diagnostic per ~minute, evaluated at EVERY guard so "why didn't it fire" is
            // answerable even when an early guard (auto-run active / foreground mismatch) returns before
            // the idle snapshot below.
            bool logReason = IsDebugEnabled && (++_antiAfkDiagTicks % 12) == 0;

            // Guard 2: Advanced-Mode-gated feature — off → this tick simply no-ops (no explicit disarm).
            if (!_advancedModeEnabled)
            {
                if (logReason) LogDebug("Anti-AFK skip: advanced-mode-off");
                return;
            }

            // Guard 3: an active game profile that has Anti-AFK enabled.
            var profile = _activeProfile;
            if (profile is null || !profile.AntiAfk.IsEnabled)
            {
                if (logReason) LogDebug("Anti-AFK skip: no active profile / anti-afk disabled");
                return;
            }

            // Guard 4 (fast pre-check): Auto-Run counts as activity — never overlap a WASD tap onto a
            // held W. This volatile read is only an optimization; the AUTHORITATIVE re-check is under
            // _autoRunLock at the fire point below (codex P4 #1), so an Auto-Run activation racing this
            // tick cannot interleave its W-down before our WASD in the FIFO.
            if (_autoRunActive)
            {
                if (logReason) LogDebug("Anti-AFK skip: auto-run active");
                return;
            }

            // Guard 5: KEYBOARD-ONLY physical idle AND cadence both satisfied. Keyboard idle is measured
            // from _lastPhysicalKeyboardTick (Stopwatch domain; genuine physical keys only — injected
            // events are filtered before that stamp). GetLastInputInfo is deliberately NOT used here: it
            // is global (all devices), so mouse/peripheral noise kept the system "fresh" and this guard
            // never tripped (debug.log: 69 fresh / 0 idle → Anti-AFK never fired). Cadence stays in the
            // Environment.TickCount (uint) domain — do NOT mix the two raw values. Anti-AFK's own injected
            // WASD is INPUT_IGNORE-filtered so it does NOT advance _lastPhysicalKeyboardTick; the cadence
            // clause is what paces repeat ripples every interval while the keyboard stays idle, and a
            // single REAL keypress resets keyboard idle and stops it.
            var intervalMs = (uint)(Math.Clamp(profile.AntiAfk.IntervalMinutes, 1, 15) * 60_000);
            var keyboardIdleMs = (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastPhysicalKeyboardTick)) * TickToMilliseconds;
            var now = unchecked((uint)Environment.TickCount);
            var sinceLastFireMs = unchecked(now - _antiAfkLastFireTick);

            // Diagnostic (throttled, ~once/min): the exact snapshot the decision uses, so a future "it
            // didn't fire" investigation reads the reason straight from the log.
            if (logReason)
            {
                LogDebug($"Anti-AFK idle check: keyboardIdle={keyboardIdleMs:F0}ms, sinceLastFire={sinceLastFireMs}ms, interval={intervalMs}ms");
            }

            if (keyboardIdleMs < intervalMs || sinceLastFireMs < intervalMs)
            {
                return; // not idle long enough / cadence — the idle-check line above logged the numbers
            }

            // Guard 6 (MANDATORY): the foreground process still matches the active profile.
            // _activeProfile lags real foreground during ProfileActivationService's color work, so
            // without this an idle-satisfied tick could inject WASD into a browser you alt-tabbed to.
            if (!ForegroundMatchesActiveProfile())
            {
                if (logReason) LogDebug("Anti-AFK skip: foreground is not the active game (idle+cadence WERE met)");
                return;
            }

            // Build the jittered sequence OUTSIDE the lock (RNG only), then check guard 4 AUTHORITATIVELY
            // and enqueue ATOMICALLY under _autoRunLock: ActivateAutoRun holds this same lock while it
            // enqueues its W-down and publishes _autoRunActive, so either we observe it active (skip) or
            // we enqueue our WASD before its W-down takes the lock — never our W-up AFTER its held W-down
            // (which would release Auto-Run's forward, codex P4 #1). Enqueue takes no other lock (I5), and
            // the RNG work is already done, so the lock is held only for a check + a queue Add. Dummy
            // Key/IsDown/PreSleep — the Sequence field carries the real work.
            var sequence = BuildAntiAfkSequence();
            lock (_autoRunLock)
            {
                // Re-check _isRunning (C2) as well as _autoRunActive: a tick racing Stop() (before its
                // _isRunning=false flip) must not enqueue a ripple into a tearing-down injector.
                if (!_isRunning || _autoRunActive)
                {
                    // Auto-Run won the race between guard-4's pre-check and this authoritative recheck.
                    if (logReason && _autoRunActive) LogDebug("Anti-AFK skip: auto-run active (authoritative recheck)");
                    return;
                }

                EnqueueHoldBreathInjection(new HoldBreathInjection(Key.None, IsDown: false, PreSleepMs: 0, Sequence: sequence));
                _antiAfkLastFireTick = now;
            }

            if (IsDebugEnabled) LogDebug($"Anti-AFK fired WASD ripple (keyboardIdle={keyboardIdleMs:F0}ms, interval={intervalMs}ms)");
        }
        finally
        {
            Volatile.Write(ref _antiAfkTickRunning, 0);
        }
    }

    // W↓W↑ · gap · A↓A↑ · gap · S↓S↑ · gap · D↓D↑ — net-zero displacement, each tap human-jittered
    // (reuses the hold-breath tap-duration idiom + RNG warmup). Sequential taps, never simultaneous
    // (simultaneous W+S / A+D cancel and read as a single frame).
    private TapStep[] BuildAntiAfkSequence()
    {
        var rng = _random.Value!;
        var warmupCalls = rng.Next(RNG_WARMUP_MIN_CALLS, RNG_WARMUP_MAX_CALLS + 1);
        for (int i = 0; i < warmupCalls; i++) rng.Next();

        TapStep Tap(Key key) => new(
            key,
            rng.Next(HOLD_BREATH_TAP_DURATION_MIN_MS, HOLD_BREATH_TAP_DURATION_MAX_MS + 1),
            rng.Next(ANTI_AFK_GAP_MIN_MS, ANTI_AFK_GAP_MAX_MS + 1));

        return new[] { Tap(Key.W), Tap(Key.A), Tap(Key.S), Tap(Key.D) };
    }

    // True iff the current foreground window's process matches the active profile's exe. Used by the
    // Anti-AFK tick (fire-time gate, guard 6) and the injector's per-step sequence abort so WASD can
    // only ever land in the profile's own game window.
    private bool ForegroundMatchesActiveProfile()
    {
        var profile = _activeProfile;
        if (profile is null)
        {
            return false;
        }

        var exe = profile.NormalizedExecutable;
        return !string.IsNullOrEmpty(exe) && WindowBelongsToExe(NativeMethods.GetForegroundWindow(), exe);
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

    // F-011: MUST be called under _combinedOverridesLock. Decrements the target's source-refcount; returns
    // true only on the 1→0 transition (the last source released it), meaning the caller should send the
    // target's UP. Returns false while another source still holds the target.
    private bool DecrementCombinedTarget(Key target)
    {
        var count = _combinedTargetCounts.GetValueOrDefault(target);
        if (count == 1)
        {
            _combinedTargetCounts.Remove(target); // codex #3: only 1→0 removes and requests the UP
            return true;
        }

        if (count <= 0)
        {
            // Invariant failure — the target was not tracked; do NOT emit a spurious UP.
            if (IsDebugEnabled) LogDebug($"Combined target refcount underflow for {target} (count={count})");
            return false;
        }

        _combinedTargetCounts[target] = count - 1;
        return false;
    }

    private void ReleaseRightClickOverrides()
    {
        List<Key>? targetsToRelease = null;

        lock (_combinedOverridesLock)
        {
            if (_activeCombinedOverrides.Count == 0)
            {
                return;
            }

            List<Key>? sourcesToRemove = null;
            foreach (var kvp in _activeCombinedOverrides)
            {
                if (kvp.Value.RightClickOnly)
                {
                    (sourcesToRemove ??= new List<Key>()).Add(kvp.Key);
                }
            }

            if (sourcesToRemove is null)
            {
                return;
            }

            foreach (var source in sourcesToRemove)
            {
                if (_activeCombinedOverrides.Remove(source, out var state) && state is not null &&
                    DecrementCombinedTarget(state.TargetKey)) // F-011: UP only when the LAST source releases
                {
                    (targetsToRelease ??= new List<Key>()).Add(state.TargetKey);
                }
            }

            _activeCombinedOverrideCount = _activeCombinedOverrides.Count;
        }

        if (targetsToRelease is not null)
        {
            foreach (var target in targetsToRelease)
            {
                SendKey(target, false);
                if (IsDebugEnabled) LogDebug($"Force-release right-click override target: {target}");
            }
        }
    }

    // Advanced-Mode-off release of the gated capability among combined overrides: the un-suppressed
    // (SuppressOriginal == false) ones, which are the non-1:1 mappings. Suppressed 1:1 overrides are
    // game-safe and stay active. ENQUEUE each target UP on the injector (NOT synchronous SendKey):
    // the Advanced-off setter calls this on the UI dispatcher/hook thread, where a SendInput trip
    // through a stalled foreign hook would freeze input. Removing the dict entry means a later
    // physical source key-up finds nothing to release (H2 Remove→false), so it can't double-send.
    private void ReleaseUnsuppressedCombinedOverrides()
    {
        List<Key>? targetsToRelease = null;

        lock (_combinedOverridesLock)
        {
            if (_activeCombinedOverrides.Count == 0)
            {
                return;
            }

            List<Key>? sourcesToRemove = null;
            foreach (var kvp in _activeCombinedOverrides)
            {
                if (!kvp.Value.SuppressOriginal)
                {
                    (sourcesToRemove ??= new List<Key>()).Add(kvp.Key);
                }
            }

            if (sourcesToRemove is null)
            {
                return;
            }

            foreach (var source in sourcesToRemove)
            {
                if (_activeCombinedOverrides.Remove(source, out var state) && state is not null &&
                    DecrementCombinedTarget(state.TargetKey)) // F-011: UP only when the LAST source releases
                {
                    (targetsToRelease ??= new List<Key>()).Add(state.TargetKey);
                }
            }

            _activeCombinedOverrideCount = _activeCombinedOverrides.Count;
        }

        if (targetsToRelease is not null)
        {
            foreach (var target in targetsToRelease)
            {
                EnqueueHoldBreathInjection(new HoldBreathInjection(target, IsDown: false, PreSleepMs: 0));
                if (IsDebugEnabled) LogDebug($"Advanced-off release un-suppressed target: {target} (queued)");
            }
        }
    }

    private void ReleaseAllOverrides()
    {
        List<Key> targetsToRelease;

        lock (_combinedOverridesLock)
        {
            if (_activeCombinedOverrides.Count == 0)
            {
                return;
            }

            // F-011: everything is cleared, so every currently-held target reaches 0 → release each once.
            targetsToRelease = new List<Key>(_combinedTargetCounts.Keys);
            _activeCombinedOverrides.Clear();
            _combinedTargetCounts.Clear();
            _activeCombinedOverrideCount = 0;
        }

        foreach (var target in targetsToRelease)
        {
            SendKey(target, false);
            LogDebug($"Force-release combined override target: {target}");
        }
    }
// ==================== STATE MANAGEMENT ====================
    
    private void ReleaseAllState()
    {
        
        ReleaseAllOverrides();
        ResetMouseStates();
        ReleaseCapsState();
        ReleaseHoldBreathState();
        lock (_holdBreathLock)
        {
            _holdBreathPanicSuppressed = false;
        }
        Volatile.Write(ref _holdBreathPanicConsumedKeyVk, 0);
        Volatile.Write(ref _holdBreathPanicConsumedMouseButton, 0);
        // includeBackground:false — a decoupled Background run must survive ordinary teardown
        // (ReleaseAllState is reached by profile switch AND watchdog reinstall). §11.6.
        ReleaseAutoRunState(includeBackground: false);

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
