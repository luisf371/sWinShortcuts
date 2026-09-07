using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.Services.Input;

internal interface IAutoRunTransport
{
    IntPtr GetForegroundWindow();
    IntPtr GetChildWindow(IntPtr window);
    uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    uint GetCurrentThreadId();
    short GetAsyncKeyState(int virtualKey);
    uint MapVirtualKey(uint code, uint mapType);
    bool IsHungAppWindow(IntPtr window);
    bool GetKeyboardState(byte[] state);
    bool SetKeyboardState(byte[] state);
    bool AttachThreadInput(uint sourceThread, uint targetThread, bool attach);
    bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    int GetLastWin32Error();
}

internal sealed class NativeAutoRunTransport : IAutoRunTransport
{
    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();
    public IntPtr GetChildWindow(IntPtr window) => NativeMethods.GetWindow(window, NativeMethods.GW_CHILD);
    public uint GetWindowThreadProcessId(IntPtr window, out uint processId) =>
        NativeMethods.GetWindowThreadProcessId(window, out processId);
    public uint GetCurrentThreadId() => NativeMethods.GetCurrentThreadId();
    public short GetAsyncKeyState(int virtualKey) => NativeMethods.GetAsyncKeyState(virtualKey);
    public uint MapVirtualKey(uint code, uint mapType) => NativeMethods.MapVirtualKey(code, mapType);
    public bool IsHungAppWindow(IntPtr window) => NativeMethods.IsHungAppWindow(window);
    public bool GetKeyboardState(byte[] state) => NativeMethods.GetKeyboardState(state);
    public bool SetKeyboardState(byte[] state) => NativeMethods.SetKeyboardState(state);
    public bool AttachThreadInput(uint sourceThread, uint targetThread, bool attach) =>
        NativeMethods.AttachThreadInput(sourceThread, targetThread, attach);
    public bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam) =>
        NativeMethods.PostMessage(window, message, wParam, lParam);
    public int GetLastWin32Error() => Marshal.GetLastWin32Error();
}

/// <summary>
/// Auto-Run's hook entry points only update latches, take the human-frequency leaf lock, and enqueue.
/// Native background delivery and waits run on the owned background thread. The command guard is
/// deliberately lock-free because the input executor calls it on its worker thread.
/// </summary>
internal sealed class AutoRunStateMachine : IInputCommandGuard
{
    private const int VK_W = 0x57;
    private const int VK_S = 0x53;
    private const int AUTO_RUN_REPEAT_MS = 35;
    private const int BG_SPRINT_PREDELAY_MIN_MS = 40;
    private const int BG_SPRINT_PREDELAY_MAX_MS = 60;
    private const int BG_SPRINT_TAP_MIN_MS = 40;
    private const int BG_SPRINT_TAP_MAX_MS = 60;
    private const int BG_SPRINT_REENGAGE_QUIET_MS = 50;
    private const int TAP_DURATION_MIN_MS = 20;
    private const int TAP_DURATION_MAX_MS = 30;
    private const int RNG_WARMUP_MIN_CALLS = 1;
    private const int RNG_WARMUP_MAX_CALLS = 5;

    private readonly InputRuntimeState _runtime;
    private readonly IInputQueue _queue;
    private readonly ThreadLocal<Random> _random;
    private readonly ILoggerService _logger;
    private readonly IAutoRunTransport _transport;
    private readonly object _autoRunLock = new();

    private volatile bool _active;
    private volatile Profile? _ownerProfile;
    private bool _activationPending;
    private long _injectionGeneration;
    private long _configurationGeneration = 1;
    private long _activeInjectionGeneration;
    private volatile ForegroundIdentitySnapshot? _foregroundGuard;
    private volatile bool _bypassForegroundGuardForTesting;
    private bool _moveInjected;
    private bool _sprintInjected;
    private Key _sprintInjectedKey;
    private Key _sprintKey;
    private bool _sprintToggleable;
    private bool _sprintPending;
    private volatile bool _sprintIntendedHeld;

    private volatile bool _isBackground;
    private BackgroundRun _backgroundTarget;
    private uint _targetPid;
    private Thread? _backgroundThread;
    private volatile bool _backgroundRun;
    private bool _backgroundReleaseW;
    private bool _backgroundReleaseSprint;
    private Key _backgroundReleaseSprintKey;

    private int _consumedTriggerVk;
    private int _triggerKeyDownVk;
    private int _snapshotTriggerVk;
    private ModifierKeys _snapshotModifier;
    private bool _wPhysicallyDown;
    private bool _sPhysicallyDown;
    private bool _sprintPhysicallyDown;
    private volatile bool _physicalWHandoff;
    private bool _suppressedPhysicalWUp;
    private bool _stopOnPhysicalWUp;
    private bool _antiAfkTapInFlight;

    internal readonly record struct PhysicalEvent(
        bool FreshW,
        bool FreshS,
        bool SuppressPhysicalWHandoffUp);

    private readonly record struct BackgroundRun(Profile Owner, long Generation, IntPtr Window, uint ProcessId);

    internal AutoRunStateMachine(
        InputRuntimeState runtime,
        IInputQueue queue,
        ThreadLocal<Random> random,
        ILoggerService logger,
        IAutoRunTransport? transport = null)
    {
        _runtime = runtime;
        _queue = queue;
        _random = random;
        _logger = logger;
        _transport = transport ?? new NativeAutoRunTransport();
    }

