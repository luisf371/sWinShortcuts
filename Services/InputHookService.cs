using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
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
    // ==================== CONFIGURATION ====================
    
    /// <summary>
    /// Enable debug logging (off by default for production performance).
    /// Set to true during development/troubleshooting.
    /// </summary>
    private const bool ENABLE_DEBUG_LOGGING = false;
    
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
    
    private const int HOLDBREATH_IDLE = 0;
    private const int HOLDBREATH_PENDING = 1;
    private const int HOLDBREATH_ACTIVE = 2;
    private const int HOLDBREATH_CANCELLED = 3;

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
    
    private readonly Dictionary<Key, CombinedOverrideState> _activeCombinedOverrides = new();
    
    // Profile state
    private volatile Profile? _activeProfile;
    private volatile Profile? _windowsProfile;
    
    // Runtime flags (volatile for lock-free reads)
    private volatile bool _isRunning;
    private volatile bool _altPressed;
    private volatile bool _rightButtonPressed;
    
    // CapsLock state
    private int _capsShiftEngaged;
    private Key? _capsRemappedKey;
    
    // Hold-breath state (lock-free)
    private int _holdBreathState = HOLDBREATH_IDLE;
    private readonly System.Threading.Timer _holdBreathTimer;
    private readonly object _holdBreathTimerLock = new();
    
    // Hook handles
    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private IntPtr _keyboardHookHandle = IntPtr.Zero;
    private IntPtr _mouseHookHandle = IntPtr.Zero;
    
    // Performance metrics
    private static readonly double TickToMilliseconds = 1000.0 / Stopwatch.Frequency;
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "sWinShortcuts_Input_Debug.log");
    
    // Logging queue (off hot path, only used if ENABLE_DEBUG_LOGGING is true)
    private static readonly BlockingCollection<string>? _logQueue = 
        ENABLE_DEBUG_LOGGING ? new BlockingCollection<string>(new ConcurrentQueue<string>(), 1000) : null;
    private static readonly CancellationTokenSource? _logCancellation = 
        ENABLE_DEBUG_LOGGING ? new CancellationTokenSource() : null;
    
    static InputHookService()
    {
        if (!ENABLE_DEBUG_LOGGING || _logQueue == null || _logCancellation == null)
        {
            return;
        }
        
        // Background logging thread (never blocks input processing)
        Task.Run(() =>
        {
            var buffer = new List<string>(100);
            while (!_logCancellation.Token.IsCancellationRequested)
            {
                try
                {
                    buffer.Clear();
                    while (buffer.Count < 100 && _logQueue.TryTake(out var msg, 50))
                    {
                        buffer.Add(msg);
                    }
                    
                    if (buffer.Count > 0)
                    {
                        File.AppendAllLines(LogPath, buffer);
                    }
                }
                catch
                {
                    // Swallow logging errors
                }
            }
        }, _logCancellation.Token);
    }

    public InputHookService()
    {
        // Initialize hold breath timer (pre-allocated, reused throughout lifetime)
        _holdBreathTimer = new System.Threading.Timer(_ =>
        {
            var profile = _activeProfile;
            if (profile?.RightClickHoldBreath.IsEnabled == true)
            {
                ActivateHoldBreath(profile.RightClickHoldBreath);
            }
        }, null, Timeout.Infinite, Timeout.Infinite);
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
                Stop();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install input hooks");
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
        Stop();
        _random.Dispose();
        _holdBreathTimer.Dispose();
        
#pragma warning disable CS0162 // Unreachable code detected - intentional based on ENABLE_DEBUG_LOGGING const
        if (ENABLE_DEBUG_LOGGING)
        {
            _logQueue?.CompleteAdding();
            _logCancellation?.Cancel();
        }
#pragma warning restore CS0162
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
            handled = HandleWindowsLauncher(vkCode, isKeyDown);
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

        // Track right button state (lock-free)
        if (message == NativeMethods.WM_RBUTTONDOWN)
        {
            _rightButtonPressed = true;
            HandleRightClickHoldBreathDown();
        }
        else if (message == NativeMethods.WM_RBUTTONUP)
        {
            _rightButtonPressed = false;
            ReleaseRightClickOverrides();
            HandleRightClickHoldBreathUp();
        }

        var handled = HandleAltMouse(message, data.mouseData);

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

        LogDebug($"[{button}] DOWN - Tap={binding.TapKey}, Hold={binding.HoldKey}, " +
                 $"Threshold={profile.AltMouse.HoldThresholdMilliseconds}ms");

        // Schedule hold timer if configured
        if (binding.HoldKey.HasValue)
        {
            var holdKey = binding.HoldKey.Value;
            //    var baseThreshold = Math.Max(10, profile.AltMouse.HoldThresholdMilliseconds);
            //    
            //    // Add anti-cheat jitter
            //    var rng = _random.Value!;
            //    var jitter = rng.Next(ALT_MOUSE_HOLD_JITTER_MIN_MS, ALT_MOUSE_HOLD_JITTER_MAX_MS + 1);
            //    var threshold = baseThreshold + jitter;

            //    LogDebug($"[{button}] Hold timer: base={baseThreshold}ms, jitter=+{jitter}ms, total={threshold}ms");
            // Deterministic threshold (no jitter)
            var threshold = Math.Max(10, profile.AltMouse.HoldThresholdMilliseconds);
            LogDebug($"[{button}] Hold timer: {threshold}ms");

            // Only capture the state reference (not runtime flags)
            var stateRef = state;

            state.HoldTimer.Change(threshold, Timeout.Infinite);
            state.HoldCallback = _ =>
            {
                // ✅ CRITICAL FIX: Check CURRENT runtime state via volatile fields
                // These are read at execution time, not captured at scheduling time
                if (!_isRunning)
                {
                    LogDebug($"[{button}] Hold timer blocked - service stopped");
                    return;
                }
                
                if (!_altPressed)
                {
                    LogDebug($"[{button}] Hold timer blocked - Alt released");
                    return;
                }

                // ✅ Atomic state check: only fire if still ARMED
                if (Interlocked.CompareExchange(ref stateRef.TimerState, TIMER_FIRED, TIMER_ARMED) != TIMER_ARMED)
                {
                    LogDebug($"[{button}] Hold timer blocked - state changed (cancelled or already fired)");
                    return;
                }

                LogDebug($"[{button}] Hold timer FIRED - sending {holdKey}");
                FireTapKey(holdKey);
            };
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

        LogDebug($"[{button}] UP - Elapsed={elapsedMs:F1}ms, Threshold={threshold}ms, State={finalState}");

        if (finalState == TIMER_FIRED)
        {
            // Timer already sent the hold key - don't send again
            LogDebug($"[{button}] Hold was triggered by timer (not re-triggering)");
        }
        else if (binding.HoldKey.HasValue && elapsedMs >= threshold)
        {
            // We beat the timer, but threshold was met - send hold key
            LogDebug($"[{button}] Hold threshold met manually");
            FireTapKey(binding.HoldKey.Value);
        }
        else if (binding.TapKey.HasValue)
        {
            // Quick tap - send tap key
            LogDebug($"[{button}] Quick tap");
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
        var profile = _activeProfile;
        if (profile is null || !profile.CombinedMappings.IsEnabled)
        {
            return false;
        }

        var sourceKey = KeyInteropUtilities.FromVirtualKey(vkCode);
        if (sourceKey is null)
        {
            return false;
        }

        var entry = profile.CombinedMappings.Mappings.FirstOrDefault(m => m.SourceKey == sourceKey.Value);
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

            if (_activeCombinedOverrides.ContainsKey(sourceKey.Value))
            {
                return suppressOriginal;
            }

            // Safety: do nothing if mapping is a no-op (source == target)
            if (targetKey == sourceKey.Value)
            {
                return false;
            }

            _activeCombinedOverrides[sourceKey.Value] = new CombinedOverrideState { TargetKey = targetKey, SuppressOriginal = suppressOriginal, RightClickOnly = requiresRightClick };

            SendKey(targetKey, true);
            LogDebug($"Combined mapping: {sourceKey.Value} → {targetKey} (suppress={suppressOriginal})");
            
            return suppressOriginal;
        }

        if (isKeyUp)
        {
            if (_activeCombinedOverrides.Remove(sourceKey.Value, out var state))
            {
                SendKey(state.TargetKey, false);
                LogDebug($"Combined mapping released: {sourceKey.Value}");
                return state.SuppressOriginal;
            }
        }

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
    private static void ForceCapsLockState(bool enabled)
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

    // ==================== HOLD BREATH HANDLING (LOCK-FREE) ====================
    
    private void HandleRightClickHoldBreathDown()
    {
        var profile = _activeProfile;
        if (profile is null || !profile.RightClickHoldBreath.IsEnabled)
        {
            return;
        }

        // Cancel any pending activation
        CancelHoldBreathDelay();

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

        LogDebug($"HoldBreath DOWN: base={baseDelay}ms, jitter=+{jitter}ms, total={totalDelay}ms, warmup={warmupCalls}");

        // Atomically transition: * → PENDING
        Interlocked.Exchange(ref _holdBreathState, HOLDBREATH_PENDING);

        if (totalDelay > 0)
        {
            // Schedule delayed activation
            lock (_holdBreathTimerLock)
            {
                _holdBreathTimer.Change(totalDelay, Timeout.Infinite);
            }
        }
        else
        {
            // Immediate activation (check current state, not captured)
            if (_isRunning && _rightButtonPressed)
            {
                ActivateHoldBreath(settings);
            }
        }
    }

    private void HandleRightClickHoldBreathUp()
    {
        // Cancel pending activation
        CancelHoldBreathDelay();

        var profile = _activeProfile;
        if (profile is null || !profile.RightClickHoldBreath.IsEnabled)
        {
            return;
        }

        LogDebug("HoldBreath UP");

        // Atomically check and deactivate: ACTIVE → IDLE
        if (Interlocked.CompareExchange(ref _holdBreathState, HOLDBREATH_IDLE, HOLDBREATH_ACTIVE) == HOLDBREATH_ACTIVE)
        {
            if (profile.RightClickHoldBreath.Mode == HoldBreathMode.Hold)
            {
                SendKey(profile.RightClickHoldBreath.HoldBreathKey, false);
                LogDebug($"HoldBreath released: {profile.RightClickHoldBreath.HoldBreathKey}");
            }
        }
        else
        {
            // Wasn't active, just ensure we're idle
            Interlocked.CompareExchange(ref _holdBreathState, HOLDBREATH_IDLE, HOLDBREATH_PENDING);
            LogDebug("HoldBreath UP (was not active)");
        }
    }

    private void ActivateHoldBreath(RightClickHoldBreathSettings settings)
    {
        // ✅ CRITICAL FIX: Check CURRENT state, not captured values
        if (!_isRunning)
        {
            LogDebug("HoldBreath activation blocked - service stopped");
            return;
        }
        

        // Atomically activate: PENDING → ACTIVE
        if (Interlocked.CompareExchange(ref _holdBreathState, HOLDBREATH_ACTIVE, HOLDBREATH_PENDING) != HOLDBREATH_PENDING)
        {
            LogDebug("HoldBreath activation blocked - state changed (cancelled or already active)");
            return;
        }

        LogDebug($"HoldBreath ACTIVATED: mode={settings.Mode}, key={settings.HoldBreathKey}");

        if (settings.Mode == HoldBreathMode.Hold)
        {
            SendKey(settings.HoldBreathKey, true);
        }
        else if (settings.Mode == HoldBreathMode.Toggle)
        {
            // Fire tap asynchronously (don't block hook)
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var rng = _random.Value!;
                
                // Warmup to prevent pattern detection
                var warmup = rng.Next(RNG_WARMUP_MIN_CALLS, RNG_WARMUP_MAX_CALLS + 1);
                for (int i = 0; i < warmup; i++)
                {
                    rng.Next();
                }
                
                SendKey(settings.HoldBreathKey, true);
                
                // Human-like key press duration (20-30ms)
                var duration = rng.Next(20, 31);
                Thread.Sleep(duration);
                
                SendKey(settings.HoldBreathKey, false);
                
                LogDebug($"HoldBreath toggle tap complete: duration={duration}ms");
            });

            // Reset state immediately for toggle
            Interlocked.CompareExchange(ref _holdBreathState, HOLDBREATH_IDLE, HOLDBREATH_ACTIVE);
        }
    }

    private void CancelHoldBreathDelay()
    {
        // Atomically cancel: PENDING → CANCELLED
        var previous = Interlocked.CompareExchange(ref _holdBreathState, HOLDBREATH_CANCELLED, HOLDBREATH_PENDING);

        // Disable timer
        lock (_holdBreathTimerLock)
        {
            _holdBreathTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        // If we cancelled PENDING, reset to IDLE
        if (previous == HOLDBREATH_PENDING)
        {
            Interlocked.CompareExchange(ref _holdBreathState, HOLDBREATH_IDLE, HOLDBREATH_CANCELLED);
        }
    }

    private void ReleaseHoldBreathState()
    {
        CancelHoldBreathDelay();

        // Force release if active
        if (Interlocked.Exchange(ref _holdBreathState, HOLDBREATH_IDLE) == HOLDBREATH_ACTIVE)
        {
            var profile = _activeProfile;
            if (profile?.RightClickHoldBreath.IsEnabled == true &&
                profile.RightClickHoldBreath.Mode == HoldBreathMode.Hold)
            {
                SendKey(profile.RightClickHoldBreath.HoldBreathKey, false);
                LogDebug("Force-release HoldBreath key");
            }
        }
    }

    // ==================== WINDOWS LAUNCHER ====================
    
    private bool HandleWindowsLauncher(int vkCode, bool isKeyDown)
    {
        var profile = _windowsProfile;
        if (profile is null || !profile.WindowsLauncher.IsEnabled || !isKeyDown)
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

        var key = KeyInteropUtilities.FromVirtualKey(vkCode);
        if (key is null)
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

        LogDebug($"WindowsLauncher: Win+{key.Value} → {binding.Path}");

        // Launch asynchronously (don't block hook)
        ThreadPool.QueueUserWorkItem(_ => LaunchProcess(binding));

        return true;
    }

    private static void LaunchProcess(LauncherBinding binding)
    {
        try
        {
            ProcessLauncher.Launch(binding.Path, binding.Arguments, binding.RunAsAdmin);
            
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
            
            LogDebug($"FireTapKey: {key}, duration={duration}ms, warmup={warmupCalls}");
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SendKey(Key key, bool isKeyDown, bool bypassHook = true)
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
                    dwExtraInfo = bypassHook ? NativeMethods.INPUT_IGNORE : IntPtr.Zero
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
    private static bool IsExtendedKey(Key key)
    {
        return key is Key.RightAlt or Key.RightCtrl or Key.Insert or Key.Delete or 
                      Key.Home or Key.End or Key.PageUp or Key.PageDown or 
                      Key.Up or Key.Down or Key.Left or Key.Right or 
                      Key.NumLock or Key.PrintScreen or Key.Divide;
    }

        private void ReleaseRightClickOverrides()
    {
        var toRelease = _activeCombinedOverrides.Where(kvp => kvp.Value.RightClickOnly).ToList();
        foreach (var (key, state) in toRelease)
        {
            SendKey(state.TargetKey, false);
            LogDebug($"Force-release right-click override key: {key}");
            _activeCombinedOverrides.Remove(key);
        }
    }

    private void ReleaseAllOverrides()
    {
        if (_activeCombinedOverrides.Count == 0)
        {
            return;
        }

        foreach (var (key, state) in _activeCombinedOverrides.ToList())
        {
            SendKey(state.TargetKey, false);
            LogDebug($"Force-release combined override key: {key} -> {state.TargetKey}");
            _activeCombinedOverrides.Remove(key);
        }
    }
// ==================== STATE MANAGEMENT ====================
    
    private void ReleaseAllState()
    {
        
        ReleaseAllOverrides();
        ResetMouseStates();
        ReleaseCapsState();
        ReleaseHoldBreathState();
        
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
    private static void LogDebug(string message)
    {
        if (!ENABLE_DEBUG_LOGGING || _logQueue == null)
        {
            return;
        }
        
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var threadId = Environment.CurrentManagedThreadId;
        var entry = $"[{timestamp}] [T{threadId:D3}] {message}";
        
        // Non-blocking enqueue
        _logQueue.TryAdd(entry);
    }
}






