using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.Services.Input;

/// <summary>
/// Combined mappings, CapsLock modes, and Windows Launcher state. Hook entry points are synchronous
/// and enqueue-only; recorded releases are resolved before live settings are consulted.
/// </summary>
internal sealed class RemapStateMachine : IInputCommandGuard
{
    private const int VK_CAPITAL = 0x14;
    private const long GUARD_COMBINED = 1;
    private const long GUARD_CAPS = 2;
    private const long GUARD_LAUNCHER = 3;
    private const long GUARD_CAPS_RELEASE = 4;
    private const int KEY_PRESS_DURATION_MIN_MS = 31;
    private const int KEY_PRESS_DURATION_MAX_MS = 53;
    // Longest keyboard delay (1000 ms) plus margin for a late or coarse event timestamp.
    private const uint CAPS_MISSED_UP_GAP_MS = 1500;

    private readonly InputRuntimeState _runtime;
    private readonly IInputQueue _queue;
    private readonly ThreadLocal<Random> _random;
    private readonly ILoggerService _logger;
    private readonly Func<int, bool> _isPhysicalKeyDown;
    private readonly Action<string, string, bool> _launchProcess;

    private readonly object _combinedLock = new();
    private readonly Dictionary<Key, CombinedOverrideState> _activeCombinedOverrides = [];
    private readonly Dictionary<Key, int> _combinedTargetCounts = [];
    private readonly Dictionary<Key, bool> _combinedSuppressionUntilUp = [];
    private volatile int _activeCombinedOverrideCount;
    private volatile int _combinedSuppressionUntilUpCount;
    private long _combinedConfigurationGeneration = 1;

    private readonly object _capsLock = new();
    private Key? _capsHeldOutputKey;
    private long _capsHeldGeneration;
    private long _capsHeldForegroundGeneration;
    private Profile? _capsHeldProfile;
    private Key? _capsSecondTapKey;
    private long _capsSecondTapToken;
    private long _capsTapTokenSequence;
    // Where the current DoubleNormal pair's first tap was aimed; guarded by _capsLock.
    private ForegroundIdentitySnapshot? _capsTapPairForeground;
    private Profile? _capsTapPairProfile;
    // A foreground change kept the release tap for the physical UP (guarded by _capsLock). Remapped
    // output also records the window it must return to; Caps Lock state is global and needs none.
    private bool _capsSecondTapRetained;
    private CapsReleaseTarget? _capsRetainedTarget;
    // Worker-read target of the last enqueued window-bound release tap; replaced, never mutated.
    private volatile CapsReleaseTarget? _capsReleaseTarget;
    private bool _capsDownSuppressed;
    private bool _capsPhysicallyDown;
    // KBDLLHOOKSTRUCT.time of the last physical Caps DOWN (0 = unknown); guarded by _capsLock.
    private uint _capsLastDownTime;
    private long _capsConfigurationGeneration = 1;

    private readonly object _launcherLock = new();
    private readonly HashSet<Key> _heldLauncherKeys = [];
    private volatile Profile? _windowsProfile;
    private long _launcherConfigurationGeneration = 1;

    internal RemapStateMachine(
        InputRuntimeState runtime,
        IInputQueue queue,
        ThreadLocal<Random> random,
        ILoggerService logger,
        Func<int, bool>? isPhysicalKeyDown = null,
        Action<string, string, bool>? launchProcess = null)
    {
        _runtime = runtime;
        _queue = queue;
        _random = random;
        _logger = logger;
        _isPhysicalKeyDown = isPhysicalKeyDown ??
            (virtualKey => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0);
        _launchProcess = launchProcess ??
            ((path, arguments, runAsAdmin) => ProcessLauncher.Launch(path, arguments, runAsAdmin, logger));
    }

