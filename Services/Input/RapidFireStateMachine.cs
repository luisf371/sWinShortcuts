using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using MouseButton = sWinShortcuts.Models.MouseButton;
using Timer = System.Threading.Timer;

namespace sWinShortcuts.Services.Input;

/// <summary>
/// Sticky Rapid Fire ownership and one-shot click cadence. Hook entry points are synchronous;
/// clicks run only on the timer thread. The physical DOWN passes through as shot 1, so each press
/// first releases it after a normal click hold, then clicks on a cadence anchored to the press.
/// Mutations return whether status may have changed so the
/// dispatcher can raise its public event after releasing the profile lock.
/// </summary>
internal sealed class RapidFireStateMachine : IDisposable
{
    private const int TIMER_IDLE = 0;
    private const int TIMER_ARMED = 1;
    private const int TIMER_FIRED = 2;
    private const int TIMER_CANCELLED = 3;
    internal const int HOLD_MIN_MS = 10;
    internal const int HOLD_MAX_MS = 20;
    private const double FIRE_TOLERANCE_MS = 2.0;
    // Shortest release gap in steady cadence (MinIntervalMilliseconds - HOLD_MAX_MS); keeps a late
    // initial release from merging into the first synthetic DOWN.
    internal const int RELEASE_GAP_MIN_MS = RapidFireSettings.MinIntervalMilliseconds - HOLD_MAX_MS;
    private static readonly double TickToMilliseconds = 1000.0 / Stopwatch.Frequency;

    private readonly InputRuntimeState _runtime;
    private readonly IInputSender _inputSender;
    private readonly ThreadLocal<Random> _random;
    private readonly ILoggerService _logger;
    private readonly object _profileLock;
    private readonly Timer _timer;
    private readonly Lock _timerCallbackLock = new();

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
    private long _requestedPressGeneration;
    private long _pressTick;
    private bool _releasePending;
    private int _firstClickDelayMs;
    private long _timerGeneration;
    private long _armedTick;
    private int _armedDelayMs;
    private int _disposed;
    // Press generation whose synthetic DOWN was delivered without its UP; 0 when nothing is owed.
    private long _owedUpGeneration;
    // Set when a send fails, cleared by the next delivered one; keeps the status dot off Ready.
    private volatile bool _deliveryFaulted;
    private readonly Action? _statusChanged;
    // Logical (post-swap) right-button state, read by the hook and timer threads.
    private readonly Func<bool> _isRightButtonHeld;
    // Whether the current press was started under "only while right button is held".
    private volatile bool _pressRequiresRightButton;

