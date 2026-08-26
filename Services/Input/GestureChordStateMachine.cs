using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using MouseButton = sWinShortcuts.Models.MouseButton;
using Timer = System.Threading.Timer;

namespace sWinShortcuts.Services.Input;

/// <summary>
/// Alt gestures, right-click hold-breath, and Early Cancel state. Hook entry points are
/// synchronous and enqueue-only; timers recheck runtime/configuration epochs before enqueueing.
/// The dispatcher remains the sole owner of the physical right-button latch.
/// </summary>
internal sealed class GestureChordStateMachine : IInputCommandGuard, IDisposable
{
    private const int TIMER_IDLE = 0;
    private const int TIMER_ARMED = 1;
    private const int TIMER_FIRED = 2;
    private const int TIMER_CANCELLED = 3;
    private const int KEY_PRESS_MIN_MS = 31;
    private const int KEY_PRESS_MAX_MS = 53;
    private const int HOLD_BREATH_JITTER_MIN_MS = 15;
    private const int HOLD_BREATH_JITTER_MAX_MS = 36;
    private const int HOLD_BREATH_TAP_MIN_MS = 20;
    private const int HOLD_BREATH_TAP_MAX_MS = 30;
    private const int RNG_WARMUP_MIN_CALLS = 1;
    private const int RNG_WARMUP_MAX_CALLS = 5;
    private const double FIRE_TOLERANCE_MS = 2.0;
    private const long TOKEN_KIND_MASK = 0x7000000000000000;
    private const long TOKEN_ALT_MOUSE = 0x1000000000000000;
    private const long TOKEN_ALT_KEYBOARD = 0x2000000000000000;
    private const long TOKEN_HOLD_BREATH = 0x3000000000000000;
    private static readonly double TickToMilliseconds = 1000.0 / Stopwatch.Frequency;

    private readonly InputRuntimeState _runtime;
    private readonly IInputQueue _inputQueue;
    private readonly ThreadLocal<Random> _random;
    private readonly ILoggerService _logger;
    private readonly Func<bool> _isRightButtonPressed;
    private readonly Dictionary<MouseButton, MouseState> _mouseStates;
    private readonly Dictionary<Key, KeyboardState> _keyboardStates;
    private readonly object _holdBreathLock = new();
    private readonly Timer _holdBreathTimer;

    private volatile bool _altPressed;
    private long _altMouseGeneration = 1;
    private long _altKeyboardGeneration = 1;
    private long _pressTokenSequence;

    private bool _holdBreathPending;
    private Key? _holdBreathInjectedKey;
    private Key _holdBreathArmedKey;
    private HoldBreathMode _holdBreathArmedMode;
    private long _holdBreathArmedTick;
    private int _holdBreathArmedDelayMs;
    private long _holdBreathArmedForegroundGeneration;
    private long _holdBreathGeneration;
    private long _holdBreathConfigurationGeneration = 1;
    private bool _holdBreathPanicSuppressed;
    private int _panicConsumedKeyVk;
    private int _panicConsumedMouseButton;
    private int _panicKeyPhysicallyDownVk;
    private long _panicDerivationEpoch;
    private long _panicDerivationTicketSequence;
    private int _disposed;

    internal GestureChordStateMachine(
        InputRuntimeState runtime,
        IInputQueue inputQueue,
        ThreadLocal<Random> random,
        ILoggerService logger,
        Func<bool> isRightButtonPressed)
    {
        _runtime = runtime;
        _inputQueue = inputQueue;
        _random = random;
        _logger = logger;
        _isRightButtonPressed = isRightButtonPressed;
        _holdBreathTimer = new Timer(_ => OnHoldBreathTimerFired(), null, Timeout.Infinite, Timeout.Infinite);

        _mouseStates = new Dictionary<MouseButton, MouseState>
        {
            [MouseButton.Left] = new(this, MouseButton.Left),
            [MouseButton.Right] = new(this, MouseButton.Right),
            [MouseButton.Middle] = new(this, MouseButton.Middle),
            [MouseButton.XButton1] = new(this, MouseButton.XButton1),
            [MouseButton.XButton2] = new(this, MouseButton.XButton2)
        };

        _keyboardStates = [];
        foreach (var key in KeyCatalog.GetCommonKeys())
        {
            if (key is not (Key.LeftAlt or Key.RightAlt))
            {
                _keyboardStates[key] = new KeyboardState(this, key);
            }
        }
    }

