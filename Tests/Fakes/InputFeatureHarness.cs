using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using sWinShortcuts.Utilities;

namespace Tests.Fakes;

internal sealed class InputFeatureHarness : IInputCommandGuard, IDisposable
{
    private readonly object _profileLock = new();
    private readonly ThreadLocal<Random> _random = new(() => new Random(1));
    private readonly InputRuntimeState _runtime;
    private readonly InputExecutor _executor;
    private readonly GestureChordStateMachine _gestures;
    private readonly RapidFireStateMachine _rapidFire;
    private readonly AutoRunStateMachine _autoRun;
    private readonly AntiAfkStateMachine _antiAfk;
    private readonly RemapStateMachine _remaps;
    private bool _rightButtonPressed;
    private Profile? _windowsProfile;
    private int _disposed;

    internal InputFeatureHarness(ILoggerService logger, IInputSender inputSender)
    {
        _runtime = new InputRuntimeState();
        _executor = new InputExecutor(_runtime, inputSender, logger);
        var transport = new NativeAutoRunTransport();
        _autoRun = new AutoRunStateMachine(_runtime, _executor, _random, logger, transport);
        _antiAfk = new AntiAfkStateMachine(_runtime, _autoRun, _random, logger, transport);
        _gestures = new GestureChordStateMachine(
            _runtime,
            _executor,
            _random,
            logger,
            () => _rightButtonPressed);
        _rapidFire = new RapidFireStateMachine(_runtime, inputSender, _random, logger, _profileLock);
        _remaps = new RemapStateMachine(
            _runtime,
            _executor,
            _random,
            logger,
            isPhysicalKeyDown: _ => false);
    }

    internal event EventHandler<Profile?>? ActiveProfileChanged;

    internal event EventHandler? RapidFireArmChanged;

    internal bool AdvancedModeEnabled
    {
        get => _runtime.AdvancedModeEnabled;
        set
        {
            if (_runtime.AdvancedModeEnabled == value)
            {
                return;
            }

            _runtime.SetAdvancedMode(value);
            if (!value)
            {
                if (_rapidFire.Release(preservePhysicalPairing: true))
                {
                    RaiseRapidFireArmChanged();
                }
                _autoRun.Release(includeBackground: true);
                _gestures.ReleaseHoldBreath();
                _remaps.ReleaseUnsuppressed();
            }
        }
    }

    internal bool PanicTriggerDerivationPendingForTesting => _gestures.PanicDerivationPending;

    internal bool RapidFireArmedForTesting => _rapidFire.GetStatus() == RapidFireArmStatus.Ready;

    internal void StartInputExecutorForTesting()
    {
        lock (_profileLock)
        {
            if (_runtime.IsRunning)
            {
                throw new InvalidOperationException("Input executor is already running.");
            }

            _executor.Start("InputExecutorTest");
            _rapidFire.Release(preservePhysicalPairing: false);
            _runtime.SetRunning(true);
        }
    }

    internal void StopInputExecutorForTesting()
    {
        lock (_profileLock)
        {
            _runtime.SetRunning(false);
            _executor.StopAndDrain(
                () => ReleaseAllState(preservePhysicalPairing: false),
                TimeSpan.FromSeconds(2));
        }
    }

    internal bool EnqueueTransitionForTesting(
        Key key,
        bool isDown,
        long foregroundGeneration = 0) =>
        _executor.Enqueue(new InputCommand(
            key,
            isDown,
            Guard: foregroundGeneration == 0 ? null : this,
            ForegroundGeneration: foregroundGeneration));

    bool IInputCommandGuard.CanExecute(in InputCommand command) =>
        _runtime.IsRunning &&
        command.ForegroundGeneration == _runtime.ActiveProfileGeneration &&
        command.ForegroundGeneration == _runtime.PublishedForegroundGeneration;

    internal bool EnqueueTapForTesting(Key key, int durationMs) =>
        _executor.Enqueue(new InputCommand(
            key,
            IsDown: true,
            DelayBeforeMs: durationMs,
            Kind: InputCommandKind.KeyTap));