    internal bool IsActive => _active;
    internal bool IsBackground => _active && _isBackground;
    internal long ConfigurationGeneration => Volatile.Read(ref _configurationGeneration);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal PhysicalEvent ObservePhysicalEvent(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        bool freshW = false;
        bool freshS = false;
        if (vkCode == VK_W)
        {
            freshW = ApplyPhysicalKeyEvent(ref _wPhysicallyDown, isKeyDown, isKeyUp);
        }
        else if (vkCode == VK_S)
        {
            freshS = ApplyPhysicalKeyEvent(ref _sPhysicallyDown, isKeyDown, isKeyUp);
        }

        if (vkCode == VK_W && isKeyUp)
        {
            CompleteStopOnPhysicalWUp();
        }

        var active = _active;
        var handoffEvent = vkCode == VK_W && _physicalWHandoff;
        var suppressUp = active && handoffEvent && isKeyUp;
        if (vkCode == VK_W && isKeyUp && handoffEvent)
        {
            _physicalWHandoff = false;
            if (active)
            {
                BeginAfterPhysicalWRelease();
            }

            if (_consumedTriggerVk == vkCode) _consumedTriggerVk = 0;
            if (_triggerKeyDownVk == vkCode) _triggerKeyDownVk = 0;
        }

        return new PhysicalEvent(freshW, freshS, suppressUp);
    }

    internal bool Handle(int vkCode, bool isKeyDown, bool isKeyUp, PhysicalEvent physicalEvent)
    {
        var active = _active;
        var sprintVk = active ? KeyInteropUtilities.ToVirtualKey(_sprintKey) : 0;
        bool freshSprint = false;
        if (active && sprintVk != 0 && vkCode == sprintVk && vkCode != VK_W && vkCode != VK_S)
        {
            freshSprint = ApplyPhysicalKeyEvent(ref _sprintPhysicallyDown, isKeyDown, isKeyUp);
        }

        if (active && isKeyDown && (physicalEvent.FreshW || physicalEvent.FreshS)
            && !(vkCode == _snapshotTriggerVk && IsTriggerModifierDown(_snapshotModifier)))
        {
            if (!_isBackground || ForegroundIsTargetProcess())
            {
                if (physicalEvent.FreshW)
                {
                    lock (_autoRunLock)
                    {
                        if (_active) _stopOnPhysicalWUp = true;
                    }
                }
                else
                {
                    Release(includeBackground: true);
                }
            }
            return false;
        }

        if (active && _sprintToggleable && sprintVk != 0 && vkCode == sprintVk
            && vkCode != VK_W && vkCode != VK_S && vkCode != _snapshotTriggerVk)
        {
            if (!_isBackground || ForegroundIsTargetProcess())
            {
                if (isKeyDown && freshSprint) ToggleSprintHold();
                return true;
            }
        }

        if (_consumedTriggerVk != 0 && vkCode == _consumedTriggerVk)
        {
            if (isKeyUp)
            {
                _consumedTriggerVk = 0;
                if (_triggerKeyDownVk == vkCode) _triggerKeyDownVk = 0;
                return true;
            }
            return isKeyDown;
        }

        if (isKeyUp && _triggerKeyDownVk == vkCode)
        {
            _triggerKeyDownVk = 0;
            return physicalEvent.SuppressPhysicalWHandoffUp;
        }
        if (physicalEvent.SuppressPhysicalWHandoffUp) return true;

        if (_active)
        {
            if (_snapshotTriggerVk == 0 || vkCode != _snapshotTriggerVk || !isKeyDown) return false;
            if (_triggerKeyDownVk == vkCode) return false;
            _triggerKeyDownVk = vkCode;
            if (!IsTriggerModifierDown(_snapshotModifier))
            {
                if (_sprintToggleable && sprintVk == vkCode && vkCode != VK_W && vkCode != VK_S)
                {
                    _consumedTriggerVk = vkCode;
                    return true;
                }
                return false;
            }

            Release(includeBackground: true);
            _consumedTriggerVk = vkCode;
            return true;
        }

        var configurationGeneration = Volatile.Read(ref _configurationGeneration);
        var profile = _runtime.ActiveProfile;
        if (!_runtime.AdvancedModeEnabled || !_runtime.ProfileInputGenerationIsCurrent()
            || profile is not { IsEnabled: true } || !profile.AutoRun.IsEnabled)
        {
            return false;
        }

        var settings = profile.AutoRun;
        var triggerVk = KeyInteropUtilities.ToVirtualKey(settings.TriggerKey);
        if (triggerVk == 0 || vkCode != triggerVk || !isKeyDown) return false;
        if (_triggerKeyDownVk == vkCode) return false;
        _triggerKeyDownVk = vkCode;
        if (!IsTriggerModifierDown(settings.TriggerModifier)) return false;
        if (!Activate(settings, profile, configurationGeneration)) return false;

        _consumedTriggerVk = vkCode;
        return true;
    }

    internal void SeedMovementPhysicalState()
    {
        _wPhysicallyDown = (_transport.GetAsyncKeyState(VK_W) & 0x8000) != 0;
        _sPhysicallyDown = (_transport.GetAsyncKeyState(VK_S) & 0x8000) != 0;
    }