    internal void SetWindowsProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _windowsProfile = profile;
    }

    internal void ConfigureCombinedOverrideForTesting(
        Key source,
        Key target,
        bool suppressOriginal)
    {
        lock (_combinedLock)
        {
            var targetCount = _combinedTargetCounts.GetValueOrDefault(target);
            _activeCombinedOverrides[source] = new CombinedOverrideState(
                target,
                suppressOriginal,
                RightClickOnly: false);
            _combinedTargetCounts[target] = targetCount + 1;
            _activeCombinedOverrideCount = _activeCombinedOverrides.Count;
            if (targetCount == 0)
            {
                _queue.Enqueue(new InputCommand(
                    target,
                    IsDown: true,
                    Guard: this,
                    Generation: Volatile.Read(ref _combinedConfigurationGeneration),
                    Token: GUARD_COMBINED, HoldOwner: InputHoldOwner.Combined));
            }
        }
    }

    internal void ForceReleaseCombinedForTesting() => ReleaseAllCombinedOverrides();

    internal void ConfigureLauncherLatchForTesting(Profile windowsProfile, Key key)
    {
        SetWindowsProfile(windowsProfile);
        lock (_launcherLock)
        {
            _heldLauncherKeys.Add(key);
        }
    }

    internal bool HandleKeyboardEvent(
        int virtualKey,
        bool isKeyDown,
        bool isKeyUp,
        bool rightButtonPressed,
        uint eventTime = 0)
    {
        var handled = HandleCapsLock(virtualKey, isKeyDown, isKeyUp, eventTime) ||
                      HandleCombinedMapping(virtualKey, isKeyDown, isKeyUp, rightButtonPressed);
        return handled || HandleWindowsLauncher(virtualKey, isKeyDown, isKeyUp);
    }

    /// <summary>
    /// Runs every owned release for a key-up consumed by an earlier dispatcher feature.
    /// </summary>
    internal bool ReleaseOwnedKeyUp(int virtualKey)
    {
        var caps = HandleCapsLock(virtualKey, isKeyDown: false, isKeyUp: true);
        var combined = HandleCombinedMapping(
            virtualKey,
            isKeyDown: false,
            isKeyUp: true,
            rightButtonPressed: false);
        var launcher = HandleWindowsLauncher(virtualKey, isKeyDown: false, isKeyUp: true);
        return caps || combined || launcher;
    }

    internal void OnRightButtonReleased() => ReleaseCombinedOverrides(static state => state.RightClickOnly);

    internal void ReleaseUnsuppressed() =>
        ReleaseCombinedOverrides(static state => !state.SuppressOriginal, invalidateGeneration: true);

    internal void ReleaseForProfileChange(bool preservePhysicalPairing = true)
    {
        ReleaseAllCombinedOverrides(preservePhysicalPairing);
        ReleaseCapsState(preservePhysicalPairing);
    }

    internal void ReleaseCombinedState(bool preservePhysicalPairing) =>
        ReleaseAllCombinedOverrides(preservePhysicalPairing);

    internal void ReleaseCapsStateOnly(bool preservePhysicalPairing, bool foregroundDeparture = false) =>
        ReleaseCapsState(preservePhysicalPairing, foregroundDeparture);

    internal void ClearLauncherState()
    {
        lock (_launcherLock)
        {
            _heldLauncherKeys.Clear();
        }
    }

    internal void ReleaseAllState(bool preservePhysicalPairing = true)
    {
        ReleaseForProfileChange(preservePhysicalPairing);
        if (!preservePhysicalPairing)
        {
            lock (_launcherLock)
            {
                _heldLauncherKeys.Clear();
            }
        }
    }

    internal void ReconcileProfileSettings(Profile profile, ProfileChangeKind changeKind)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var active = ReferenceEquals(_runtime.ActiveProfile, profile);
        var windows = ReferenceEquals(_windowsProfile, profile);
        if (active && (changeKind & ProfileChangeKind.CombinedMappings) != 0)
        {
            ReleaseAllCombinedOverrides();
        }
        if (active && (changeKind & ProfileChangeKind.CapsLock) != 0)
        {
            ReleaseCapsState();
        }

        if (windows &&
            (changeKind & (ProfileChangeKind.WindowsLauncher |
                           ProfileChangeKind.Master |
                           ProfileChangeKind.Removed)) != 0)
        {
            Interlocked.Increment(ref _launcherConfigurationGeneration);
        }
        if (windows &&
            (changeKind & (ProfileChangeKind.CapsLock |
                           ProfileChangeKind.Master |
                           ProfileChangeKind.Removed)) != 0)
        {
            ReleaseCapsState();
        }
    }

    internal void SeedCapsPhysicalState(bool physicallyDown)
    {
        lock (_capsLock)
        {
            _capsPhysicallyDown = physicallyDown;
            _capsDownSuppressed = false;
        }
    }

    public bool CanExecute(in InputCommand command)
    {
        if (_runtime.IsDisposed)
        {
            return false;
        }

        return command.Token switch
        {
            GUARD_COMBINED =>
                _runtime.IsRunning &&
                command.Generation == Volatile.Read(ref _combinedConfigurationGeneration) &&
                ForegroundGenerationIsCurrent(command.ForegroundGeneration) &&
                ExpectedProfileIsCurrent(command.ExpectedProfile),
            GUARD_CAPS =>
                _runtime.IsRunning &&
                command.Generation == Volatile.Read(ref _capsConfigurationGeneration) &&
                ForegroundGenerationIsCurrent(command.ForegroundGeneration) &&
                ExpectedProfileIsCurrent(command.ExpectedProfile),
            GUARD_LAUNCHER =>
                command.Generation == Volatile.Read(ref _launcherConfigurationGeneration),
            GUARD_CAPS_RELEASE => CapsReleaseTargetIsForeground(in command),
            _ => false
        };
    }

    private bool HandleCombinedMapping(
        int virtualKey,
        bool isKeyDown,
        bool isKeyUp,
        bool rightButtonPressed)
    {
        var sourceKey = KeyInteropUtilities.FromVirtualKey(virtualKey);

        if (isKeyUp && sourceKey is not null &&
            (_activeCombinedOverrideCount > 0 || _combinedSuppressionUntilUpCount > 0))
        {
            CombinedOverrideState? held;
            var hasForcedReleaseDecision = false;
            var forcedReleaseSuppression = false;
            lock (_combinedLock)
            {
                if (_combinedSuppressionUntilUp.Remove(sourceKey.Value, out forcedReleaseSuppression))
                {
                    hasForcedReleaseDecision = true;
                }

                if (_activeCombinedOverrides.Remove(sourceKey.Value, out held) && held is not null &&
                    DecrementCombinedTarget(held.TargetKey))
                {
                    _queue.Enqueue(new InputCommand(held.TargetKey, IsDown: false, HoldOwner: InputHoldOwner.Combined));
                }
                _activeCombinedOverrideCount = _activeCombinedOverrides.Count;
                _combinedSuppressionUntilUpCount = _combinedSuppressionUntilUp.Count;
            }

            if (held is not null)
            {
                if (_logger.IsEnabled)
                {
                    _logger.Log($"Combined mapping released: {sourceKey.Value}");
                }
                return held.SuppressOriginal;
            }
            if (hasForcedReleaseDecision)
            {
                return forcedReleaseSuppression;
            }
        }

        if (isKeyDown && sourceKey is not null && _combinedSuppressionUntilUpCount > 0)
        {
            lock (_combinedLock)
            {
                if (_combinedSuppressionUntilUp.TryGetValue(sourceKey.Value, out var heldSuppression))
                {
                    return heldSuppression;
                }
            }
        }

        if (isKeyDown && sourceKey is not null && _activeCombinedOverrideCount > 0)
        {
            lock (_combinedLock)
            {
                if (_activeCombinedOverrides.TryGetValue(sourceKey.Value, out var held))
                {
                    return held.SuppressOriginal;
                }
            }
        }

        if (!isKeyDown || !_runtime.ProfileInputGenerationIsCurrent())
        {
            return false;
        }

        var generation = Volatile.Read(ref _combinedConfigurationGeneration);
        var profile = _runtime.ActiveProfile;
        if (profile is not { IsEnabled: true } || !profile.CombinedMappings.IsEnabled || sourceKey is null)
        {
            return false;
        }

        CombinedMappingEntry? entry = null;
        foreach (var candidate in profile.CombinedMappings.Mappings)
        {
            if (candidate.Source == InputTrigger.FromKey(sourceKey.Value))
            {
                entry = candidate;
                break;
            }
        }
        if (entry is null || entry.TargetKey == sourceKey.Value ||
            (entry.RightClickOnly && !rightButtonPressed))
        {
            return false;
        }

        var target = entry.TargetKey;
        var suppression = entry.SuppressOriginalKey || !_runtime.AdvancedModeEnabled;
        var state = new CombinedOverrideState(target, suppression, entry.RightClickOnly);
        var foregroundGeneration = _runtime.ActiveProfileGeneration;

        lock (_combinedLock)
        {
            if (!_runtime.IsRunning ||
                generation != Volatile.Read(ref _combinedConfigurationGeneration))
            {
                return false;
            }
            if (_activeCombinedOverrides.ContainsKey(sourceKey.Value))
            {
                return suppression;
            }

            _activeCombinedOverrides[sourceKey.Value] = state;
            _activeCombinedOverrideCount = _activeCombinedOverrides.Count;
            var targetCount = _combinedTargetCounts.GetValueOrDefault(target);
            _combinedTargetCounts[target] = targetCount + 1;
            if (targetCount == 0 && !_queue.Enqueue(
                    new InputCommand(
                        target,
                        IsDown: true,
                        Guard: this,
                        Generation: generation,
                        ForegroundGeneration: foregroundGeneration,
                        ExpectedProfile: profile,
                        Token: GUARD_COMBINED, HoldOwner: InputHoldOwner.Combined)))
            {
                _activeCombinedOverrides.Remove(sourceKey.Value);
                DecrementCombinedTarget(target);
                _activeCombinedOverrideCount = _activeCombinedOverrides.Count;
                return false;
            }
        }

        if (_logger.IsEnabled)
        {
            _logger.Log($"Combined mapping: {sourceKey.Value} → {target} (suppress={suppression})");
        }
        return suppression;
    }

    private bool HandleCapsLock(int virtualKey, bool isKeyDown, bool isKeyUp, uint eventTime = 0)
    {
        if (virtualKey != VK_CAPITAL)
        {
            return false;
        }

        if (isKeyUp)
        {
            lock (_capsLock)
            {
                return CompleteCapsRelease();
            }
        }

        if (isKeyDown && eventTime != 0)
        {
            lock (_capsLock)
            {
                // Typematic repeats arrive at most one keyboard delay apart (1 s at the slowest
                // setting). A "repeat" after a longer gap means the hook never saw the last UP, so
                // settle that press first; otherwise the pairing shifts and the next press swaps.
                if (_capsPhysicallyDown && _capsLastDownTime != 0 &&
                    unchecked(eventTime - _capsLastDownTime) > CAPS_MISSED_UP_GAP_MS)
                {
                    CompleteCapsRelease();
                    Log("Caps Lock: release was not observed; treating this press as new");
                }
                _capsLastDownTime = eventTime;
            }
        }

        var generation = Volatile.Read(ref _capsConfigurationGeneration);
        var settings = GetEffectiveCapsLockSettings();
        var foregroundGeneration = ReferenceEquals(settings, _runtime.ActiveProfile?.CapsLock)
            ? _runtime.ActiveProfileGeneration
            : 0;

        bool suppressDown;
        bool initialPress;
        lock (_capsLock)
        {
            initialPress = !_capsPhysicallyDown;
            if (initialPress)
            {
                if (generation != Volatile.Read(ref _capsConfigurationGeneration))
                {
                    return false;
                }
                _capsPhysicallyDown = true;
                _capsDownSuppressed = settings is { IsEnabled: true } &&
                    (settings.Mode != CapsLockMode.Normal || settings.IsRemapEnabled);
            }
            suppressDown = _capsDownSuppressed;
        }

        if (!initialPress)
        {
            if (suppressDown && isKeyDown)
            {
                lock (_capsLock)
                {
                    if (_capsHeldOutputKey is { } repeatedOutput)
                    {
                        _queue.Enqueue(new InputCommand(
                            repeatedOutput,
                            IsDown: true,
                            Guard: this,
                            Generation: _capsHeldGeneration,
                            ForegroundGeneration: _capsHeldForegroundGeneration,
                            ExpectedProfile: _capsHeldProfile,
                            Token: GUARD_CAPS, HoldOwner: InputHoldOwner.Caps));
                    }
                }
            }
            return suppressDown;
        }

        if (!suppressDown)
        {
            return false;
        }
        if (settings!.Mode == CapsLockMode.Disabled)
        {
            return true;
        }

        var outputKey = settings.IsRemapEnabled ? settings.RemapTarget : Key.CapsLock;
        if (outputKey is null)
        {
            return true;
        }

        lock (_capsLock)
        {
            if (!isKeyDown || !_runtime.IsRunning ||
                generation != Volatile.Read(ref _capsConfigurationGeneration))
            {
                return true;
            }

            switch (settings.Mode)
            {
                case CapsLockMode.Normal:
                    _capsHeldOutputKey = outputKey.Value;
                    _capsHeldGeneration = generation;
                    _capsHeldForegroundGeneration = foregroundGeneration;
                    _capsHeldProfile = foregroundGeneration == 0 ? null : _runtime.ActiveProfile;
                    _queue.Enqueue(new InputCommand(
                        outputKey.Value,
                        IsDown: true,
                        Guard: this,
                        Generation: generation,
                        ForegroundGeneration: foregroundGeneration,
                        ExpectedProfile: foregroundGeneration == 0 ? null : _runtime.ActiveProfile,
                        Token: GUARD_CAPS, HoldOwner: InputHoldOwner.Caps));
                    break;
                case CapsLockMode.DoubleNormal:
                    var tapPairToken = Interlocked.Increment(ref _capsTapTokenSequence);
                    _capsSecondTapKey = outputKey.Value;
                    _capsSecondTapToken = tapPairToken;
                    _capsTapPairForeground = _runtime.ForegroundIdentity;
                    _capsTapPairProfile = foregroundGeneration == 0 ? null : _runtime.ActiveProfile;
                    EnqueueCapsTap(
                        outputKey.Value,
                        tapPairToken,
                        isInitialTap: true,
                        foregroundGeneration,
                        generation);
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    // Caller holds _capsLock. Returns whether the physical UP should be suppressed.
    private bool CompleteCapsRelease()
    {
        if (_capsHeldOutputKey is { } heldOutput)
        {
            _queue.Enqueue(new InputCommand(heldOutput, IsDown: false, HoldOwner: InputHoldOwner.Caps));
            _capsHeldOutputKey = null;
            _capsHeldGeneration = 0;
            _capsHeldForegroundGeneration = 0;
            _capsHeldProfile = null;
        }
        EnqueueCapsSecondTap();

        var suppressUp = _capsDownSuppressed;
        _capsDownSuppressed = false;
        _capsPhysicallyDown = false;
        _capsLastDownTime = 0;
        return suppressUp;
    }

    private CapsLockSettings? GetEffectiveCapsLockSettings() => GetEffectiveCapsLockSettings(
        _runtime.ProfileInputGenerationIsCurrent() ? _runtime.ActiveProfile : null, _windowsProfile);

    internal static CapsLockSettings? GetEffectiveCapsLockSettings(Profile? activeProfile, Profile? windows)
    {
        var active = activeProfile is { IsEnabled: true } ? activeProfile.CapsLock : null;
        if (active is { IsEnabled: true } enabledActive &&
            (enabledActive.Mode != CapsLockMode.Normal || enabledActive.IsRemapEnabled))
        {
            return enabledActive;
        }

        var global = windows is { IsEnabled: true } ? windows.CapsLock : null;
        if (global is { IsEnabled: true } enabledGlobal &&
            (enabledGlobal.Mode != CapsLockMode.Normal || enabledGlobal.IsRemapEnabled))
        {
            return enabledGlobal;
        }

        return active?.IsEnabled == true ? active : global?.IsEnabled == true ? global : null;
    }

    private void ReleaseCapsState(bool preservePhysicalPairing = true, bool foregroundDeparture = false)
    {
        lock (_capsLock)
        {
            Interlocked.Increment(ref _capsConfigurationGeneration);
            if (_capsHeldOutputKey is { } heldOutput)
            {
                _queue.Enqueue(new InputCommand(heldOutput, IsDown: false, HoldOwner: InputHoldOwner.Caps));
                _capsHeldOutputKey = null;
                _capsHeldGeneration = 0;
                _capsHeldForegroundGeneration = 0;
                _capsHeldProfile = null;
            }
            if (foregroundDeparture && preservePhysicalPairing && _capsPhysicallyDown)
            {
                RetainCapsSecondTap();
            }
            else
            {
                EnqueueCapsSecondTap();
            }
            if (!preservePhysicalPairing)
            {
                _capsPhysicallyDown = false;
                _capsDownSuppressed = false;
                _capsLastDownTime = 0;
            }
        }
    }

    // Caller holds _capsLock. Focus left mid-press (e.g. Win opened Start): completing the pair now
    // would toggle early and, for remapped output, type into whatever took focus. Keep the release
    // tap for the physical UP; a remapped one stays bound to the window its first tap reached.
    private void RetainCapsSecondTap()
    {
        if (_capsSecondTapKey is not { } secondTap || _capsSecondTapRetained)
        {
            return;
        }

        _capsSecondTapRetained = true;
        if (secondTap != Key.CapsLock)
        {
            var foreground = _capsTapPairForeground;
            _capsRetainedTarget = new CapsReleaseTarget(
                _capsSecondTapToken,
                foreground?.WindowHandle ?? IntPtr.Zero,
                foreground?.ProcessId ?? 0,
                _capsTapPairProfile);
        }
        Log("Caps Lock: foreground changed mid-press; 2x release tap kept for the physical release");
    }

    // Caller holds _capsLock. Allocation-free: runs on the hook thread for a physical UP.
    private void EnqueueCapsSecondTap()
    {
        if (_capsSecondTapKey is not { } secondTap)
        {
            return;
        }

        var target = _capsSecondTapRetained ? _capsRetainedTarget : null;
        if (target is not null)
        {
            _capsReleaseTarget = target;
        }
        EnqueueCapsTap(secondTap, _capsSecondTapToken, isInitialTap: false, targetBound: target is not null);
        _capsSecondTapKey = null;
        _capsSecondTapToken = 0;
        _capsSecondTapRetained = false;
        _capsRetainedTarget = null;
        _capsTapPairForeground = null;
        _capsTapPairProfile = null;
    }

    // Worker only. A retained remapped release tap is dropped rather than sent to another window.
    private bool CapsReleaseTargetIsForeground(in InputCommand command)
    {
        var target = _capsReleaseTarget;
        if (target is not null && target.TapPairToken == command.TapPairToken &&
            _runtime.LiveForegroundIsWindow(target.WindowHandle, target.ProcessId, target.Profile))
        {
            return true;
        }

        if (_logger.IsEnabled)
        {
            _logger.Log($"Caps Lock: 2x release tap for {command.Key} dropped; its window is no longer in the foreground");
        }
        return false;
    }

    private void EnqueueCapsTap(
        Key key,
        long tapPairToken,
        bool isInitialTap,
        long foregroundGeneration = 0,
        long generation = 0,
        bool targetBound = false)
    {
        _queue.Enqueue(new InputCommand(
            key,
            IsDown: isInitialTap,
            DelayBeforeMs: _random.Value!.Next(
                KEY_PRESS_DURATION_MIN_MS,
                KEY_PRESS_DURATION_MAX_MS + 1),
            Kind: InputCommandKind.KeyTap,
            Guard: isInitialTap || targetBound ? this : null,
            Generation: generation,
            ForegroundGeneration: foregroundGeneration,
            ExpectedProfile: foregroundGeneration == 0 ? null : _runtime.ActiveProfile,
            Token: isInitialTap ? GUARD_CAPS : targetBound ? GUARD_CAPS_RELEASE : 0,
            TapPairToken: tapPairToken,
            RequireTapPairToken: !isInitialTap));
    }

    private bool HandleWindowsLauncher(int virtualKey, bool isKeyDown, bool isKeyUp)
    {
        var key = KeyInteropUtilities.FromVirtualKey(virtualKey);
        if (key is null)
        {
            return false;
        }

        if (isKeyUp)
        {
            lock (_launcherLock)
            {
                return _heldLauncherKeys.Remove(key.Value);
            }
        }
        if (isKeyDown)
        {
            lock (_launcherLock)
            {
                if (_heldLauncherKeys.Contains(key.Value))
                {
                    return true;
                }
            }
        }

        var generation = Volatile.Read(ref _launcherConfigurationGeneration);
        var profile = _windowsProfile;
        if (!isKeyDown || profile is not { IsEnabled: true } || !profile.WindowsLauncher.IsEnabled ||
            !profile.WindowsLauncher.Launchers.TryGetValue(key.Value, out var binding) ||
            string.IsNullOrWhiteSpace(binding.Path) ||
            (!_isPhysicalKeyDown(KeyInteropUtilities.ToVirtualKey(Key.LWin)) &&
             !_isPhysicalKeyDown(KeyInteropUtilities.ToVirtualKey(Key.RWin))))
        {
            return false;
        }

        lock (_launcherLock)
        {
            if (generation != Volatile.Read(ref _launcherConfigurationGeneration))
            {
                return false;
            }
            if (!_heldLauncherKeys.Add(key.Value))
            {
                return true;
            }
        }

        var path = binding.Path;
        var arguments = binding.Arguments;
        var runAsAdmin = binding.RunAsAdmin;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Enqueue(new InputCommand(
                Key.None,
                IsDown: false,
                Kind: InputCommandKind.DummyKey,
                Guard: this,
                Completion: completion,
                Generation: generation,
                Token: GUARD_LAUNCHER)))
        {
            completion.TrySetResult(false);
        }

        _ = completion.Task.ContinueWith(
            task =>
            {
                if (task.Status == TaskStatus.RanToCompletion && task.Result && !_runtime.IsDisposed)
                {
                    LaunchProcess(path, arguments, runAsAdmin);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.DenyChildAttach,
            TaskScheduler.Default);
        return true;
    }

    private void LaunchProcess(string path, string arguments, bool runAsAdmin)
    {
        if (_runtime.IsDisposed)
        {
            return;
        }

        try
        {
            _launchProcess(path, arguments, runAsAdmin);
            Log($"Launch successful: {path}");
        }
        catch (Exception ex)
        {
            Log($"Launch failed: {path} - {ex.Message}");
        }
    }

    private bool ForegroundGenerationIsCurrent(long generation) =>
        generation == 0 ||
        (generation == _runtime.ActiveProfileGeneration &&
         generation == _runtime.PublishedForegroundGeneration);

    private bool ExpectedProfileIsCurrent(Profile? profile) =>
        profile is null || ReferenceEquals(profile, _runtime.ActiveProfile);

    private bool DecrementCombinedTarget(Key target)
    {
        var count = _combinedTargetCounts.GetValueOrDefault(target);
        if (count == 1)
        {
            _combinedTargetCounts.Remove(target);
            return true;
        }
        if (count <= 0)
        {
            Log($"Combined target refcount underflow for {target} (count={count})");
            return false;
        }

        _combinedTargetCounts[target] = count - 1;
        return false;
    }

    private void ReleaseCombinedOverrides(
        Predicate<CombinedOverrideState> shouldRelease,
        bool invalidateGeneration = false)
    {
        List<Key>? targets = null;
        lock (_combinedLock)
        {
            List<Key>? sources = null;
            foreach (var (source, state) in _activeCombinedOverrides)
            {
                if (shouldRelease(state))
                {
                    (sources ??= []).Add(source);
                }
            }
            if (sources is null)
            {
                return;
            }
            if (invalidateGeneration)
            {
                Interlocked.Increment(ref _combinedConfigurationGeneration);
            }

            foreach (var source in sources)
            {
                if (_activeCombinedOverrides.Remove(source, out var state))
                {
                    _combinedSuppressionUntilUp[source] = state.SuppressOriginal;
                    if (DecrementCombinedTarget(state.TargetKey))
                    {
                        (targets ??= []).Add(state.TargetKey);
                    }
                }
            }
            _activeCombinedOverrideCount = _activeCombinedOverrides.Count;
            _combinedSuppressionUntilUpCount = _combinedSuppressionUntilUp.Count;
            EnqueueTargetReleases(targets);
        }
    }

    private void ReleaseAllCombinedOverrides(bool preserveSuppression = true)
    {
        lock (_combinedLock)
        {
            Interlocked.Increment(ref _combinedConfigurationGeneration);
            if (_activeCombinedOverrides.Count == 0)
            {
                if (!preserveSuppression)
                {
                    _combinedSuppressionUntilUp.Clear();
                    _combinedSuppressionUntilUpCount = 0;
                }
                return;
            }

            var targets = new List<Key>(_combinedTargetCounts.Keys);
            if (preserveSuppression)
            {
                foreach (var (source, state) in _activeCombinedOverrides)
                {
                    _combinedSuppressionUntilUp[source] = state.SuppressOriginal;
                }
            }
            else
            {
                _combinedSuppressionUntilUp.Clear();
            }
            _activeCombinedOverrides.Clear();
            _combinedTargetCounts.Clear();
            _activeCombinedOverrideCount = 0;
            _combinedSuppressionUntilUpCount = _combinedSuppressionUntilUp.Count;
            EnqueueTargetReleases(targets);
        }
    }

    private void EnqueueTargetReleases(List<Key>? targets)
    {
        if (targets is null)
        {
            return;
        }
        foreach (var target in targets)
        {
            _queue.Enqueue(new InputCommand(target, IsDown: false, HoldOwner: InputHoldOwner.Combined));
        }
    }

    private void Log(string message)
    {
        if (_logger.IsEnabled)
        {
            _logger.Log(message);
        }
    }

    private sealed record CombinedOverrideState(
        Key TargetKey,
        bool SuppressOriginal,
        bool RightClickOnly);

    private sealed record CapsReleaseTarget(
        long TapPairToken,
        IntPtr WindowHandle,
        uint ProcessId,
        Profile? Profile);
}