    internal Task<bool> EnqueueDummyForTesting()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_executor.Enqueue(new InputCommand(
                Key.None,
                IsDown: false,
                Kind: InputCommandKind.DummyKey,
                Completion: completion)))
        {
            completion.TrySetResult(false);
        }
        return completion.Task;
    }

    internal void SetForegroundGenerationsForTesting(long active, long published)
    {
        _runtime.SetActiveProfile(_runtime.ActiveProfile, active);
        _runtime.SetForegroundIdentity(
            IntPtr.Zero,
            0,
            _runtime.ActiveProfile?.NormalizedExecutable,
            published);
    }

    internal void ConfigureActiveProfileForTesting(
        Profile profile,
        long foregroundGeneration,
        bool altPressed)
    {
        _runtime.SetActiveProfile(profile, foregroundGeneration);
        _runtime.SetForegroundIdentity(
            IntPtr.Zero,
            0,
            profile.NormalizedExecutable,
            foregroundGeneration);
        _gestures.SeedAltPressed(altPressed);
    }

    internal bool HandleAltMouseForTesting(sWinShortcuts.Models.MouseButton button, bool isDown)
    {
        var message = (button, isDown) switch
        {
            (sWinShortcuts.Models.MouseButton.Left, true) => NativeMethods.WM_LBUTTONDOWN,
            (sWinShortcuts.Models.MouseButton.Left, false) => NativeMethods.WM_LBUTTONUP,
            (sWinShortcuts.Models.MouseButton.Right, true) => NativeMethods.WM_RBUTTONDOWN,
            (sWinShortcuts.Models.MouseButton.Right, false) => NativeMethods.WM_RBUTTONUP,
            (sWinShortcuts.Models.MouseButton.Middle, true) => NativeMethods.WM_MBUTTONDOWN,
            (sWinShortcuts.Models.MouseButton.Middle, false) => NativeMethods.WM_MBUTTONUP,
            _ => throw new ArgumentOutOfRangeException(nameof(button))
        };
        return _gestures.HandleAltMouse(message, 0);
    }

    internal bool HandleAltKeyboardForTesting(Key key, bool isDown) =>
        _gestures.HandleAltKeyboard(
            KeyInteropUtilities.ToVirtualKey(key),
            isKeyDown: isDown,
            isKeyUp: !isDown);

    internal void HandleAltKeyboardPanicOverrideForTesting(Key key, bool isDown) =>
        _gestures.HandleAltKeyboardPanicOverride(
            KeyInteropUtilities.ToVirtualKey(key),
            isKeyDown: isDown,
            isKeyUp: !isDown);

    internal void RederiveAltKeyboardPhysicalStateForTesting(Func<int, bool> isPhysicallyDown) =>
        _gestures.RederiveAltKeyboardPhysicalState(isPhysicallyDown);

    internal void RederivePanicTriggerPhysicalStateForTesting(Func<int, bool> isPhysicallyDown) =>
        _gestures.RederivePanicTriggerPhysicalState(isPhysicallyDown);

    internal long PanicDerivationBeginForTesting() => _gestures.BeginPanicDerivation();

    internal void PanicDerivationRetireForTesting(long ticket) => _gestures.RetirePanicDerivation(ticket);

    internal bool HandleCapsLockForTesting(bool isDown) =>
        _remaps.HandleKeyboardEvent(
            NativeMethods.VK_CAPITAL,
            isKeyDown: isDown,
            isKeyUp: !isDown,
            rightButtonPressed: false);

    internal void ForceReleaseCapsLockForTesting(bool preservePhysicalPairing = true) =>
        _remaps.ReleaseCapsStateOnly(preservePhysicalPairing);

    internal void ConfigureForegroundAutoRunForTesting(
        Profile owner,
        bool sprintInjected,
        Key sprintKey) =>
        _autoRun.ConfigureForegroundForTesting(owner, sprintInjected, sprintKey);

    internal void ConfigureForegroundAutoRunHandoffForTesting(
        Profile owner,
        bool sprintEnabled = false,
        SprintActivation sprintMode = SprintActivation.Hold,
        Key sprintKey = Key.LeftShift) =>
        _autoRun.ConfigureForegroundHandoffForTesting(owner, sprintEnabled, sprintMode, sprintKey);

    internal bool HandleAutoRunForTesting(Key key, bool isKeyDown, bool isKeyUp)
    {
        var virtualKey = KeyInteropUtilities.ToVirtualKey(key);
        var physicalEvent = _autoRun.ObservePhysicalEvent(virtualKey, isKeyDown, isKeyUp);
        if (physicalEvent.SuppressPhysicalWHandoffUp)
        {
            _remaps.ReleaseOwnedKeyUp(virtualKey);
        }
        return _autoRun.Handle(virtualKey, isKeyDown, isKeyUp, physicalEvent);
    }

    internal void ConfigureCombinedOverrideForTesting(Key source, Key target, bool suppressOriginal) =>
        _remaps.ConfigureCombinedOverrideForTesting(source, target, suppressOriginal);

    internal void ForceReleaseCombinedForTesting() => _remaps.ForceReleaseCombinedForTesting();

    internal void ForceReleaseUnsuppressedCombinedForTesting() => _remaps.ReleaseUnsuppressed();

    internal bool HandleCombinedForTesting(Key source, bool isDown) =>
        _remaps.HandleKeyboardEvent(
            KeyInteropUtilities.ToVirtualKey(source),
            isKeyDown: isDown,
            isKeyUp: !isDown,
            rightButtonPressed: _rightButtonPressed);

    internal void ConfigureLauncherLatchForTesting(Profile windowsProfile, Key key)
    {
        _windowsProfile = windowsProfile;
        _remaps.ConfigureLauncherLatchForTesting(windowsProfile, key);
    }

    internal bool HandleLauncherForTesting(Key key, bool isDown) =>
        _remaps.HandleKeyboardEvent(
            KeyInteropUtilities.ToVirtualKey(key),
            isKeyDown: isDown,
            isKeyUp: !isDown,
            rightButtonPressed: _rightButtonPressed);

    internal void ConfigureHoldBreathForTesting(Profile profile, long foregroundGeneration)
    {
        ConfigureActiveProfileForTesting(profile, foregroundGeneration, altPressed: false);
        _runtime.SetAdvancedMode(true);
    }

    internal void HandleHoldBreathRightButtonForTesting(bool isDown)
    {
        _rightButtonPressed = isDown;
        if (isDown)
        {
            _gestures.HandleRightButtonDown(_rightButtonPressed);
        }
        else
        {
            _gestures.HandleRightButtonUp();
        }
    }

    internal bool HandleHoldBreathPanicKeyForTesting(Key key, bool isDown) =>
        _gestures.HandlePanicKey(
            KeyInteropUtilities.ToVirtualKey(key),
            isKeyDown: isDown,
            isKeyUp: !isDown,
            rightButtonPressed: _rightButtonPressed);

    internal bool HandleHoldBreathPanicMouseForTesting(
        sWinShortcuts.Models.MouseButton button,
        bool isDown)
    {
        var (message, mouseData) = (button, isDown) switch
        {
            (sWinShortcuts.Models.MouseButton.Left, true) => (NativeMethods.WM_LBUTTONDOWN, 0u),
            (sWinShortcuts.Models.MouseButton.Left, false) => (NativeMethods.WM_LBUTTONUP, 0u),
            (sWinShortcuts.Models.MouseButton.Right, true) => (NativeMethods.WM_RBUTTONDOWN, 0u),
            (sWinShortcuts.Models.MouseButton.Right, false) => (NativeMethods.WM_RBUTTONUP, 0u),
            (sWinShortcuts.Models.MouseButton.Middle, true) => (NativeMethods.WM_MBUTTONDOWN, 0u),
            (sWinShortcuts.Models.MouseButton.Middle, false) => (NativeMethods.WM_MBUTTONUP, 0u),
            (sWinShortcuts.Models.MouseButton.XButton1, true) => (NativeMethods.WM_XBUTTONDOWN, 1u << 16),
            (sWinShortcuts.Models.MouseButton.XButton1, false) => (NativeMethods.WM_XBUTTONUP, 1u << 16),
            (sWinShortcuts.Models.MouseButton.XButton2, true) => (NativeMethods.WM_XBUTTONDOWN, 2u << 16),
            (sWinShortcuts.Models.MouseButton.XButton2, false) => (NativeMethods.WM_XBUTTONUP, 2u << 16),
            _ => throw new ArgumentOutOfRangeException(nameof(button))
        };
        return _gestures.HandlePanicMouse(message, mouseData, _rightButtonPressed);
    }

    internal void FireHoldBreathTimerForTesting() => _gestures.FireHoldBreathTimerForTesting();

    internal void ConfigureRapidFireForTesting(
        Profile profile,
        long foregroundGeneration,
        bool armed = true)
    {
        ConfigureActiveProfileForTesting(profile, foregroundGeneration, altPressed: false);
        _runtime.SetAdvancedMode(true);
        _rapidFire.ConfigureForTesting(profile, foregroundGeneration, armed);
    }

    internal void HandleRapidFireLeftButtonForTesting(bool isDown, bool consumed = false) =>
        _rapidFire.HandleLeftButton(isDown, allowStart: !consumed);

    internal void FireRapidFireTimerForTesting() => _rapidFire.FireTimerForTesting();

    internal void HandleRapidFireToggleForTesting(Key key, bool isDown)
    {
        if (_rapidFire.HandleToggleKey(
                KeyInteropUtilities.ToVirtualKey(key),
                isKeyDown: isDown,
                isKeyUp: !isDown))
        {
            RaiseRapidFireArmChanged();
        }
    }

    internal RapidFireArmStatus GetRapidFireArmStatus() => _rapidFire.GetStatus();

    internal void ReleaseForegroundAutoRun() => _autoRun.Release(includeBackground: false);

    internal void ReleaseForegroundState() => ReleaseAllState(preserveRapidFireArm: true);

    internal void SetRapidFireToggleKey(Key? key)
    {
        if (_rapidFire.SetToggleKey(key))
        {
            RaiseRapidFireArmChanged();
        }
    }

    internal void SetForegroundIdentity(
        IntPtr windowHandle,
        uint processId,
        string? normalizedExecutable,
        long foregroundGeneration)
    {
        var changed = _runtime.PublishedForegroundGeneration != foregroundGeneration;
        _runtime.SetForegroundIdentity(
            windowHandle,
            processId,
            normalizedExecutable,
            foregroundGeneration);
        if (changed)
        {
            RaiseRapidFireArmChanged();
        }
    }

    internal void ActivateProfile(Profile profile, long foregroundGeneration)
    {
        ArgumentNullException.ThrowIfNull(profile);
        bool changed;
        bool generationChanged;
        lock (_profileLock)
        {
            if (!_runtime.IsRunning)
            {
                return;
            }

            changed = !ReferenceEquals(_runtime.ActiveProfile, profile);
            generationChanged = !changed && _runtime.ActiveProfileGeneration != foregroundGeneration;
            if (changed)
            {
                ReleaseAllState(preserveRapidFireArm: true);
            }
            _runtime.SetActiveProfile(profile, foregroundGeneration);
        }

        if (changed)
        {
            ActiveProfileChanged?.Invoke(this, profile);
        }
        else if (generationChanged)
        {
            RaiseRapidFireArmChanged();
        }
    }

    internal void DeactivateProfile(long foregroundGeneration)
    {
        Profile? previous;
        lock (_profileLock)
        {
            previous = _runtime.ActiveProfile;
            if (previous is not null)
            {
                ReleaseAllState(preserveRapidFireArm: true);
            }
            _runtime.SetActiveProfile(null, foregroundGeneration);
        }

        if (previous is not null)
        {
            ActiveProfileChanged?.Invoke(this, null);
        }
    }

    internal void ReconcileProfileSettings(Profile profile, ProfileChangeKind changeKind)
    {
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
            lock (_profileLock)
            {
                if (ReferenceEquals(_runtime.ActiveProfile, profile))
                {
                    ReleaseAllState(preserveRapidFireArm: true);
                    _runtime.SetActiveProfile(null, long.MinValue);
                }
            }
            _autoRun.ReleaseOwnedBy(profile);
            _antiAfk.ReleaseOwnedBy(profile);
            if (_rapidFire.ReleaseOwnedBy(profile))
            {
                RaiseRapidFireArmChanged();
            }
            ActiveProfileChanged?.Invoke(this, null);
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
        if (active && (changeKind & ProfileChangeKind.AltMouse) != 0)
        {
            _gestures.ReleaseAltMouse(preserveSuppressedUps: true);
        }
        if (active && (changeKind & ProfileChangeKind.AltKeyboard) != 0)
        {
            _gestures.ReleaseAltKeyboard(preserveSuppressedUps: true);
        }
        if (active && (changeKind & ProfileChangeKind.HoldBreath) != 0)
        {
            _gestures.ReleaseHoldBreath();
            _gestures.RederivePanicTriggerPhysicalState(_ => false);
        }

        if ((changeKind & (ProfileChangeKind.RapidFire |
                           ProfileChangeKind.Removed |
                           ProfileChangeKind.Identity)) != 0 ||
            ((changeKind & ProfileChangeKind.Master) != 0 && !profile.IsEnabled))
        {
            if (_rapidFire.ReleaseOwnedBy(profile))
            {
                RaiseRapidFireArmChanged();
            }
        }

        // Mirrors InputHookService: AntiAfk-kind edits (Mode/interval) must NOT release the retained
        // background target — those settings are read live by the tick.
        if ((changeKind & (ProfileChangeKind.Removed | ProfileChangeKind.Identity)) != 0 ||
            ((changeKind & ProfileChangeKind.Master) != 0 && !profile.IsEnabled))
        {
            _antiAfk.ReleaseOwnedBy(profile);
        }
    }

    internal void Stop()
    {
        bool armChanged;
        lock (_profileLock)
        {
            if (!_runtime.IsRunning)
            {
                return;
            }

            _runtime.SetRunning(false);
            armChanged = ReleaseAllState(preservePhysicalPairing: false);
            _autoRun.Release(includeBackground: true);
            _autoRun.JoinBackgroundInputThread();
            _executor.StopAndDrain();
        }

        if (armChanged)
        {
            RaiseRapidFireArmChanged();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _runtime.TryBeginDispose();
        Stop();
        _antiAfk.Dispose();
        _gestures.Dispose();
        _rapidFire.Dispose();
        _executor.Dispose();
    }

    private bool ReleaseAllState(
        bool preservePhysicalPairing = true,
        bool preserveRapidFireArm = false)
    {
        var armChanged = preserveRapidFireArm
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
        return armChanged;
    }

    private bool CancelRapidFirePressAndKeepArm()
    {
        _rapidFire.CancelPress();
        return false;
    }

    private void RaiseRapidFireArmChanged() => RapidFireArmChanged?.Invoke(this, EventArgs.Empty);
}
