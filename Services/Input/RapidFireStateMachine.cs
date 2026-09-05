using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using Timer = System.Threading.Timer;

namespace sWinShortcuts.Services.Input;

/// <summary>
/// Sticky Rapid Fire ownership and one-shot click cadence. Hook entry points are synchronous;
/// clicks run only on the timer thread. Mutations return whether status may have changed so the
/// dispatcher can raise its public event after releasing the profile lock.
/// </summary>
internal sealed class RapidFireStateMachine : IDisposable
{
    private const int TIMER_IDLE = 0;
    private const int TIMER_ARMED = 1;
    private const int TIMER_FIRED = 2;
    private const int TIMER_CANCELLED = 3;
    private const int HOLD_MIN_MS = 10;
    private const int HOLD_MAX_MS = 20;
    private const double FIRE_TOLERANCE_MS = 2.0;
    private static readonly double TickToMilliseconds = 1000.0 / Stopwatch.Frequency;

    private readonly InputRuntimeState _runtime;
    private readonly IInputSender _inputSender;
    private readonly ThreadLocal<Random> _random;
    private readonly ILoggerService _logger;
    private readonly object _profileLock;
    private readonly Timer _timer;

    private int _toggleVk;
    private bool _toggleDownLatched;
    private int _hookSeenToggleVk;
    private volatile bool _armed;
    private long _armEpoch;
    private long _armedEpoch;
    private volatile bool _physicalLeftDown;
    private volatile Profile? _ownerProfile;
    private long _generation;
    private long _foregroundGeneration;
    private int _intervalMs;
    private int _jitterMs;
    private int _timerState = TIMER_IDLE;
    private long _timerGeneration;
    private long _armedTick;
    private int _armedDelayMs;
    private int _disposed;