    internal bool AltPressed => _altPressed;

    internal bool PanicDerivationPending => Volatile.Read(ref _panicDerivationEpoch) != 0;

    internal void SeedAltPressed(bool isPressed) => _altPressed = isPressed;

    internal void ObserveAlt(int vkCode, bool isKeyDown, bool isKeyUp, Func<int, bool> isPhysicallyDown)
    {
        if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (vkCode is not (0xA4 or 0xA5 or 0x12))
        {
            return;
        }

        if (isKeyDown)
        {
            _altPressed = true;
            return;
        }

        if (!isKeyUp)
        {
            return;
        }

        _altPressed = vkCode switch
        {
            0xA4 => isPhysicallyDown(0xA5),
            0xA5 => isPhysicallyDown(0xA4),
            _ => false
        };
        if (!_altPressed)
        {
            ResetMouseStates(preserveSuppressedUps: true);
            ResetKeyboardStates(preserveSuppressedUps: true);
        }
    }

    internal bool HandleAltMouse(int message, uint mouseData)
    {
        if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        var button = DecodeMouseButton(message, mouseData);
        if (!button.HasValue)
        {
            return false;
        }

        var state = _mouseStates[button.Value];
        if (IsMouseUp(message))
        {
            return HandleMouseUp(button.Value, state);
        }

        if (!IsMouseDown(message) || !_altPressed || !_runtime.ProfileInputGenerationIsCurrent())
        {
            return false;
        }

        var generation = Volatile.Read(ref _altMouseGeneration);
        var profile = _runtime.ActiveProfile;
        if (profile is not { IsEnabled: true } ||
            !profile.AltMouse.IsEnabled ||
            !profile.AltMouse.Bindings.TryGetValue(button.Value, out var binding) ||
            binding is null ||
            (!binding.TapKey.HasValue && !binding.HoldKey.HasValue))
        {
            return false;
        }

        CancelTimer(state);
        if (generation != Volatile.Read(ref _altMouseGeneration))
        {
            return false;
        }

        var press = new MousePress(
            profile,
            _runtime.ActiveProfileGeneration,
            generation,
            Stopwatch.GetTimestamp(),
            binding.TapKey,
            binding.HoldKey,
            Math.Max(10, profile.AltMouse.HoldThresholdMilliseconds));
        Volatile.Write(ref state.ActivePress, press);
        Interlocked.Exchange(ref state.SuppressNextUp, 1);
        Interlocked.Exchange(ref state.TimerState, TIMER_ARMED);
        if (press.HoldKey.HasValue && !TryArmTimer(state.Timer, press.HoldThresholdMs))
        {
            Interlocked.Exchange(ref state.ActivePress, null);
            Interlocked.Exchange(ref state.SuppressNextUp, 0);
            Interlocked.Exchange(ref state.TimerState, TIMER_CANCELLED);
            return false;
        }

        return true;
    }

    internal bool HandleAltKeyboard(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        if (!isKeyDown && !isKeyUp)
        {
            return false;
        }

        var key = KeyInteropUtilities.FromVirtualKey(vkCode);
        if (key is null || !_keyboardStates.TryGetValue(key.Value, out var state))
        {
            return false;
        }

        if (isKeyUp)
        {
            return HandleKeyboardUp(key.Value, state);
        }

        if (Volatile.Read(ref state.SuppressNextUp) != 0)
        {
            return true;
        }

        if (state.PhysicallyDown)
        {
            return false;
        }

        state.PhysicallyDown = true;
        if (!_altPressed || !_runtime.ProfileInputGenerationIsCurrent())
        {
            return false;
        }

        var generation = Volatile.Read(ref _altKeyboardGeneration);
        var profile = _runtime.ActiveProfile;
        if (profile is not { IsEnabled: true } ||
            !profile.AltKeyboard.IsEnabled ||
            !profile.AltKeyboard.Bindings.TryGetValue(key.Value, out var binding) ||
            binding is null ||
            (!binding.TapKey.HasValue && !binding.HoldKey.HasValue))
        {
            return false;
        }

        CancelTimer(state);
        if (generation != Volatile.Read(ref _altKeyboardGeneration))
        {
            return false;
        }

        var token = TOKEN_ALT_KEYBOARD |
            ((long)(ushort)key.Value << 32) |
            (Interlocked.Increment(ref _pressTokenSequence) & uint.MaxValue);
        var press = new KeyboardPress(
            profile,
            _runtime.ActiveProfileGeneration,
            generation,
            Stopwatch.GetTimestamp(),
            binding.TapKey,
            binding.HoldKey,
            Math.Max(10, profile.AltKeyboard.HoldThresholdMilliseconds),
            token);
        Volatile.Write(ref state.ActivePress, press);
        Interlocked.Exchange(ref state.SuppressNextUp, 1);
        Interlocked.Exchange(ref state.TimerState, TIMER_ARMED);
        if (press.HoldKey.HasValue && !TryArmTimer(state.Timer, press.HoldThresholdMs))
        {
            Interlocked.Exchange(ref state.ActivePress, null);
            Interlocked.Exchange(ref state.SuppressNextUp, 0);
            Interlocked.Exchange(ref state.TimerState, TIMER_CANCELLED);
            return false;
        }

        return true;
    }