    internal void ConfigurationChanged(Profile profile)
    {
        Interlocked.Increment(ref _configurationGeneration);
        ReleaseOwnedBy(profile);
    }

    internal void ReleaseOwnedBy(Profile profile)
    {
        lock (_autoRunLock)
        {
            if (ReferenceEquals(_ownerProfile, profile)) ReleaseLocked(includeBackground: true);
        }
    }

    internal void Release(bool includeBackground)
    {
        lock (_autoRunLock)
        {
            ReleaseLocked(includeBackground);
        }
    }

    internal void ClearTriggerLatches()
    {
        _consumedTriggerVk = 0;
        _triggerKeyDownVk = 0;
    }

    internal void ConfigureForegroundForTesting(
        Profile owner,
        bool sprintInjected,
        Key sprintKey,
        bool bypassForegroundGuard = true)
    {
        lock (_autoRunLock)
        {
            _ownerProfile = owner;
            _moveInjected = true;
            _sprintInjected = sprintInjected;
            _sprintIntendedHeld = sprintInjected;
            _sprintInjectedKey = sprintKey;
            _sprintKey = sprintKey;
            _isBackground = false;
            _bypassForegroundGuardForTesting = bypassForegroundGuard;
            _active = true;
        }
    }

    internal void ConfigureForegroundHandoffForTesting(
        Profile owner,
        bool sprintEnabled = false,
        SprintActivation sprintMode = SprintActivation.Hold,
        Key sprintKey = Key.LeftShift)
    {
        lock (_autoRunLock)
        {
            _wPhysicallyDown = true;
            _physicalWHandoff = true;
            _moveInjected = false;
            _sprintPending = sprintEnabled;
            _sprintToggleable = sprintEnabled && sprintMode == SprintActivation.Hold;
            _sprintIntendedHeld = _sprintToggleable;
            _sprintInjected = false;
            _sprintKey = sprintKey;
            _sprintInjectedKey = Key.None;
            _isBackground = false;
            _foregroundGuard = null;
            _bypassForegroundGuardForTesting = true;
            _activeInjectionGeneration = Interlocked.Increment(ref _injectionGeneration);
            _ownerProfile = owner;
            _stopOnPhysicalWUp = false;
            _active = true;
        }
    }

    internal bool TryEnqueueWhileInactive(in InputCommand command)
    {
        lock (_autoRunLock)
        {
            if (_active || _activationPending || _backgroundThread is not null || _runtime.IsDisposed || !_runtime.IsRunning
                || command.ExpectedProfile is not { IsEnabled: true } profile
                || !_runtime.ProfileInputGenerationIsCurrent(profile, command.ForegroundGeneration)
                || !profile.AntiAfk.IsEnabled)
            {
                return false;
            }
            return _queue.Enqueue(command);
        }
    }

    // Anti-AFK's posted (Background/Forced) ripple arbitrates against Auto-Run under the same lock
    // that publishes _active — the authoritative check + commit at the point of conflict, mirroring
    // TryEnqueueWhileInactive above. The latch is bounded by one tap step (~150 ms incl. sleeps).
    internal bool TryBeginAntiAfkTap()
    {
        lock (_autoRunLock)
        {
            if (_active || _activationPending || _backgroundThread is not null || _runtime.IsDisposed || !_runtime.IsRunning)
            {
                return false;
            }
            _antiAfkTapInFlight = true;
            return true;
        }
    }

    internal void EndAntiAfkTap()
    {
        lock (_autoRunLock)
        {
            _antiAfkTapInFlight = false;
        }
    }

    internal void JoinBackgroundInputThread()
    {
        Thread? thread;
        lock (_autoRunLock)
        {
            if (_isBackground || _activationPending) ReleaseLocked(includeBackground: true);
            _backgroundRun = false;
            thread = _backgroundThread;
        }
        if (thread is not null && thread != Thread.CurrentThread && thread.IsAlive) thread.Join(300);
    }

    internal void SetBackgroundThreadForTesting(Thread? thread)
    {
        lock (_autoRunLock)
        {
            _backgroundThread = thread;
        }
    }

