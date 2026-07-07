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
    
    // Alt+Mouse Hold Detection
    private const int ALT_MOUSE_HOLD_JITTER_MIN_MS = 5;
    private const int ALT_MOUSE_HOLD_JITTER_MAX_MS = 10;
    
    // Key Press Duration (human-like variance)
    private const int KEY_PRESS_DURATION_MIN_MS = 31;
    private const int KEY_PRESS_DURATION_MAX_MS = 53;
    
    // Hold Breath Activation Jitter
    private const int HOLD_BREATH_JITTER_MIN_MS = 15;
    private const int HOLD_BREATH_JITTER_MAX_MS = 36;
    
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
    
    // CapsLock state
    private int _capsShiftEngaged;
    private Key? _capsRemappedKey;

    // Per-key "already launched while held" latch. Prevents typematic auto-repeat from spawning a
    // launcher process on every repeated WM_KEYDOWN. Guarded by its own lock because ReleaseAllState()
    // (Clear) runs on the activation-worker POOL thread while the keyboard hook thread does Add/Remove.
    private readonly HashSet<Key> _heldLauncherKeys = new();
    private readonly object _heldLauncherKeysLock = new();

    // Hold-breath state. All fields below are guarded by _holdBreathLock. Every hold-breath event
    // (arm, fire, cancel, release) already paid this lock for Timer.Change, and events occur at
    // human click frequency — so guarding the whole state machine including the SendInput calls
    // costs nothing extra while guaranteeing the UP release can never overtake the DOWN press.
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
    
    // Performance metrics
    private static readonly double TickToMilliseconds = 1000.0 / Stopwatch.Frequency;

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

            // Recover from a desktop switch that swallows the button-up (lock screen, logoff):
            // without this, an injected hold-breath key would stay down until the next click.
            SystemEvents.SessionSwitch += OnSessionSwitch;

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

            ReleaseAllState();
            _isRunning = false;

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
        if (nCode < 0 || !_isRunning)
        {
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

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
            _altPressed = isKeyDown;
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
        if (nCode < 0 || !_isRunning)
        {
            return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

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
            if (isUp && state.DownTick.HasValue)
            {
                LogDebug($"[{button}] Stale state cleared (Alt released)");
                state.DownTick = null;
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

        // Record timestamp
        state.DownTick = Stopwatch.GetTimestamp();

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
            // long so the callback can measure real elapsed time (and avoid a torn nullable read).
            var stateRef = state;
            var downTickAtArm = state.DownTick!.Value;

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
                FireTapKey(holdKey);
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
        if (!state.DownTick.HasValue)
        {
            return false;
        }

        // Calculate hold duration
        var elapsedMs = (Stopwatch.GetTimestamp() - state.DownTick.Value) * TickToMilliseconds;

        state.DownTick = null;

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
            FireTapKey(binding.HoldKey.Value);
        }
        else if (binding.TapKey.HasValue)
        {
            // Quick tap - send tap key
            if (IsDebugEnabled) LogDebug($"[{button}] Quick tap");
            FireTapKey(binding.TapKey.Value);
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
                if (isKeyDown && Interlocked.CompareExchange(ref _capsShiftEngaged, 1, 0) == 0)
                {
                    ForceCapsLockState(true);
                    LogDebug("CapsLock → FORCED ON (Hold mode)");
                }
                else if (isKeyUp && Interlocked.CompareExchange(ref _capsShiftEngaged, 0, 1) == 1)
                {
                    ForceCapsLockState(false);
                    LogDebug("CapsLock → FORCED OFF (Hold mode)");
                }
                return true;

            case CapsLockMode.Remap:
                var target = settings.RemapTarget;
                if (!target.HasValue)
                {
                    return true;
                }

                if (isKeyDown)
                {
                    _capsRemappedKey = target;
                    SendKey(target.Value, true);
                    LogDebug($"CapsLock → {target.Value} DOWN (Remap mode)");
                }
                else if (isKeyUp && _capsRemappedKey.HasValue)
                {
                    SendKey(_capsRemappedKey.Value, false);
                    LogDebug($"CapsLock → {_capsRemappedKey.Value} UP (Remap mode)");
                    _capsRemappedKey = null;
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
        if (Interlocked.CompareExchange(ref _capsShiftEngaged, 0, 1) == 1)
        {
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
            // Send Caps Lock key tap to toggle it (down + up)
            // Using VK code directly for Caps Lock (0x14)
            var input = new NativeMethods.INPUT
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
            
            // Send key down
            NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
            
            // Send key up
            input.U.ki.dwFlags = NativeMethods.KeyEventFlags.KEYEVENTF_KEYUP;
            NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
            
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
            _holdBreathTimer.Change(Timeout.Infinite, Timeout.Infinite);
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
            var key = _holdBreathArmedKey;

            // Fire tap asynchronously (don't block hook). The down+up pair is self-contained and
            // nothing is recorded, so a toggle tap cannot strand a key.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (_disposed)
                {
                    return;
                }

                var rng = _random.Value!;

                // Warmup to prevent pattern detection
                var warmup = rng.Next(RNG_WARMUP_MIN_CALLS, RNG_WARMUP_MAX_CALLS + 1);
                for (int i = 0; i < warmup; i++)
                {
                    rng.Next();
                }

                SendKey(key, true);

                // Human-like key press duration (20-30ms)
                var duration = rng.Next(20, 31);
                Thread.Sleep(duration);

                SendKey(key, false);

                LogDebug($"HoldBreath toggle tap complete: duration={duration}ms");
            });
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
            _holdBreathTimer.Change(Timeout.Infinite, Timeout.Infinite);
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

        // Check if Windows key is pressed
        bool winPressed = (NativeMethods.GetAsyncKeyState(KeyInteropUtilities.ToVirtualKey(Key.LWin)) & 0x8000) != 0 ||
                          (NativeMethods.GetAsyncKeyState(KeyInteropUtilities.ToVirtualKey(Key.RWin)) & 0x8000) != 0;

        if (!winPressed)
        {
            return false;
        }

        if (!profile.WindowsLauncher.Launchers.TryGetValue(key.Value, out var binding))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(binding.Path))
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

        LogDebug($"WindowsLauncher: Win+{key.Value} → {binding.Path}");

        // Launch asynchronously (don't block hook)
        ThreadPool.QueueUserWorkItem(_ => LaunchProcess(binding));

        return true;
    }

    private void LaunchProcess(LauncherBinding binding)
    {
        try
        {
            ProcessLauncher.Launch(binding.Path, binding.Arguments, binding.RunAsAdmin, _logger);
            
            LogDebug($"Launch successful: {binding.Path}");
        }
        catch (Exception ex)
        {
            LogDebug($"Launch failed: {binding.Path} - {ex.Message}");
        }
    }

    // ==================== KEY INJECTION ====================
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FireTapKey(Key key)
    {
        // Fire on ThreadPool to avoid blocking hook callback
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (_disposed)
            {
                return;
            }

            var rng = _random.Value!;
            
            // Warmup RNG to break thread-reuse patterns (anti-cheat)
            var warmupCalls = rng.Next(RNG_WARMUP_MIN_CALLS, RNG_WARMUP_MAX_CALLS + 1);
            for (int i = 0; i < warmupCalls; i++)
            {
                rng.Next();
            }
            
            SendKey(key, true);
            
            // Human-like key press duration with jitter
            var duration = rng.Next(KEY_PRESS_DURATION_MIN_MS, KEY_PRESS_DURATION_MAX_MS + 1);

            //// High-resolution wait (more accurate than Thread.Sleep for <50ms)
            //var sw = Stopwatch.StartNew();
            //while (sw.ElapsedMilliseconds < duration)
            //{
            //    Thread.SpinWait(1000);  // ~10-50μs per iteration
            //}
            Thread.Sleep(duration);
            SendKey(key, false);
            
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

        var result = NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
        
        if (result == 0)
        {
            LogDebug($"SendKey FAILED: {key} ({(isKeyDown ? "DOWN" : "UP")}) - SendInput returned 0, VK=0x{virtualKey:X2}");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsExtendedKey(Key key)
    {
        return key is Key.RightAlt or Key.RightCtrl or Key.Insert or Key.Delete or 
                      Key.Home or Key.End or Key.PageUp or Key.PageDown or 
                      Key.Up or Key.Down or Key.Left or Key.Right or 
                      Key.NumLock or Key.PrintScreen or Key.Divide;
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
        lock (_heldLauncherKeysLock)
        {
            _heldLauncherKeys.Clear();
        }

        _altPressed = false;
        _rightButtonPressed = false;
        
        LogDebug("All state released");
    }

    private void ResetMouseStates()
    {
        foreach (var (button, state) in _mouseStates)
        {
            CancelHoldTimer(state);
            Interlocked.Exchange(ref state.TimerState, TIMER_IDLE);
            state.DownTick = null;
            
            LogDebug($"Reset mouse state: {button}");
        }
    }

    // ==================== STATE CLASSES ====================
    
    private sealed class MouseButtonState
    {
        // Atomic state machine
        public int TimerState = TIMER_IDLE;
        
        // Timestamp tracking
        public long? DownTick;
        
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