    internal void HandleAltKeyboardPanicOverride(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var key = KeyInteropUtilities.FromVirtualKey(vkCode);
        if (key is null || !_keyboardStates.TryGetValue(key.Value, out var state))
        {
            return;
        }

        if (isKeyDown)
        {
            CancelTimer(state);
            Interlocked.Exchange(ref state.TimerState, TIMER_IDLE);
            var press = Interlocked.Exchange(ref state.ActivePress, null);
            if (press is not null)
            {
                press.Cancel();
            }
            Interlocked.Exchange(ref state.SuppressNextUp, 0);
        }
        else if (isKeyUp)
        {
            state.PhysicallyDown = false;
            Interlocked.Exchange(ref state.SuppressNextUp, 0);
        }
    }

    internal bool HandlePanicKey(int vkCode, bool isKeyDown, bool isKeyUp, bool rightButtonPressed)
    {
        if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        var consumedKeyVk = Volatile.Read(ref _panicConsumedKeyVk);
        if (consumedKeyVk != 0 && consumedKeyVk == vkCode)
        {
            if (isKeyUp)
            {
                Volatile.Write(ref _panicConsumedKeyVk, 0);
                if (Volatile.Read(ref _panicKeyPhysicallyDownVk) == vkCode)
                {
                    Volatile.Write(ref _panicKeyPhysicallyDownVk, 0);
                }
            }

            return true;
        }

        if (!isKeyDown)
        {
            if (isKeyUp && Volatile.Read(ref _panicKeyPhysicallyDownVk) == vkCode)
            {
                Volatile.Write(ref _panicKeyPhysicallyDownVk, 0);
            }
            return false;
        }

        var profile = _runtime.ActiveProfile;
        if (profile is null)
        {
            return false;
        }

        var trigger = profile.RightClickHoldBreath.PanicTrigger;
        if (trigger is not { Kind: InputTriggerKind.KeyboardKey } ||
            KeyInteropUtilities.ToVirtualKey(trigger.Key) != vkCode)
        {
            return false;
        }

        if (Volatile.Read(ref _panicKeyPhysicallyDownVk) == vkCode)
        {
            return false;
        }

        Volatile.Write(ref _panicKeyPhysicallyDownVk, vkCode);
        if (Volatile.Read(ref _panicDerivationEpoch) != 0 ||
            !_runtime.AdvancedModeEnabled ||
            !_runtime.ProfileInputGenerationIsCurrent() ||
            !profile.IsEnabled ||
            !profile.RightClickHoldBreath.IsEnabled ||
            !rightButtonPressed)
        {
            return false;
        }

        return Panic(profile, _runtime.ActiveProfileGeneration, rightButtonPressed, vkCode, 0);
    }

    internal bool HandlePanicMouse(int message, uint mouseData, bool rightButtonPressed)
    {
        if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        var button = DecodeMouseButton(message, mouseData);
        if (!button.HasValue)
        {
            return false;
        }

        var consumedButton = Volatile.Read(ref _panicConsumedMouseButton);
        if (consumedButton != 0 && consumedButton == (int)button.Value)
        {
            if (IsMouseUp(message))
            {
                Volatile.Write(ref _panicConsumedMouseButton, 0);
            }
            return true;
        }

        if (!IsMouseDown(message))
        {
            return false;
        }

        var profile = _runtime.ActiveProfile;
        if (!_runtime.AdvancedModeEnabled ||
            !_runtime.ProfileInputGenerationIsCurrent() ||
            profile is not { IsEnabled: true } ||
            !profile.RightClickHoldBreath.IsEnabled)
        {
            return false;
        }

        var trigger = profile.RightClickHoldBreath.PanicTrigger;
        return trigger.Kind == InputTriggerKind.MouseButton &&
               trigger.MouseButton == button.Value &&
               rightButtonPressed &&
               Panic(profile, _runtime.ActiveProfileGeneration, rightButtonPressed, 0, (int)button.Value);
    }