    internal RapidFireStateMachine(
        InputRuntimeState runtime,
        IInputSender inputSender,
        ThreadLocal<Random> random,
        ILoggerService logger,
        object profileLock,
        Action? statusChanged = null,
        Func<bool>? isRightButtonHeld = null)
    {
        _runtime = runtime;
        _inputSender = inputSender;
        _random = random;
        _logger = logger;
        _profileLock = profileLock;
        _statusChanged = statusChanged;
        _isRightButtonHeld = isRightButtonHeld ?? (static () => false);
        _timer = new Timer(_ => OnTimerFired(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal bool SetToggleKey(Key? key)
    {
        key = KeyInteropUtilities.NormalizeAppToggleKey(key);
        var vk = key.HasValue ? KeyInteropUtilities.ToVirtualKey(key.Value) : 0;

        if (Volatile.Read(ref _toggleVk) == vk)
        {
            return false;
        }

        Volatile.Write(ref _toggleVk, vk);
        return Release(preservePhysicalPairing: true, reason: "toggle key reassigned");
    }

    internal bool HandleToggleKey(int vkCode, bool isKeyDown, bool isKeyUp, bool allowToggle = true)
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
        if (!allowToggle) return false;
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
        // Any physical transition settles an owed synthetic UP (see SettleOwedUp).
        if (Volatile.Read(ref _owedUpGeneration) != 0) Interlocked.Exchange(ref _owedUpGeneration, 0);
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

        // A gated press starts only with the right button already held; pressing it later while
        // left is held does not start one (a fresh left press is required).
        var requiresRightButton = profile.RapidFire.RequireRightButton;
        if (requiresRightButton && !_isRightButtonHeld())
        {
            return;
        }

        _pressRequiresRightButton = requiresRightButton;
        var generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _foregroundGeneration, _runtime.ActiveProfileGeneration);
        _intervalMs = Math.Clamp(
            profile.RapidFire.IntervalMilliseconds,
            RapidFireSettings.MinIntervalMilliseconds,
            RapidFireSettings.MaxIntervalMilliseconds);
        _jitterMs = Math.Clamp(profile.RapidFire.JitterMilliseconds, 0, RapidFireSettings.MaxJitterMilliseconds);
        Volatile.Write(ref _pressTick, Stopwatch.GetTimestamp());
        // Publish the request after its settings. Only timer workers own cadence state.
        Volatile.Write(ref _requestedPressGeneration, generation);
        ChangeTimer(0, generation);
    }

    internal void SeedPhysicalLeftButton(bool isDown) => _physicalLeftDown = isDown;

    // Hook thread. Releasing the right button ends a gated press for good: cancelling latches it, so
    // a quick release-and-re-press between timer wakes cannot resume the old burst.
    internal void HandleRightButtonReleased()
    {
        if (_pressRequiresRightButton) CancelPress();
    }

    internal void SeedTogglePhysicalState(Func<int, bool> isPhysicalKeyDown)
    {
        var toggleVk = Volatile.Read(ref _toggleVk);
        _hookSeenToggleVk = toggleVk;
        _toggleDownLatched = toggleVk != 0 && isPhysicalKeyDown(toggleVk);
    }

    internal void CancelPress()
    {
        // An old cancellation must not erase a newer press's request or timer wakeup.
        Interlocked.Increment(ref _generation);
    }

    internal bool Release(bool preservePhysicalPairing, string? reason = null)
    {
        var wasArmed = _armed || _ownerProfile is not null;
        Interlocked.Increment(ref _armEpoch);
        CancelPress();
        _armed = false;
        _ownerProfile = null;
        _deliveryFaulted = false;
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

    internal bool CancelPressOwnedBy(Profile profile)
    {
        if (!ReferenceEquals(_ownerProfile, profile)) return false;
        CancelPress();
        return true;
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

        // A burst stopped by a delivery failure is armed but not delivering, so never report Ready.
        return !_deliveryFaulted &&
               _runtime.ProfileInputGenerationIsCurrent() && ReferenceEquals(_runtime.ActiveProfile, owner)
            ? RapidFireArmStatus.Ready
            : RapidFireArmStatus.ArmedNotReady;
    }

    internal static int CalculateSuccessorDelay(int targetDelayMs, double sendElapsedMs) =>
        sendElapsedMs < targetDelayMs
            ? Math.Max(1, (int)Math.Ceiling(targetDelayMs - sendElapsedMs))
            : targetDelayMs;

    internal static int CalculatePressRelativeDelay(int targetDelayMs, double sincePressMs) =>
        Math.Max(1, (int)Math.Ceiling(targetDelayMs - sincePressMs));

    internal void FireTimerForTesting()
    {
        if (Volatile.Read(ref _requestedPressGeneration) != Volatile.Read(ref _timerGeneration))
        {
            OnTimerFired();
        }

        // The physical-press release is its own timer phase ahead of the first click.
        if (FastForwardAndFireForTesting())
        {
            FastForwardAndFireForTesting();
        }
    }

    private bool FastForwardAndFireForTesting()
    {
        Volatile.Write(
            ref _armedTick,
            Stopwatch.GetTimestamp() -
            (long)Math.Ceiling((_armedDelayMs + FIRE_TOLERANCE_MS) * Stopwatch.Frequency / 1000.0));
        return OnTimerFired();
    }

    internal void ConfigureForTesting(Profile profile, long foregroundGeneration, bool armed = true)
    {
        _runtime.SetActiveProfile(profile, foregroundGeneration);
        var foreground = _runtime.ForegroundIdentity;
        _runtime.SetForegroundIdentity(foreground?.WindowHandle ?? IntPtr.Zero,
            foreground?.ProcessId ?? 0, profile.Executable, foregroundGeneration);
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
        !_runtime.AutomationInhibited &&
        _runtime.IsRunning &&
        _runtime.AdvancedModeEnabled &&
        IsReady() &&
        _physicalLeftDown &&
        (!_pressRequiresRightButton || _isRightButtonHeld()) &&
        generation == Volatile.Read(ref _generation) &&
        foregroundGeneration == _runtime.PublishedForegroundGeneration &&
        foregroundGeneration == _runtime.ActiveProfileGeneration &&
        ReferenceEquals(_ownerProfile, profile) &&
        ReferenceEquals(_runtime.ActiveProfile, profile) &&
        profile.IsEnabled &&
        profile.RapidFire.IsEnabled;

    private void StartPress(long generation)
    {
        var sincePressMs = (Stopwatch.GetTimestamp() - Volatile.Read(ref _pressTick)) * TickToMilliseconds;
        var holdMilliseconds = _random.Value!.Next(HOLD_MIN_MS, HOLD_MAX_MS + 1);
        _firstClickDelayMs = NextTargetDelay();
        var delay = CalculatePressRelativeDelay(holdMilliseconds, sincePressMs);
        if (Arm(generation, delay, releasePhase: true) && _logger.IsEnabled)
        {
            _logger.Log($"Rapid Fire press started: physical press released in {delay} ms, first synthetic click at +{_firstClickDelayMs} ms (interval={_intervalMs}, jitter={_jitterMs})");
        }
    }

    private void Schedule(long generation, double sendElapsedMs) =>
        Arm(generation, CalculateSuccessorDelay(NextTargetDelay(), sendElapsedMs), releasePhase: false);

    private int NextTargetDelay() => _intervalMs + (_jitterMs == 0 ? 0 : _random.Value!.Next(_jitterMs + 1));

    private bool Arm(long generation, int delay, bool releasePhase)
    {
        var profile = _ownerProfile;
        var foregroundGeneration = Volatile.Read(ref _foregroundGeneration);
        if (profile is null || !IsCurrent(generation, profile, foregroundGeneration) || _runtime.IsDisposed)
        {
            return false;
        }

        _releasePending = releasePhase;
        Volatile.Write(ref _timerGeneration, generation);
        Volatile.Write(ref _armedTick, Stopwatch.GetTimestamp());
        Volatile.Write(ref _armedDelayMs, delay);
        Interlocked.Exchange(ref _timerState, TIMER_ARMED);
        if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0)
        {
            Interlocked.Exchange(ref _timerState, TIMER_CANCELLED);
            return false;
        }

        ChangeTimer(delay, generation);
        return true;
    }

    // Returns true only when this wakeup performed the initial physical-press release.
    private bool OnTimerFired()
    {
        var requested = Volatile.Read(ref _requestedPressGeneration);
        if (requested != Volatile.Read(ref _generation) ||
            (requested == Volatile.Read(ref _timerGeneration) && Volatile.Read(ref _timerState) != TIMER_ARMED)) return false;
        // A new physical press may rearm while the previous DOWN/UP pair is still sending.
        // Only timer workers take this lock; hook cancellation and button handling never wait.
        using var callbackScope = _timerCallbackLock.EnterScope();
        requested = Volatile.Read(ref _requestedPressGeneration);
        if (requested != Volatile.Read(ref _generation)) return false;
        if (requested != Volatile.Read(ref _timerGeneration))
        {
            StartPress(requested);
            return false;
        }
        if (Volatile.Read(ref _timerState) != TIMER_ARMED) return false;
        var delay = Volatile.Read(ref _armedDelayMs);
        var elapsedMs = (Stopwatch.GetTimestamp() - Volatile.Read(ref _armedTick)) * TickToMilliseconds;
        if (elapsedMs < delay - FIRE_TOLERANCE_MS)
        {
            // A delayed hook kick can replace the initialized deadline; restore its remaining wait.
            ChangeTimer(Math.Max(1, (int)Math.Ceiling(delay - elapsedMs)), requested);
            return false;
        }
        if (Interlocked.CompareExchange(ref _timerState, TIMER_FIRED, TIMER_ARMED) != TIMER_ARMED)
        {
            return false;
        }

        var generation = Volatile.Read(ref _timerGeneration);
        var foregroundGeneration = Volatile.Read(ref _foregroundGeneration);
        var profile = _ownerProfile;
        if (profile is null || !IsCurrent(generation, profile, foregroundGeneration) || _runtime.IsDisposed)
        {
            return false;
        }

        if (_releasePending)
        {
            _releasePending = false;
            ReleasePhysicalPress(generation, profile, foregroundGeneration);
            return true;
        }

        if (_logger.IsEnabled)
        {
            _logger.Log($"Rapid Fire timer fired: elapsed={elapsedMs:F1} ms, armed delay={delay} ms");
        }

        var clickStart = Stopwatch.GetTimestamp();
        if (TrySend(generation, profile, foregroundGeneration, release: false))
        {
            Schedule(generation, (Stopwatch.GetTimestamp() - clickStart) * TickToMilliseconds);
        }

        return false;
    }

    private void ReleasePhysicalPress(long generation, Profile profile, long foregroundGeneration)
    {
        // Shot 1 is the passed-through physical DOWN. Releasing it after a normal hold gives the
        // first synthetic DOWN a real edge instead of landing on an already-held button.
        if (!TrySend(generation, profile, foregroundGeneration, release: true))
        {
            return;
        }

        var sincePressMs = (Stopwatch.GetTimestamp() - Volatile.Read(ref _pressTick)) * TickToMilliseconds;
        Arm(generation,
            Math.Max(RELEASE_GAP_MIN_MS, CalculatePressRelativeDelay(_firstClickDelayMs, sincePressMs)),
            releasePhase: false);
    }

    // Returns true only when the input was delivered; any delivery failure ends this press's burst.
    private bool TrySend(long generation, Profile profile, long foregroundGeneration, bool release)
    {
        LeftClickResult? result = null;
        try
        {
            var holdMilliseconds = release ? 0 : _random.Value!.Next(HOLD_MIN_MS, HOLD_MAX_MS + 1);
            if (!_runtime.LiveForegroundMatches(profile, foregroundGeneration) ||
                !IsCurrent(generation, profile, foregroundGeneration))
            {
                return false;
            }

            if (!_runtime.TryBeginAutomationOutput()) return false;
            try
            {
                if (IsCurrent(generation, profile, foregroundGeneration))
                {
                    result = release
                        ? _inputSender.SendMouseButton(MouseButton.Left, isDown: false)
                            ? LeftClickResult.Sent
                            : LeftClickResult.UpFailed
                        : _inputSender.SendLeftClick(holdMilliseconds);
                }
            }
            finally
            {
                _runtime.EndAutomationOutput();
            }
        }
        catch (Exception ex)
        {
            Log($"Rapid Fire {(release ? "press release" : "click")} injection error: {ex.Message}");
            SetDeliveryFaulted(true);
            return false;
        }

        if (result is { } failure && failure != LeftClickResult.Sent)
        {
            OnDeliveryFailed(generation, release, failure);
            return false;
        }

        if (result is not null) SetDeliveryFaulted(false);
        return !_runtime.IsDisposed;
    }

    // WindowsInputSender already logs which SendInput call failed; this adds one line per stopped burst.
    private void OnDeliveryFailed(long generation, bool release, LeftClickResult failure)
    {
        SetDeliveryFaulted(true);
        if (release)
        {
            // The still-held DOWN is the user's physical press; their own UP releases it.
            Log("Rapid Fire stopped: the physical press could not be released, so this press sends no clicks");
            return;
        }

        if (failure == LeftClickResult.DownFailed)
        {
            Log("Rapid Fire stopped: a click DOWN was not delivered");
            return;
        }

        // Our DOWN landed but its UP did not: the logical button may be held by us, not the user.
        Volatile.Write(ref _owedUpGeneration, generation);
        Log("Rapid Fire stopped: a click UP was not delivered");
        SettleOwedUp(generation);
    }

    // Timer thread only (under _timerCallbackLock). HandleLeftButton clears the debt on any physical
    // transition: a physical UP releases the button itself, and a new DOWN owns it until its own UP.
    private void SettleOwedUp(long generation)
    {
        // Still held: the user's UP will release it (and clear the debt). Otherwise claim the debt
        // before sending, so a physical DOWN that clears it first is never cut short by a stale UP.
        if (_physicalLeftDown ||
            Interlocked.CompareExchange(ref _owedUpGeneration, 0, generation) != generation)
        {
            return;
        }

        var released = false;
        try
        {
            released = _inputSender.SendMouseButton(MouseButton.Left, isDown: false);
        }
        catch (Exception ex)
        {
            Log($"Rapid Fire owed release injection error: {ex.Message}");
        }

        if (released)
        {
            Log("Rapid Fire released the left button after a failed click UP");
            return;
        }

        // Keep the debt unless a physical transition already settled it; the next physical click will.
        Interlocked.CompareExchange(ref _owedUpGeneration, generation, 0);
        Log("Rapid Fire could not release the left button; it may stay held until the next physical click");
    }

    private void SetDeliveryFaulted(bool faulted)
    {
        if (_deliveryFaulted == faulted) return;
        _deliveryFaulted = faulted;
        _statusChanged?.Invoke();
    }

    private void ChangeTimer(int dueTime, long generation)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _timer.Change(dueTime, Timeout.Infinite);
                // A newer request may have arrived before Change and had its kick overwritten.
                // Requests arriving after this check supply their own later wakeup.
                var requested = Volatile.Read(ref _requestedPressGeneration);
                if (requested != generation && requested == Volatile.Read(ref _generation))
                {
                    _timer.Change(0, Timeout.Infinite);
                }
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
        _timer.Dispose();
    }
}