    internal RapidFireStateMachine(
        InputRuntimeState runtime,
        IInputSender inputSender,
        ThreadLocal<Random> random,
        ILoggerService logger,
        object profileLock)
    {
        _runtime = runtime;
        _inputSender = inputSender;
        _random = random;
        _logger = logger;
        _profileLock = profileLock;
        _timer = new Timer(_ => OnTimerFired(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal bool SetToggleKey(Key? key)
    {
        var vk = key.HasValue ? KeyInteropUtilities.ToVirtualKey(key.Value) : 0;
        if (vk is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C)
        {
            vk = 0;
        }

        if (Volatile.Read(ref _toggleVk) == vk)
        {
            return false;
        }

        Volatile.Write(ref _toggleVk, vk);
        return Release(preservePhysicalPairing: true, reason: "toggle key reassigned");
    }

    internal bool HandleToggleKey(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        var toggleVk = Volatile.Read(ref _toggleVk);
        if (toggleVk != _hookSeenToggleVk)
        {
            _hookSeenToggleVk = toggleVk;
            _toggleDownLatched = false;
        }

        if (toggleVk == 0 || vkCode != toggleVk)
        {
            return false;
        }

        if (isKeyUp)
        {
            _toggleDownLatched = false;
            return false;
        }

        if (!isKeyDown || _toggleDownLatched)
        {
            return false;
        }

        _toggleDownLatched = true;
        if (IsReady())
        {
            return Release(preservePhysicalPairing: true, reason: "toggle-off");
        }

        var armEpoch = Volatile.Read(ref _armEpoch);
        var expectedProfile = _runtime.ActiveProfile;
        var expectedActiveGeneration = _runtime.ActiveProfileGeneration;
        var expectedPublishedGeneration = _runtime.PublishedForegroundGeneration;

        lock (_profileLock)
        {
            var profile = _runtime.ActiveProfile;
            if (Volatile.Read(ref _toggleVk) == toggleVk &&
                armEpoch == Volatile.Read(ref _armEpoch) &&
                _runtime.IsRunning &&
                !_runtime.IsDisposed &&
                _runtime.AdvancedModeEnabled &&
                ReferenceEquals(profile, expectedProfile) &&
                expectedActiveGeneration == expectedPublishedGeneration &&
                expectedActiveGeneration == _runtime.ActiveProfileGeneration &&
                expectedPublishedGeneration == _runtime.PublishedForegroundGeneration &&
                profile is { IsEnabled: true } &&
                profile.RapidFire.IsEnabled)
            {
                _ownerProfile = profile;
                Volatile.Write(ref _armedEpoch, armEpoch);
                _armed = true;
                if (_logger.IsEnabled)
                {
                    _logger.Log($"Rapid Fire armed for profile: {profile.Name}");
                }
                return true;
            }

            if (Volatile.Read(ref _toggleVk) == toggleVk &&
                expectedActiveGeneration == expectedPublishedGeneration &&
                expectedActiveGeneration == _runtime.ActiveProfileGeneration &&
                expectedPublishedGeneration == _runtime.PublishedForegroundGeneration &&
                TryGetLiveOwner(out _))
            {
                return Release(preservePhysicalPairing: true, reason: "toggle-off");
            }
        }

        return false;
    }

    internal void HandleLeftButton(bool isDown, bool allowStart)
    {
        if (!isDown)
        {
            _physicalLeftDown = false;
            CancelPress();
            return;
        }

        var freshPress = !_physicalLeftDown;
        _physicalLeftDown = true;
        if (!freshPress || !allowStart || !IsReady() || !_runtime.AdvancedModeEnabled ||
            !_runtime.ProfileInputGenerationIsCurrent())
        {
            return;
        }

        var profile = _runtime.ActiveProfile;
        if (profile is not { IsEnabled: true } ||
            !ReferenceEquals(profile, _ownerProfile) ||
            !profile.RapidFire.IsEnabled)
        {
            return;
        }

        CancelTimer();
        var generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _foregroundGeneration, _runtime.ActiveProfileGeneration);
        _intervalMs = Math.Clamp(
            profile.RapidFire.IntervalMilliseconds,
            RapidFireSettings.MinIntervalMilliseconds,
            RapidFireSettings.MaxIntervalMilliseconds);
        _jitterMs = Math.Clamp(profile.RapidFire.JitterMilliseconds, 0, RapidFireSettings.MaxJitterMilliseconds);
        Schedule(generation);
        if (_logger.IsEnabled)
        {
            _logger.Log($"Rapid Fire press started: first synthetic click due in {_armedDelayMs} ms (interval={_intervalMs}, jitter={_jitterMs})");
        }
    }

    internal void SeedPhysicalLeftButton(bool isDown) => _physicalLeftDown = isDown;

    internal void SeedTogglePhysicalState(Func<int, bool> isPhysicalKeyDown)
    {
        var toggleVk = Volatile.Read(ref _toggleVk);
        _hookSeenToggleVk = toggleVk;
        _toggleDownLatched = toggleVk != 0 && isPhysicalKeyDown(toggleVk);
    }

    internal void CancelPress()
    {
        Interlocked.Increment(ref _generation);
        CancelTimer();
    }

    internal bool Release(bool preservePhysicalPairing, string? reason = null)
    {
        var wasArmed = _armed || _ownerProfile is not null;
        Interlocked.Increment(ref _armEpoch);
        CancelPress();
        _armed = false;
        _ownerProfile = null;
        if (!preservePhysicalPairing)
        {
            _physicalLeftDown = false;
        }

        if (wasArmed && _logger.IsEnabled)
        {
            _logger.Log($"Rapid Fire disarmed{(reason is null ? string.Empty : $" ({reason})")}");
        }

        return wasArmed;
    }

    internal bool ReleaseOwnedBy(Profile profile)
    {
        lock (_profileLock)
        {
            return ReferenceEquals(_ownerProfile, profile) &&
                Release(preservePhysicalPairing: true, reason: "owner settings changed/removed");
        }
    }

    internal RapidFireArmStatus GetStatus()
    {
        if (!TryGetLiveOwner(out var owner))
        {
            return RapidFireArmStatus.Off;
        }

        return _runtime.ProfileInputGenerationIsCurrent() && ReferenceEquals(_runtime.ActiveProfile, owner)
            ? RapidFireArmStatus.Ready
            : RapidFireArmStatus.ArmedNotReady;
    }

    internal static int CalculateSuccessorDelay(int targetDelayMs, double sendElapsedMs) =>
        sendElapsedMs < targetDelayMs
            ? Math.Max(1, (int)Math.Ceiling(targetDelayMs - sendElapsedMs))
            : targetDelayMs;

    internal void FireTimerForTesting()
    {
        Volatile.Write(
            ref _armedTick,
            Stopwatch.GetTimestamp() -
            (long)Math.Ceiling((_armedDelayMs + FIRE_TOLERANCE_MS) * Stopwatch.Frequency / 1000.0));
        OnTimerFired();
    }

    internal void ConfigureForTesting(Profile profile, long foregroundGeneration, bool armed = true)
    {
        _runtime.SetActiveProfile(profile, foregroundGeneration);
        _runtime.SetForegroundIdentity(IntPtr.Zero, 0, profile.Executable, foregroundGeneration);
        Release(preservePhysicalPairing: false);
        _ownerProfile = armed ? profile : null;
        Volatile.Write(ref _armedEpoch, Volatile.Read(ref _armEpoch));
        _armed = armed;
    }

    private bool TryGetLiveOwner(out Profile? owner)
    {
        owner = _ownerProfile;
        return _runtime.IsRunning &&
               !_runtime.IsDisposed &&
               _armed &&
               Volatile.Read(ref _armedEpoch) == Volatile.Read(ref _armEpoch) &&
               _runtime.AdvancedModeEnabled &&
               owner is { IsEnabled: true } && owner.RapidFire.IsEnabled;
    }

    private bool IsReady() =>
        TryGetLiveOwner(out var owner) &&
        _runtime.ProfileInputGenerationIsCurrent() &&
        ReferenceEquals(_runtime.ActiveProfile, owner);

    private bool IsCurrent(long generation, Profile profile, long foregroundGeneration) =>
        !_runtime.IsDisposed &&
        _runtime.IsRunning &&
        _runtime.AdvancedModeEnabled &&
        IsReady() &&
        _physicalLeftDown &&
        generation == Volatile.Read(ref _generation) &&
        foregroundGeneration == _runtime.PublishedForegroundGeneration &&
        foregroundGeneration == _runtime.ActiveProfileGeneration &&
        ReferenceEquals(_ownerProfile, profile) &&
        ReferenceEquals(_runtime.ActiveProfile, profile) &&
        profile.IsEnabled &&
        profile.RapidFire.IsEnabled;

    private void Schedule(long generation, double sendElapsedMs = 0)
    {
        var profile = _ownerProfile;
        var foregroundGeneration = Volatile.Read(ref _foregroundGeneration);
        if (profile is null || !IsCurrent(generation, profile, foregroundGeneration) || _runtime.IsDisposed)
        {
            return;
        }

        var targetDelay = _intervalMs + (_jitterMs == 0 ? 0 : _random.Value!.Next(_jitterMs + 1));
        var delay = CalculateSuccessorDelay(targetDelay, sendElapsedMs);
        Volatile.Write(ref _timerGeneration, generation);
        Volatile.Write(ref _armedTick, Stopwatch.GetTimestamp());
        Volatile.Write(ref _armedDelayMs, delay);
        Interlocked.Exchange(ref _timerState, TIMER_ARMED);
        if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Exchange(ref _timerState, TIMER_CANCELLED);
            return;
        }

        try
        {
            _timer.Change(delay, Timeout.Infinite);
        }
        catch (ObjectDisposedException) when (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    private void OnTimerFired()
    {
        var delay = Volatile.Read(ref _armedDelayMs);
        var elapsedMs = (Stopwatch.GetTimestamp() - Volatile.Read(ref _armedTick)) * TickToMilliseconds;
        if (elapsedMs < delay - FIRE_TOLERANCE_MS ||
            Interlocked.CompareExchange(ref _timerState, TIMER_FIRED, TIMER_ARMED) != TIMER_ARMED)
        {
            return;
        }

        var generation = Volatile.Read(ref _timerGeneration);
        var foregroundGeneration = Volatile.Read(ref _foregroundGeneration);
        var profile = _ownerProfile;
        if (profile is null || !IsCurrent(generation, profile, foregroundGeneration) || _runtime.IsDisposed)
        {
            return;
        }

        if (_logger.IsEnabled)
        {
            _logger.Log($"Rapid Fire timer fired: elapsed={elapsedMs:F1} ms, armed delay={delay} ms");
        }

        var clickStart = Stopwatch.GetTimestamp();
        try
        {
            var holdMilliseconds = _random.Value!.Next(HOLD_MIN_MS, HOLD_MAX_MS + 1);
            if (_runtime.IsDisposed || !IsCurrent(generation, profile, foregroundGeneration))
            {
                return;
            }

            // WindowsInputSender logs which SendInput call failed; this bool adds no useful detail.
            _inputSender.SendLeftClick(holdMilliseconds);
        }
        catch (Exception ex)
        {
            Log($"Rapid Fire click injection error: {ex.Message}");
            return;
        }

        if (_runtime.IsDisposed)
        {
            return;
        }

        Schedule(generation, (Stopwatch.GetTimestamp() - clickStart) * TickToMilliseconds);
    }

    private void CancelTimer()
    {
        Interlocked.Exchange(ref _timerState, TIMER_CANCELLED);
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
            {
            }
        }
    }

    private void Log(string message)
    {
        if (_logger.IsEnabled)
        {
            _logger.Log(message);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _timerState, TIMER_CANCELLED);
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _timer.Dispose();
    }
}