    internal void HandleRightButtonDown(bool rightButtonPressed)
    {
        if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var profile = _runtime.ActiveProfile;
        if (profile is not { IsEnabled: true } ||
            !profile.RightClickHoldBreath.IsEnabled ||
            !_runtime.AdvancedModeEnabled ||
            !_runtime.ProfileInputGenerationIsCurrent())
        {
            return;
        }

        var configurationGeneration = Volatile.Read(ref _holdBreathConfigurationGeneration);
        var settings = profile.RightClickHoldBreath;
        var foregroundGeneration = _runtime.ActiveProfileGeneration;
        var baseDelay = Math.Max(0, settings.DelayMilliseconds);
        var rng = WarmRandom();
        var jitter = baseDelay > 0 ? rng.Next(HOLD_BREATH_JITTER_MIN_MS, HOLD_BREATH_JITTER_MAX_MS + 1) : 0;
        var totalDelay = baseDelay + jitter;

        lock (_holdBreathLock)
        {
            if (configurationGeneration != Volatile.Read(ref _holdBreathConfigurationGeneration))
            {
                return;
            }

            CancelHoldBreathLocked();
            if (_runtime.IsDisposed ||
                !_runtime.IsRunning ||
                !rightButtonPressed ||
                _holdBreathPanicSuppressed ||
                !_runtime.AdvancedModeEnabled ||
                !_runtime.ProfileInputGenerationIsCurrent(profile, foregroundGeneration) ||
                !profile.IsEnabled ||
                !settings.IsEnabled)
            {
                return;
            }

            _holdBreathPending = true;
            _holdBreathArmedKey = settings.HoldBreathKey;
            _holdBreathArmedMode = settings.Mode;
            _holdBreathArmedForegroundGeneration = foregroundGeneration;
            if (totalDelay > 0)
            {
                _holdBreathArmedTick = Stopwatch.GetTimestamp();
                _holdBreathArmedDelayMs = totalDelay;
                if (!TryArmTimer(_holdBreathTimer, totalDelay))
                {
                    _holdBreathPending = false;
                }
            }
            else
            {
                ActivateHoldBreathLocked();
            }
        }
    }