    public bool CanExecute(in InputCommand command)
    {
        if (!command.IsDown || command.Generation == 0) return true;
        if (_runtime.IsDisposed || !_runtime.IsRunning
            || command.Generation != Volatile.Read(ref _injectionGeneration)
            || command.ForegroundGeneration != _runtime.ActiveProfileGeneration
            || command.ForegroundGeneration != _runtime.PublishedForegroundGeneration)
        {
            return false;
        }

        if (_bypassForegroundGuardForTesting) return true;

        var expected = _foregroundGuard;
        if (expected is null || expected.Generation != command.ForegroundGeneration
            || !string.Equals(expected.Executable, command.ExpectedExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var foreground = _transport.GetForegroundWindow();
        _transport.GetWindowThreadProcessId(foreground, out var processId);
        return foreground != IntPtr.Zero && foreground == expected.WindowHandle
            && processId != 0 && processId == expected.ProcessId;
    }

    internal static bool ApplyPhysicalKeyEvent(ref bool physicallyDown, bool isKeyDown, bool isKeyUp)
    {
        if (isKeyDown)
        {
            var fresh = !physicallyDown;
            physicallyDown = true;
            return fresh;
        }
        if (isKeyUp) physicallyDown = false;
        return false;
    }

    internal bool IsTriggerModifierDown(ModifierKeys modifier)
    {
        return modifier switch
        {
            ModifierKeys.None => true,
            ModifierKeys.Control => IsDown(0x11),
            ModifierKeys.Alt => IsDown(0x12),
            ModifierKeys.Shift => IsDown(0x10),
            ModifierKeys.Windows => IsDown(0x5B) || IsDown(0x5C),
            _ => false
        };
    }

    private bool IsDown(int virtualKey) => (_transport.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private void CompleteStopOnPhysicalWUp()
    {
        lock (_autoRunLock)
        {
            if (!_stopOnPhysicalWUp) return;
            _stopOnPhysicalWUp = false;
            if (_active) ReleaseLocked(includeBackground: true);
        }
    }

    private bool Activate(AutoRunSettings settings, Profile profile, long configurationGeneration)
    {
        long generation;
        ForegroundIdentitySnapshot? snapshot;
        bool background;
        Key sprintKey;
        bool sprintEnabled;
        bool sprintToggleable;
        int triggerVk;
        ModifierKeys triggerModifier;
        lock (_autoRunLock)
        {
            if (!CanActivate(profile, configurationGeneration) || _active || _activationPending
                || _backgroundThread is not null || _antiAfkTapInFlight) return false;
            generation = Interlocked.Increment(ref _injectionGeneration);
            _activationPending = true;
            _ownerProfile = profile;
            snapshot = _runtime.ForegroundIdentity;
            background = settings.SendMode == AutoRunSendMode.Background;
            sprintKey = settings.SprintKey;
            sprintEnabled = settings.SprintEnabled;
            sprintToggleable = sprintEnabled && settings.SprintMode == SprintActivation.Hold;
            triggerVk = KeyInteropUtilities.ToVirtualKey(settings.TriggerKey);
            triggerModifier = settings.TriggerModifier;
        }
        try
        {
            var executable = profile.NormalizedExecutable;
            var foreground = _transport.GetForegroundWindow();
            _transport.GetWindowThreadProcessId(foreground, out var foregroundPid);
            if (snapshot is null || foreground == IntPtr.Zero || foreground != snapshot.WindowHandle
                || foregroundPid == 0 || foregroundPid != snapshot.ProcessId
                || string.IsNullOrEmpty(executable)
                || !string.Equals(snapshot.Executable, executable, StringComparison.OrdinalIgnoreCase))
            {
                Log("AutoRun: foreground not confirmed as the profile game; activation aborted");
                return false;
            }
            var sprintVk = KeyInteropUtilities.ToVirtualKey(sprintKey);
            var sprintPhysicallyDown = sprintVk != 0 && IsDown(sprintVk);
            lock (_autoRunLock)
            {
                if (!_activationPending || generation != Volatile.Read(ref _injectionGeneration)
                    || !ReferenceEquals(_ownerProfile, profile) || !CanActivate(profile, configurationGeneration)
                    || !ReferenceEquals(snapshot, _runtime.ForegroundIdentity)
                    || snapshot.Generation != _runtime.ActiveProfileGeneration
                    || snapshot.Generation != _runtime.PublishedForegroundGeneration) return false;
                if (background)
                {
                    _backgroundTarget = new BackgroundRun(profile, generation, snapshot.WindowHandle, snapshot.ProcessId);
                    _targetPid = snapshot.ProcessId;
                }
                else
                {
                    _foregroundGuard = snapshot;
                    _bypassForegroundGuardForTesting = false;
                }
                _isBackground = background;
                _sprintKey = sprintKey;
                _sprintToggleable = sprintToggleable;
                _sprintIntendedHeld = sprintToggleable;
                _sprintInjected = false;
                _snapshotTriggerVk = triggerVk;
                _snapshotModifier = triggerModifier;
                _physicalWHandoff = _wPhysicallyDown;
                _suppressedPhysicalWUp = false;
                _stopOnPhysicalWUp = false;
                _sprintPhysicallyDown = sprintPhysicallyDown;
                _activeInjectionGeneration = generation;
                _moveInjected = false;
                if (!_physicalWHandoff && !background && !EnqueueForegroundDown(Key.W, generation, snapshot))
                {
                    ResetAbortedActivation();
                    return false;
                }
                if (!_physicalWHandoff && !background) _moveInjected = true;
                _sprintPending = sprintEnabled;
                if (sprintEnabled && !background && !_physicalWHandoff)
                    QueueForegroundSprintLocked(generation, snapshot);
                _active = true;
                _activationPending = false;
                if (background)
                {
                    _backgroundRun = true;
                    var thread = new Thread(BackgroundInputLoop) { IsBackground = true, Name = "sWinBgInput" };
                    _backgroundThread = thread;
                    thread.Start();
                }
                if (_logger.IsEnabled)
                    _logger.Log($"AutoRun activated ({(background ? "background" : "foreground")}) for profile: {profile.Name}");
                return true;
            }
        }
        finally
        {
            lock (_autoRunLock)
            {
                if (_activationPending && generation == Volatile.Read(ref _injectionGeneration))
                {
                    _activationPending = false;
                    _ownerProfile = null;
                }
            }
        }
    }

    private bool CanActivate(Profile profile, long configurationGeneration) =>
        !_runtime.IsDisposed && _runtime.IsRunning && _runtime.AdvancedModeEnabled
        && profile.IsEnabled && profile.AutoRun.IsEnabled
        && ReferenceEquals(_runtime.ActiveProfile, profile) && _runtime.ProfileInputGenerationIsCurrent()
        && configurationGeneration == Volatile.Read(ref _configurationGeneration);

    private bool EnqueueForegroundDown(Key key, long generation, ForegroundIdentitySnapshot snapshot) =>
        _queue.Enqueue(new InputCommand(
            key,
            IsDown: true,
            Guard: this,
            Generation: generation,
            ForegroundGeneration: snapshot.Generation,
            ExpectedExecutable: snapshot.Executable));

    private void BeginAfterPhysicalWRelease()
    {
        lock (_autoRunLock)
        {
            if (!_active) return;
            _suppressedPhysicalWUp = true;
            if (_isBackground || _moveInjected) return;
            var snapshot = _foregroundGuard;
            if (snapshot is null && _bypassForegroundGuardForTesting)
            {
                snapshot = new ForegroundIdentitySnapshot(
                    IntPtr.Zero,
                    0,
                    _ownerProfile?.NormalizedExecutable,
                    _runtime.ActiveProfileGeneration);
            }
            if (snapshot is null || !EnqueueForegroundDown(Key.W, _activeInjectionGeneration, snapshot)) return;
            _moveInjected = true;
            _suppressedPhysicalWUp = false;
            QueueForegroundSprintLocked(_activeInjectionGeneration, snapshot);
        }
    }

    private void QueueForegroundSprintLocked(long generation, ForegroundIdentitySnapshot snapshot)
    {
        if (!_sprintPending) return;
        _sprintPending = false;
        if (_sprintToggleable)
        {
            if (!_sprintIntendedHeld) return;
            if (EnqueueForegroundDown(_sprintKey, generation, snapshot))
            {
                _sprintInjected = true;
                _sprintInjectedKey = _sprintKey;
            }
            return;
        }

        var rng = _random.Value!;
        int warmup = rng.Next(RNG_WARMUP_MIN_CALLS, RNG_WARMUP_MAX_CALLS + 1);
        for (int i = 0; i < warmup; i++) rng.Next();
        var down = new InputCommand(
            _sprintKey,
            IsDown: true,
            Guard: this,
            Generation: generation,
            ForegroundGeneration: snapshot.Generation,
            ExpectedExecutable: snapshot.Executable);
        var up = new InputCommand(_sprintKey, IsDown: false, DelayBeforeMs: rng.Next(TAP_DURATION_MIN_MS, TAP_DURATION_MAX_MS + 1));
        if (_queue.EnqueuePair(down, up) && _sprintKey == Key.W)
        {
            // The sprint tap releases the shared movement key; restore its hold only for this run.
            EnqueueForegroundDown(Key.W, generation, snapshot);
        }
    }

    private void ReleaseLocked(bool includeBackground, string? reason = null)
    {
        if (_activationPending)
        {
            Interlocked.Increment(ref _injectionGeneration);
            _activationPending = false;
            _ownerProfile = null;
        }
        if (!_active || (_isBackground && !includeBackground)) return;
        // Requested, not completed: the foreground path only queues the UP commands to the executor
        // and the background path only signals the worker via release flags flushed later in
        // FlushBackgroundReleases. Emitted before ResetRunState, while _isBackground is still
        // valid, and only for real releases — the gate above keeps no-ops silent.
        if (_logger.IsEnabled)
        {
            _logger.Log(reason is null
                ? $"AutoRun release requested ({(_isBackground ? "background" : "foreground")})"
                : $"AutoRun release requested ({reason})");
        }
        Interlocked.Increment(ref _injectionGeneration);
        bool releaseSprint = _sprintInjected || (!_isBackground && _sprintIntendedHeld && !_sprintPending);
        var sprintUpKey = _sprintInjected ? _sprintInjectedKey : _sprintKey;
        bool releaseW = _moveInjected || _suppressedPhysicalWUp;

        if (_isBackground)
        {
            _backgroundRun = false;
            _backgroundReleaseW |= releaseW;
            _backgroundReleaseSprint |= releaseSprint;
            if (releaseSprint) _backgroundReleaseSprintKey = sprintUpKey;
            ResetRunState(preserveBackgroundTarget: true);
            return;
        }
        if (releaseW) _queue.Enqueue(new InputCommand(Key.W, IsDown: false));
        if (releaseSprint) _queue.Enqueue(new InputCommand(sprintUpKey, IsDown: false));
        ResetRunState();
    }

    private void ResetAbortedActivation()
    {
        _isBackground = false;
        _backgroundTarget = default;
        _targetPid = 0;
        _physicalWHandoff = false;
        _suppressedPhysicalWUp = false;
        _sprintIntendedHeld = false;
        _sprintInjected = false;
        _foregroundGuard = null;
        _bypassForegroundGuardForTesting = false;
    }

    private void ResetRunState(bool preserveBackgroundTarget = false)
    {
        _moveInjected = false;
        _physicalWHandoff = false;
        _suppressedPhysicalWUp = false;
        _stopOnPhysicalWUp = false;
        _sprintInjected = false;
        _sprintIntendedHeld = false;
        _sprintToggleable = false;
        _sprintPending = false;
        _sprintKey = Key.None;
        _sprintInjectedKey = Key.None;
        _isBackground = false;
        _foregroundGuard = null;
        _bypassForegroundGuardForTesting = false;
        _ownerProfile = null;
        _active = false;
        if (!preserveBackgroundTarget)
        {
            _backgroundTarget = default;
            _targetPid = 0;
        }
    }

    private void ToggleSprintHold()
    {
        lock (_autoRunLock)
        {
            if (!_active) return;
            _sprintIntendedHeld = !_sprintIntendedHeld;
            if (_sprintPending || _isBackground) return;
            if (!_sprintIntendedHeld)
            {
                if (_sprintInjected) _queue.Enqueue(new InputCommand(_sprintInjectedKey, IsDown: false));
                _sprintInjected = false;
            }
            else if (_foregroundGuard is { } snapshot
                && EnqueueForegroundDown(_sprintKey, _activeInjectionGeneration, snapshot))
            {
                _sprintInjected = true;
                _sprintInjectedKey = _sprintKey;
            }
        }
    }

    private bool BackgroundRunIsCurrent(in BackgroundRun run) =>
        run.Generation == Volatile.Read(ref _injectionGeneration)
        && _active && _backgroundRun && _isBackground && ReferenceEquals(_ownerProfile, run.Owner)
        && !_runtime.IsDisposed && _runtime.IsRunning && _runtime.AdvancedModeEnabled
        && run.Owner.IsEnabled && run.Owner.AutoRun.IsEnabled;

    private bool PostAutoRunKey(in BackgroundRun run, Key key, bool isDown,
        bool repeat = false, bool forceAttach = false, bool sprint = false, bool sprintHold = false)
    {
        var posted = PostKeyToWindow(run, key, isDown, repeat, forceAttach, sprint, sprintHold);
        if (posted && isDown && sprintHold && (!BackgroundRunIsCurrent(run) || !_sprintIntendedHeld))
            PostKeyToWindow(run, key, isDown: false, repeat: false, sprint: sprint);
        return posted;
    }

    private bool PostKeyToWindow(in BackgroundRun run, Key key, bool isDown, bool repeat,
        bool forceAttach = false, bool sprint = false, bool sprintHold = false)
    {
        if (isDown && !BackgroundRunIsCurrent(run)) return false;
        var vk = KeyInteropUtilities.ToVirtualKey(key);
        if (vk == 0) return false;
        var scan = _transport.MapVirtualKey((uint)vk, 0);
        var systemKey = vk is 0x12 or 0xA4 or 0xA5 or 0x79;
        var message = (uint)(isDown
            ? (systemKey ? NativeMethods.WM_SYSKEYDOWN : NativeMethods.WM_KEYDOWN)
            : (systemKey ? NativeMethods.WM_SYSKEYUP : NativeMethods.WM_KEYUP));
        var lParam = BuildKeyLParam(scan, isDown, IsExtendedKey(key), repeat);
        var targetThread = _transport.GetWindowThreadProcessId(run.Window, out var actualPid);
        if (run.ProcessId == 0 || actualPid != run.ProcessId) return false;
        var currentThread = _transport.GetCurrentThreadId();
        var foreground = ForegroundIsTargetProcess(run.ProcessId);
        var candidate = (forceAttach || !foreground) && targetThread != 0 && targetThread != currentThread;
        var targetHung = candidate && _transport.IsHungAppWindow(run.Window);
        var willAttach = ShouldAttachBackgroundInput(true, foreground, targetThread, currentThread, targetHung, forceAttach);
        byte[]? savedState = null;
        if (willAttach)
        {
            savedState = new byte[256];
            if (!_transport.GetKeyboardState(savedState))
            {
                savedState = null;
                willAttach = false;
            }
        }
        bool attached = false;
        try
        {
            attached = willAttach && _transport.AttachThreadInput(currentThread, targetThread, true);
            if (!BackgroundTargetValid(run.Window, run.ProcessId)) return false;
            // Native preparation may block; no state lock is held and these are the last DOWN guards.
            if (isDown && (!BackgroundRunIsCurrent(run) || (sprintHold && !_sprintIntendedHeld))) return false;
            var posted = _transport.PostMessage(run.Window, message, (IntPtr)vk, lParam);
            if (posted) RecordBackgroundPost(run, key, isDown, sprint);
            return posted;
        }
        finally
        {
            try
            {
                if (attached) _transport.AttachThreadInput(currentThread, targetThread, false);
            }
            finally
            {
                if (savedState is not null) _transport.SetKeyboardState(savedState);
            }
        }
    }

    private void RecordBackgroundPost(in BackgroundRun run, Key key, bool isDown, bool sprint)
    {
        // Sprint may also use W; account for the operation's role rather than its virtual key.
        lock (_autoRunLock)
        {
            if (_backgroundThread != Thread.CurrentThread) return;
            if (isDown)
            {
                if (BackgroundRunIsCurrent(run))
                {
                    if (!sprint) _moveInjected = true;
                    else
                    {
                        _sprintInjected = true;
                        _sprintInjectedKey = key;
                    }
                }
                else if (!sprint) _backgroundReleaseW = true;
                else
                {
                    _backgroundReleaseSprint = true;
                    _backgroundReleaseSprintKey = key;
                }
            }
            else if (!sprint)
            {
                _moveInjected = false;
                _backgroundReleaseW = false;
            }
            else
            {
                if (_sprintInjectedKey == key) _sprintInjected = false;
                if (_backgroundReleaseSprintKey == key) _backgroundReleaseSprint = false;
            }
        }
    }

    internal static bool ShouldAttachBackgroundInput(
        bool onBackgroundThread,
        bool targetIsForegroundProcess,
        uint targetThread,
        uint currentThread,
        bool targetIsHung,
        bool forceAttach = false)
    {
        return onBackgroundThread && (forceAttach || !targetIsForegroundProcess)
            && targetThread != 0 && targetThread != currentThread && !targetIsHung;
    }

    private void BackgroundInputLoop()
    {
        BackgroundRun run = default;
        try
        {
            lock (_autoRunLock)
            {
                if (_backgroundThread != Thread.CurrentThread) return;
                // Activation captures this before publishing the worker; cancellation may already have reset its live owner.
                run = _backgroundTarget;
            }
            if (!ResolveBackgroundTarget(ref run)) return;
            if (!EnsureBackgroundMovementStarted(run)) return;
            DoDelayedBackgroundSprintActivation(run);
            bool wasForeground = true;
            bool sprintPending = false;
            int sprintDueTick = 0;
            while (BackgroundRunIsCurrent(run))
            {
                if (!BackgroundTargetValid(run.Window, run.ProcessId))
                {
                    StopBackgroundRun(run, "background target validation failed");
                    break;
                }
                bool foreground = ForegroundIsTargetProcess(run.ProcessId);
                bool reengageW = false;
                bool repeatW = false;
                Key sprintKey = Key.None;
                bool sprintDown = false;
                lock (_autoRunLock)
                {
                    if (!BackgroundRunIsCurrent(run)) break;
                    if (wasForeground && !foreground)
                    {
                        if (_sprintInjected) sprintKey = _sprintInjectedKey;
                        sprintPending = false;
                    }
                    else if (!wasForeground && foreground)
                    {
                        reengageW = true;
                        if (_sprintToggleable && _sprintIntendedHeld && !_sprintInjected)
                        {
                            sprintPending = true;
                            sprintDueTick = unchecked(Environment.TickCount + BG_SPRINT_REENGAGE_QUIET_MS);
                        }
                    }
                    wasForeground = foreground;

                    if (sprintKey == Key.None)
                    {
                        if (!sprintPending && foreground && _sprintToggleable && !_sprintIntendedHeld && _sprintInjected)
                        {
                            sprintKey = _sprintInjectedKey;
                        }
                        else if (!sprintPending && foreground && _sprintToggleable && _sprintIntendedHeld && !_sprintInjected)
                        {
                            sprintKey = _sprintKey;
                            sprintDown = true;
                        }
                        else if (sprintPending && unchecked(Environment.TickCount - sprintDueTick) >= 0)
                        {
                            sprintPending = false;
                            if (_sprintToggleable && _sprintIntendedHeld && !_sprintInjected && foreground)
                            {
                                sprintKey = _sprintKey;
                                sprintDown = true;
                            }
                        }
                        else if (!sprintPending && _moveInjected)
                        {
                            repeatW = true;
                        }
                    }
                }
                if (reengageW) PostAutoRunKey(run, Key.W, isDown: true, forceAttach: true);
                if (sprintKey != Key.None)
                    PostAutoRunKey(run, sprintKey, sprintDown, sprint: true, sprintHold: sprintDown);
                else if (repeatW && !reengageW)
                    PostAutoRunKey(run, Key.W, isDown: true, repeat: true);

                int sleep = AUTO_RUN_REPEAT_MS;
                if (sprintPending)
                {
                    int remaining = unchecked(sprintDueTick - Environment.TickCount);
                    sleep = Math.Max(1, Math.Min(AUTO_RUN_REPEAT_MS, remaining));
                }
                Thread.Sleep(sleep);
            }
        }
        catch (Exception ex)
        {
            Log($"BackgroundInputLoop exception: {ex}");
        }
        finally
        {
            StopBackgroundRun(run);
            FlushBackgroundReleases(run);
        }
    }

    private bool ResolveBackgroundTarget(ref BackgroundRun run)
    {
        if (!BackgroundRunIsCurrent(run)) return false;
        var foreground = _transport.GetForegroundWindow();
        _transport.GetWindowThreadProcessId(foreground, out var foregroundPid);
        if (run.Window == IntPtr.Zero || foreground != run.Window || foregroundPid == 0 || foregroundPid != run.ProcessId)
        {
            StopBackgroundRun(run, "background target validation failed");
            return false;
        }
        var child = _transport.GetChildWindow(run.Window);
        if (child != IntPtr.Zero)
        {
            _transport.GetWindowThreadProcessId(child, out var childPid);
            if (childPid == run.ProcessId) run = run with { Window = child };
        }
        lock (_autoRunLock)
        {
            if (!BackgroundRunIsCurrent(run)) return false;
            _backgroundTarget = run;
            return true;
        }
    }

    private void StopBackgroundRun(in BackgroundRun run, string? reason = null)
    {
        lock (_autoRunLock)
        {
            if (_backgroundThread == Thread.CurrentThread && _activeInjectionGeneration == run.Generation)
                ReleaseLocked(includeBackground: true, reason);
        }
    }

    private void FlushBackgroundReleases(in BackgroundRun run)
    {
        bool releaseW;
        bool releaseSprint;
        Key sprintKey;
        lock (_autoRunLock)
        {
            if (_backgroundThread != Thread.CurrentThread) return;
            releaseW = _backgroundReleaseW;
            releaseSprint = _backgroundReleaseSprint;
            sprintKey = _backgroundReleaseSprintKey;
            _backgroundReleaseW = false;
            _backgroundReleaseSprint = false;
        }
        try
        {
            try
            {
                if (releaseW) PostKeyToWindow(run, Key.W, isDown: false, repeat: false);
            }
            finally
            {
                if (releaseSprint) PostKeyToWindow(run, sprintKey, isDown: false, repeat: false, sprint: true);
            }
        }
        catch (Exception ex)
        {
            Log($"AutoRun background release failed: {ex.Message}");
        }
        finally
        {
            lock (_autoRunLock)
            {
                if (_backgroundThread == Thread.CurrentThread)
                {
                    _backgroundReleaseSprintKey = Key.None;
                    _backgroundTarget = default;
                    _targetPid = 0;
                    // This slot also blocks a new run and Anti-AFK until native cleanup has returned.
                    _backgroundThread = null;
                }
            }
        }
    }

    private bool EnsureBackgroundMovementStarted(in BackgroundRun run)
    {
        while (BackgroundRunIsCurrent(run))
        {
            bool handoff;
            bool repeat;
            lock (_autoRunLock)
            {
                if (!BackgroundRunIsCurrent(run)) return false;
                handoff = _physicalWHandoff;
                repeat = _suppressedPhysicalWUp;
            }
            if (!handoff)
            {
                if (!PostAutoRunKey(run, Key.W, isDown: true, repeat, forceAttach: true))
                {
                    StopBackgroundRun(run, "background movement injection failed");
                    return false;
                }
                lock (_autoRunLock)
                {
                    if (!BackgroundRunIsCurrent(run)) return false;
                    _suppressedPhysicalWUp = false;
                }
                return true;
            }
            Thread.Sleep(1);
        }
        return false;
    }

    private void DoDelayedBackgroundSprintActivation(in BackgroundRun run)
    {
        bool hold;
        Key sprintKey;
        lock (_autoRunLock)
        {
            if (!BackgroundRunIsCurrent(run) || !_sprintPending) return;
            hold = _sprintToggleable;
            sprintKey = _sprintKey;
        }
        Thread.Sleep(RandomDelay(BG_SPRINT_PREDELAY_MIN_MS, BG_SPRINT_PREDELAY_MAX_MS));
        lock (_autoRunLock)
        {
            if (!BackgroundRunIsCurrent(run) || !_sprintPending) return;
            _sprintPending = false;
            if (hold && (!_sprintIntendedHeld || _sprintInjected)) return;
        }
        if (hold)
        {
            if (ForegroundIsTargetProcess(run.ProcessId))
                PostAutoRunKey(run, sprintKey, isDown: true, sprint: true, sprintHold: true);
            return;
        }
        bool downPosted = false;
        try
        {
            downPosted = PostAutoRunKey(run, sprintKey, isDown: true, sprint: true);
            if (downPosted) Thread.Sleep(RandomDelay(BG_SPRINT_TAP_MIN_MS, BG_SPRINT_TAP_MAX_MS));
        }
        finally
        {
            // A successful transient DOWN stays owned even if cancellation resets the live run.
            if (downPosted) PostKeyToWindow(run, sprintKey, isDown: false, repeat: false, sprint: true);
        }
    }

    private bool ForegroundIsTargetProcess() => ForegroundIsTargetProcess(_targetPid);

    private bool ForegroundIsTargetProcess(uint targetPid)
    {
        var foreground = _transport.GetForegroundWindow();
        if (foreground == IntPtr.Zero || targetPid == 0) return false;
        _transport.GetWindowThreadProcessId(foreground, out var processId);
        return processId == targetPid;
    }

    private bool BackgroundTargetValid(IntPtr window, uint targetPid)
    {
        if (window == IntPtr.Zero || targetPid == 0) return false;
        _transport.GetWindowThreadProcessId(window, out var processId);
        return processId != 0 && processId == targetPid;
    }

    // Internal so AntiAfkStateMachine's posted ripple reuses the byte-identical lParam/extended-key
    // logic instead of copying it (semantic drift between the two background transports).
    internal static IntPtr BuildKeyLParam(uint scanCode, bool isDown, bool extended, bool repeat)
    {
        uint value = 1u | ((scanCode & 0xFFu) << 16);
        if (extended) value |= 1u << 24;
        if (!isDown) value |= (1u << 30) | (1u << 31);
        else if (repeat) value |= 1u << 30;
        return (IntPtr)(long)value;
    }

    internal static bool IsExtendedKey(Key key) => key is
        Key.RightAlt or Key.RightCtrl or Key.Insert or Key.Delete or Key.Home or Key.End or
        Key.PageUp or Key.PageDown or Key.Up or Key.Down or Key.Left or Key.Right or Key.NumLock or
        Key.PrintScreen or Key.Divide or Key.Apps;

    private int RandomDelay(int minMs, int maxMs) => _random.Value!.Next(minMs, maxMs + 1);
    private void Log(string message)
    {
        if (_logger.IsEnabled) _logger.Log(message);
    }
}