    internal void HandleRightButtonUp()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_holdBreathLock)
        {
            CancelHoldBreathLocked();
            _holdBreathPanicSuppressed = false;
        }
    }

    internal void ReleaseHoldBreath()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_holdBreathLock)
        {
            Interlocked.Increment(ref _holdBreathConfigurationGeneration);
            CancelHoldBreathLocked();
        }
    }

    internal void ReleaseGestures(bool preserveSuppressedUps)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        ReleaseAltMouse(preserveSuppressedUps);
        ReleaseAltKeyboard(preserveSuppressedUps);
        if (!preserveSuppressedUps)
        {
            _altPressed = false;
        }
    }

    internal void ReleaseAltMouse(bool preserveSuppressedUps = true)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            ResetMouseStates(preserveSuppressedUps);
        }
    }

    internal void ReleaseAltKeyboard(bool preserveSuppressedUps = true)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            ResetKeyboardStates(preserveSuppressedUps);
        }
    }

    internal void ReleasePanic(bool preservePhysicalPairing)
    {
        lock (_holdBreathLock)
        {
            _holdBreathPanicSuppressed = false;
        }

        if (!preservePhysicalPairing)
        {
            Volatile.Write(ref _panicConsumedKeyVk, 0);
            Volatile.Write(ref _panicConsumedMouseButton, 0);
            Volatile.Write(ref _panicKeyPhysicallyDownVk, 0);
            Volatile.Write(ref _panicDerivationEpoch, 0);
        }
    }

    internal long BeginPanicDerivation()
    {
        var ticket = Interlocked.Increment(ref _panicDerivationTicketSequence);
        Volatile.Write(ref _panicDerivationEpoch, ticket);
        return ticket;
    }

    internal void RetirePanicDerivation(long ticket) =>
        Interlocked.CompareExchange(ref _panicDerivationEpoch, 0, ticket);

    internal void RederivePhysicalState(Func<int, bool> isPhysicallyDown)
    {
        _altPressed = isPhysicallyDown(0xA4) || isPhysicallyDown(0xA5);
        RederiveAltKeyboardPhysicalState(isPhysicallyDown);
        RederivePanicTriggerPhysicalState(isPhysicallyDown);
    }

    internal void RederiveAltKeyboardPhysicalState(Func<int, bool> isPhysicallyDown)
    {
        foreach (var (key, state) in _keyboardStates)
        {
            var vk = KeyInteropUtilities.ToVirtualKey(key);
            if (vk == 0)
            {
                continue;
            }

            state.PhysicallyDown = isPhysicallyDown(vk);
            if (!state.PhysicallyDown)
            {
                Interlocked.Exchange(ref state.SuppressNextUp, 0);
            }
        }
    }

    internal void RederivePanicTriggerPhysicalState(Func<int, bool> isPhysicallyDown)
    {
        var trigger = _runtime.ActiveProfile?.RightClickHoldBreath.PanicTrigger;
        if (trigger is not { Kind: InputTriggerKind.KeyboardKey })
        {
            Volatile.Write(ref _panicKeyPhysicallyDownVk, 0);
            return;
        }

        var vk = KeyInteropUtilities.ToVirtualKey(trigger.Value.Key);
        Volatile.Write(ref _panicKeyPhysicallyDownVk, vk != 0 && isPhysicallyDown(vk) ? vk : 0);
    }

    public bool CanExecute(in InputCommand command)
    {
        if (_runtime.IsDisposed ||
            Volatile.Read(ref _disposed) != 0 ||
            !_runtime.IsRunning ||
            command.ExpectedProfile is not { IsEnabled: true } profile ||
            !_runtime.ProfileInputGenerationIsCurrent(profile, command.ForegroundGeneration))
        {
            return false;
        }

        return (command.Token & TOKEN_KIND_MASK) switch
        {
            TOKEN_ALT_MOUSE => profile.AltMouse.IsEnabled &&
                command.Generation == Volatile.Read(ref _altMouseGeneration),
            TOKEN_ALT_KEYBOARD => profile.AltKeyboard.IsEnabled &&
                command.Generation == Volatile.Read(ref _altKeyboardGeneration) &&
                command.Acknowledgement?.IsCancelled != true,
            TOKEN_HOLD_BREATH => _runtime.AdvancedModeEnabled &&
                profile.RightClickHoldBreath.IsEnabled &&
                command.Generation == Volatile.Read(ref _holdBreathGeneration),
            _ => false
        };
    }

    internal void FireHoldBreathTimerForTesting()
    {
        Volatile.Write(
            ref _holdBreathArmedTick,
            Stopwatch.GetTimestamp() -
            (long)Math.Ceiling((_holdBreathArmedDelayMs + FIRE_TOLERANCE_MS) * Stopwatch.Frequency / 1000.0));
        OnHoldBreathTimerFired();
    }

    private bool HandleMouseUp(MouseButton button, MouseState state)
    {
        var suppressUp = Interlocked.Exchange(ref state.SuppressNextUp, 0) != 0;
        var press = Interlocked.Exchange(ref state.ActivePress, null);
        var finalState = Interlocked.Exchange(ref state.TimerState, TIMER_IDLE);
        TryCancelTimer(state.Timer);
        if (press is null || !GestureIsCurrent(press.Profile, press.ForegroundGeneration, press.Generation, altKeyboard: false))
        {
            return suppressUp;
        }

        var elapsedMs = (Stopwatch.GetTimestamp() - press.DownTick) * TickToMilliseconds;
        if (finalState != TIMER_FIRED)
        {
            if (press.HoldKey.HasValue && elapsedMs >= press.HoldThresholdMs)
            {
                EnqueueTap(press.HoldKey.Value, press.Profile, press.ForegroundGeneration, press.Generation,
                    TOKEN_ALT_MOUSE | (long)button);
            }
            else if (press.TapKey.HasValue)
            {
                EnqueueTap(press.TapKey.Value, press.Profile, press.ForegroundGeneration, press.Generation,
                    TOKEN_ALT_MOUSE | (long)button);
            }
        }

        return suppressUp;
    }

    private bool HandleKeyboardUp(Key key, KeyboardState state)
    {
        state.PhysicallyDown = false;
        var suppressUp = Interlocked.Exchange(ref state.SuppressNextUp, 0) != 0;
        var press = Interlocked.Exchange(ref state.ActivePress, null);
        var finalState = Interlocked.Exchange(ref state.TimerState, TIMER_IDLE);
        TryCancelTimer(state.Timer);
        if (press is null || !GestureIsCurrent(press.Profile, press.ForegroundGeneration, press.Generation, altKeyboard: true))
        {
            return suppressUp;
        }

        var elapsedMs = (Stopwatch.GetTimestamp() - press.DownTick) * TickToMilliseconds;
        if (finalState != TIMER_FIRED)
        {
            if (press.HoldKey.HasValue && elapsedMs >= press.HoldThresholdMs)
            {
                EnqueueTap(press.HoldKey.Value, press.Profile, press.ForegroundGeneration, press.Generation, press.Token,
                    acknowledgement: press);
            }
            else if (press.TapKey.HasValue)
            {
                EnqueueTap(press.TapKey.Value, press.Profile, press.ForegroundGeneration, press.Generation, press.Token,
                    acknowledgement: press);
            }
        }

        return suppressUp;
    }

    private void OnMouseTimerFired(MouseButton button, MouseState state)
    {
        var press = Volatile.Read(ref state.ActivePress);
        if (press is null ||
            !GestureIsCurrent(press.Profile, press.ForegroundGeneration, press.Generation, altKeyboard: false) ||
            (Stopwatch.GetTimestamp() - press.DownTick) * TickToMilliseconds < press.HoldThresholdMs - FIRE_TOLERANCE_MS ||
            Interlocked.CompareExchange(ref state.TimerState, TIMER_FIRED, TIMER_ARMED) != TIMER_ARMED ||
            !press.HoldKey.HasValue ||
            _runtime.IsDisposed)
        {
            return;
        }

        EnqueueTap(press.HoldKey.Value, press.Profile, press.ForegroundGeneration, press.Generation,
            TOKEN_ALT_MOUSE | (long)button);
    }

    private void OnKeyboardTimerFired(Key key, KeyboardState state)
    {
        var press = Volatile.Read(ref state.ActivePress);
        if (press is null ||
            !GestureIsCurrent(press.Profile, press.ForegroundGeneration, press.Generation, altKeyboard: true) ||
            (Stopwatch.GetTimestamp() - press.DownTick) * TickToMilliseconds < press.HoldThresholdMs - FIRE_TOLERANCE_MS ||
            Interlocked.CompareExchange(ref state.TimerState, TIMER_FIRED, TIMER_ARMED) != TIMER_ARMED ||
            !press.HoldKey.HasValue ||
            _runtime.IsDisposed)
        {
            return;
        }

        EnqueueTap(press.HoldKey.Value, press.Profile, press.ForegroundGeneration, press.Generation, press.Token,
            acknowledgement: press);
    }

    private bool GestureIsCurrent(Profile profile, long foregroundGeneration, long generation, bool altKeyboard) =>
        Volatile.Read(ref _disposed) == 0 &&
        _altPressed &&
        _runtime.ProfileInputGenerationIsCurrent(profile, foregroundGeneration) &&
        (altKeyboard
            ? profile.AltKeyboard.IsEnabled && generation == Volatile.Read(ref _altKeyboardGeneration)
            : profile.AltMouse.IsEnabled && generation == Volatile.Read(ref _altMouseGeneration));

    private bool Panic(Profile profile, long foregroundGeneration, bool rightButtonPressed, int keyVk, int mouseButton)
    {
        lock (_holdBreathLock)
        {
            if (_runtime.IsDisposed ||
                !_runtime.AdvancedModeEnabled ||
                !rightButtonPressed ||
                !_runtime.ProfileInputGenerationIsCurrent(profile, foregroundGeneration) ||
                !profile.IsEnabled ||
                !profile.RightClickHoldBreath.IsEnabled ||
                !profile.RightClickHoldBreath.SuppressEarlyCancelInput ||
                _holdBreathPanicSuppressed ||
                (!_holdBreathPending && _holdBreathInjectedKey is null))
            {
                return false;
            }

            _holdBreathPanicSuppressed = true;
            CancelHoldBreathLocked();
            if (keyVk != 0)
            {
                Volatile.Write(ref _panicConsumedKeyVk, keyVk);
            }
            else
            {
                Volatile.Write(ref _panicConsumedMouseButton, mouseButton);
            }

            if (_logger.IsEnabled)
            {
                _logger.Log("HoldBreath panic: cancelled; re-arm vetoed until right-button-up");
            }
            return true;
        }
    }

    private void OnHoldBreathTimerFired()
    {
        lock (_holdBreathLock)
        {
            if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0 || !_holdBreathPending || !_runtime.IsRunning)
            {
                return;
            }

            var elapsedMs = (Stopwatch.GetTimestamp() - _holdBreathArmedTick) * TickToMilliseconds;
            if (elapsedMs < _holdBreathArmedDelayMs - FIRE_TOLERANCE_MS)
            {
                return;
            }

            if (!_isRightButtonPressed())
            {
                _holdBreathPending = false;
                return;
            }

            var profile = _runtime.ActiveProfile;
            if (!_runtime.AdvancedModeEnabled ||
                profile is not { IsEnabled: true } ||
                !profile.RightClickHoldBreath.IsEnabled ||
                !_runtime.ProfileInputGenerationIsCurrent(profile, _holdBreathArmedForegroundGeneration))
            {
                _holdBreathPending = false;
                return;
            }

            ActivateHoldBreathLocked();
        }
    }

    private void ActivateHoldBreathLocked()
    {
        _holdBreathPending = false;
        var generation = Interlocked.Increment(ref _holdBreathGeneration);
        var profile = _runtime.ActiveProfile;
        if (_runtime.IsDisposed || profile is null)
        {
            return;
        }

        var token = TOKEN_HOLD_BREATH | (generation & 0x0FFFFFFFFFFFFFFF);
        if (_holdBreathArmedMode == HoldBreathMode.Hold)
        {
            _holdBreathInjectedKey = _holdBreathArmedKey;
            _inputQueue.Enqueue(new InputCommand(
                _holdBreathArmedKey,
                true,
                Guard: this,
                Generation: generation,
                ForegroundGeneration: _holdBreathArmedForegroundGeneration,
                ExpectedProfile: profile,
                Token: token));
        }
        else
        {
            EnqueueTap(
                _holdBreathArmedKey,
                profile,
                _holdBreathArmedForegroundGeneration,
                generation,
                token,
                WarmRandom().Next(HOLD_BREATH_TAP_MIN_MS, HOLD_BREATH_TAP_MAX_MS + 1));
        }
    }

    private void CancelHoldBreathLocked()
    {
        _holdBreathPending = false;
        _holdBreathArmedForegroundGeneration = 0;
        TryCancelTimer(_holdBreathTimer);
        Interlocked.Increment(ref _holdBreathGeneration);
        if (_holdBreathInjectedKey is { } key)
        {
            _holdBreathInjectedKey = null;
            _inputQueue.Enqueue(new InputCommand(key, false));
        }
    }

    private void EnqueueTap(
        Key key,
        Profile profile,
        long foregroundGeneration,
        long generation,
        long token,
        int duration = 0,
        InputCommandAcknowledgement? acknowledgement = null)
    {
        if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        duration = duration == 0 ? WarmRandom().Next(KEY_PRESS_MIN_MS, KEY_PRESS_MAX_MS + 1) : duration;
        var down = new InputCommand(
            key,
            true,
            Guard: this,
            Generation: generation,
            ForegroundGeneration: foregroundGeneration,
            ExpectedProfile: profile,
            Acknowledgement: acknowledgement,
            Token: token);
        var up = new InputCommand(
            key,
            false,
            DelayBeforeMs: duration,
            Acknowledgement: acknowledgement,
            RequireAcknowledgement: acknowledgement is not null);
        _inputQueue.EnqueuePair(down, up);
    }

    private Random WarmRandom()
    {
        var rng = _random.Value!;
        var warmupCalls = rng.Next(RNG_WARMUP_MIN_CALLS, RNG_WARMUP_MAX_CALLS + 1);
        for (var i = 0; i < warmupCalls; i++)
        {
            rng.Next();
        }
        return rng;
    }

    private void ResetMouseStates(bool preserveSuppressedUps)
    {
        Interlocked.Increment(ref _altMouseGeneration);
        foreach (var state in _mouseStates.Values)
        {
            CancelTimer(state);
            Interlocked.Exchange(ref state.TimerState, TIMER_IDLE);
            Interlocked.Exchange(ref state.ActivePress, null);
            if (!preserveSuppressedUps)
            {
                Interlocked.Exchange(ref state.SuppressNextUp, 0);
            }
        }
    }

    private void ResetKeyboardStates(bool preserveSuppressedUps)
    {
        Interlocked.Increment(ref _altKeyboardGeneration);
        foreach (var state in _keyboardStates.Values)
        {
            CancelTimer(state);
            Interlocked.Exchange(ref state.TimerState, TIMER_IDLE);
            Interlocked.Exchange(ref state.ActivePress, null);
            if (!preserveSuppressedUps)
            {
                Interlocked.Exchange(ref state.SuppressNextUp, 0);
                state.PhysicallyDown = false;
            }
        }
    }

    private void CancelTimer(FeatureTimerState state)
    {
        Interlocked.Exchange(ref state.TimerState, TIMER_CANCELLED);
        TryCancelTimer(state.Timer);
    }

    private bool TryArmTimer(Timer timer, int dueTime)
    {
        if (_runtime.IsDisposed || !_runtime.IsRunning || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            return timer.Change(dueTime, Timeout.Infinite);
        }
        catch (ObjectDisposedException) when (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }
    }

    private bool TryCancelTimer(Timer timer)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        try
        {
            return timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }
    }

    private static bool IsMouseDown(int message) => message is
        NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or
        NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN;

    private static bool IsMouseUp(int message) => message is
        NativeMethods.WM_LBUTTONUP or NativeMethods.WM_RBUTTONUP or
        NativeMethods.WM_MBUTTONUP or NativeMethods.WM_XBUTTONUP;

    private static MouseButton? DecodeMouseButton(int message, uint mouseData) => message switch
    {
        NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP => MouseButton.Left,
        NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP => MouseButton.Right,
        NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP => MouseButton.Middle,
        NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP =>
            ((mouseData >> 16) & 0xFFFF) == 2 ? MouseButton.XButton2 : MouseButton.XButton1,
        _ => null
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _altMouseGeneration);
        Interlocked.Increment(ref _altKeyboardGeneration);
        Interlocked.Increment(ref _holdBreathGeneration);
        _holdBreathTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _holdBreathTimer.Dispose();
        foreach (var state in _mouseStates.Values)
        {
            state.Timer.Change(Timeout.Infinite, Timeout.Infinite);
            state.Timer.Dispose();
        }
        foreach (var state in _keyboardStates.Values)
        {
            state.Timer.Change(Timeout.Infinite, Timeout.Infinite);
            state.Timer.Dispose();
        }
    }

    private abstract class FeatureTimerState
    {
        internal int TimerState = TIMER_IDLE;
        internal int SuppressNextUp;
        internal readonly Timer Timer;

        protected FeatureTimerState(TimerCallback callback) =>
            Timer = new Timer(callback, null, Timeout.Infinite, Timeout.Infinite);
    }

    private sealed class MouseState : FeatureTimerState
    {
        internal MousePress? ActivePress;

        internal MouseState(GestureChordStateMachine owner, MouseButton button)
            : base(_ => owner.OnMouseTimerFired(button, owner._mouseStates[button]))
        {
        }
    }

    private sealed class KeyboardState : FeatureTimerState
    {
        internal KeyboardPress? ActivePress;
        internal volatile bool PhysicallyDown;

        internal KeyboardState(GestureChordStateMachine owner, Key key)
            : base(_ => owner.OnKeyboardTimerFired(key, owner._keyboardStates[key]))
        {
        }
    }

    private sealed record MousePress(
        Profile Profile,
        long ForegroundGeneration,
        long Generation,
        long DownTick,
        Key? TapKey,
        Key? HoldKey,
        int HoldThresholdMs);

    private sealed class KeyboardPress(
        Profile profile,
        long foregroundGeneration,
        long generation,
        long downTick,
        Key? tapKey,
        Key? holdKey,
        int holdThresholdMs,
        long token) : InputCommandAcknowledgement
    {
        internal Profile Profile { get; } = profile;
        internal long ForegroundGeneration { get; } = foregroundGeneration;
        internal long Generation { get; } = generation;
        internal long DownTick { get; } = downTick;
        internal Key? TapKey { get; } = tapKey;
        internal Key? HoldKey { get; } = holdKey;
        internal int HoldThresholdMs { get; } = holdThresholdMs;
        internal long Token { get; } = token;
    }
}
