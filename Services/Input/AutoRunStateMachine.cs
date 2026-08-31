using System;
using System.Runtime.CompilerServices;
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
    private Profile? _ownerProfile;
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
    private bool _sprintIntendedHeld;

    private bool _isBackground;
    private IntPtr _targetHwnd;
    private uint _targetPid;
    private Thread? _backgroundThread;
    private bool _backgroundRun;
    private volatile bool _backgroundTargetFocused;
    private bool _backgroundTargetResolved;
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
            if (_active || _runtime.IsDisposed || !_runtime.IsRunning
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
            if (_active || _runtime.IsDisposed || !_runtime.IsRunning)
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
        lock (_autoRunLock)
        {
            if (_runtime.IsDisposed || !_runtime.IsRunning || _active || !_runtime.AdvancedModeEnabled
                || !profile.IsEnabled || !settings.IsEnabled || !_runtime.ProfileInputGenerationIsCurrent()
                || configurationGeneration != Volatile.Read(ref _configurationGeneration))
            {
                return false;
            }

            if (_backgroundThread?.IsAlive == true)
            {
                return false;
            }

            // An Anti-AFK background tap step is in flight (a ~150 ms-per-step window): activating
            // now would interleave this run's W/sprint work with the tap's per-step latch. Fail
            // closed once — the chord passes through to the game and the user re-presses — a
            // lesser evil than a stray tap-side release cancelling an active run later. The
            // tick-level _autoRun.IsActive gate already blocks new ripples while a run is active.
            if (_antiAfkTapInFlight)
            {
                return false;
            }

            var snapshot = _runtime.ForegroundIdentity;
            var executable = profile.NormalizedExecutable;
            var foreground = _transport.GetForegroundWindow();
            _transport.GetWindowThreadProcessId(foreground, out var foregroundPid);
            if (snapshot is null || snapshot.Generation != _runtime.ActiveProfileGeneration
                || snapshot.Generation != _runtime.PublishedForegroundGeneration
                || foreground == IntPtr.Zero || foreground != snapshot.WindowHandle
                || foregroundPid == 0 || foregroundPid != snapshot.ProcessId
                || string.IsNullOrEmpty(executable)
                || !string.Equals(snapshot.Executable, executable, StringComparison.OrdinalIgnoreCase))
            {
                Log("AutoRun: foreground not confirmed as the profile game; activation aborted");
                return false;
            }

            var background = settings.SendMode == AutoRunSendMode.Background;
            if (background)
            {
                _targetHwnd = snapshot.WindowHandle;
                _targetPid = snapshot.ProcessId;
                _backgroundTargetResolved = false;
            }
            else
            {
                _foregroundGuard = snapshot;
                _bypassForegroundGuardForTesting = false;
            }

            _isBackground = background;
            _backgroundTargetFocused = background;
            _sprintKey = settings.SprintKey;
            _sprintToggleable = settings.SprintEnabled && settings.SprintMode == SprintActivation.Hold;
            _sprintIntendedHeld = _sprintToggleable;
            _sprintInjected = false;
            _snapshotTriggerVk = KeyInteropUtilities.ToVirtualKey(settings.TriggerKey);
            _snapshotModifier = settings.TriggerModifier;
            _physicalWHandoff = _wPhysicallyDown;
            _suppressedPhysicalWUp = false;
            _stopOnPhysicalWUp = false;
            var sprintVk = KeyInteropUtilities.ToVirtualKey(_sprintKey);
            _sprintPhysicallyDown = sprintVk != 0 && IsDown(sprintVk);

            long generation = 0;
            if (!background)
            {
                generation = Interlocked.Increment(ref _injectionGeneration);
                _activeInjectionGeneration = generation;
            }

            _moveInjected = false;
            if (!_physicalWHandoff && !background &&
                !EnqueueForegroundDown(Key.W, generation, snapshot))
            {
                ResetAbortedActivation();
                return false;
            }
            if (!_physicalWHandoff && !background) _moveInjected = true;

            _sprintPending = settings.SprintEnabled;
            if (settings.SprintEnabled && !background && !_physicalWHandoff)
            {
                QueueForegroundSprintLocked(generation, snapshot);
            }

            _ownerProfile = profile;
            _active = true;
            if (background)
            {
                _backgroundRun = true;
                var thread = new Thread(BackgroundInputLoop) { IsBackground = true, Name = "sWinBgInput" };
                _backgroundThread = thread;
                thread.Start();
            }
            return true;
        }
    }

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
        _queue.EnqueuePair(down, up);
    }

    private void ReleaseLocked(bool includeBackground)
    {
        if (!_active || (_isBackground && !includeBackground)) return;
        Interlocked.Increment(ref _injectionGeneration);
        bool releaseSprint = _sprintInjected || (_sprintIntendedHeld && !_sprintPending);
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
        _targetHwnd = IntPtr.Zero;
        _targetPid = 0;
        _physicalWHandoff = false;
        _suppressedPhysicalWUp = false;
        _backgroundTargetFocused = false;
        _backgroundTargetResolved = false;
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
        _backgroundTargetFocused = false;
        _foregroundGuard = null;
        _bypassForegroundGuardForTesting = false;
        _ownerProfile = null;
        _active = false;
        if (!preserveBackgroundTarget)
        {
            _targetHwnd = IntPtr.Zero;
            _targetPid = 0;
            _backgroundTargetResolved = false;
        }
    }

    private void ToggleSprintHold()
    {
        lock (_autoRunLock)
        {
            if (!_active) return;
            if (_sprintPending)
            {
                _sprintIntendedHeld = !_sprintIntendedHeld;
                return;
            }

            if (_sprintInjected)
            {
                _sprintIntendedHeld = false;
                if (_isBackground)
                {
                    return;
                }
                _queue.Enqueue(new InputCommand(_sprintInjectedKey, IsDown: false));
                _sprintInjected = false;
                return;
            }

            _sprintIntendedHeld = true;
            if (_isBackground)
            {
                return;
            }
            if (_foregroundGuard is { } snapshot
                && EnqueueForegroundDown(_sprintKey, _activeInjectionGeneration, snapshot))
            {
                _sprintInjected = true;
                _sprintInjectedKey = _sprintKey;
            }
        }
    }

    private bool PostAutoRunKey(Key key, bool isDown, bool repeat = false, bool forceAttach = false)
    {
        if (isDown && _runtime.IsDisposed) return false;
        if (!BackgroundTargetValid(_targetHwnd)) return false;
        return PostKeyToWindow(_targetHwnd, key, isDown, repeat, forceAttach);
    }

    private bool PostKeyToWindow(IntPtr hwnd, Key key, bool isDown, bool repeat, bool forceAttach = false)
    {
        if (isDown && _runtime.IsDisposed) return false;
        var vk = KeyInteropUtilities.ToVirtualKey(key);
        if (vk == 0) return true;
        var scan = _transport.MapVirtualKey((uint)vk, 0);
        var systemKey = vk is 0x12 or 0xA4 or 0xA5 or 0x79;
        var message = (uint)(isDown
            ? (systemKey ? NativeMethods.WM_SYSKEYDOWN : NativeMethods.WM_KEYDOWN)
            : (systemKey ? NativeMethods.WM_SYSKEYUP : NativeMethods.WM_KEYUP));
        var lParam = BuildKeyLParam(scan, isDown, IsExtendedKey(key), repeat);
        var targetThread = _transport.GetWindowThreadProcessId(hwnd, out _);
        var currentThread = _transport.GetCurrentThreadId();
        var onBackgroundThread = ReferenceEquals(Thread.CurrentThread, _backgroundThread);
        var foreground = onBackgroundThread && ForegroundIsTargetProcess();
        var candidate = onBackgroundThread && (forceAttach || !foreground)
            && targetThread != 0 && targetThread != currentThread;
        var targetHung = candidate && _transport.IsHungAppWindow(hwnd);
        var willAttach = ShouldAttachBackgroundInput(
            onBackgroundThread, foreground, targetThread, currentThread, targetHung, forceAttach);

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
            if (isDown && _runtime.IsDisposed) return false;
            return _transport.PostMessage(hwnd, message, (IntPtr)vk, lParam);
        }
        finally
        {
            if (attached) _transport.AttachThreadInput(currentThread, targetThread, false);
            if (savedState is not null) _transport.SetKeyboardState(savedState);
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
        try
        {
            if (!ResolveBackgroundTarget()) return;
            if (!EnsureBackgroundMovementStarted()) return;
            DoDelayedBackgroundSprintActivation();
            bool wasForeground = _backgroundTargetFocused;
            bool sprintPending = false;
            int sprintDueTick = 0;
            while (true)
            {
                bool stop = false;
                lock (_autoRunLock)
                {
                    if (!_backgroundRun || _backgroundThread != Thread.CurrentThread || !_active
                        || !_isBackground || !_runtime.IsRunning || _runtime.IsDisposed)
                    {
                        if (_backgroundThread == Thread.CurrentThread && _active && _isBackground)
                        {
                            ReleaseLocked(includeBackground: true);
                        }
                        stop = true;
                    }
                    else if (!BackgroundTargetValid(_targetHwnd))
                    {
                        ReleaseLocked(includeBackground: true);
                        stop = true;
                    }
                    else
                    {
                        bool foreground = ForegroundIsTargetProcess();
                        _backgroundTargetFocused = foreground;
                        if (wasForeground && !foreground)
                        {
                            if (_sprintInjected) _sprintInjected = false;
                            sprintPending = false;
                        }
                        else if (!wasForeground && foreground)
                        {
                            if (_sprintToggleable && _sprintIntendedHeld && !_sprintInjected)
                            {
                                if (PostAutoRunKey(Key.W, true, forceAttach: true)) _moveInjected = true;
                                sprintPending = true;
                                sprintDueTick = unchecked(Environment.TickCount + BG_SPRINT_REENGAGE_QUIET_MS);
                            }
                            else if (PostAutoRunKey(Key.W, true, forceAttach: true))
                            {
                                _moveInjected = true;
                            }
                        }
                        wasForeground = foreground;

                        if (!sprintPending && foreground && _sprintToggleable && !_sprintIntendedHeld && _sprintInjected)
                        {
                            if (PostAutoRunKey(_sprintInjectedKey, false)) _sprintInjected = false;
                        }
                        else if (!sprintPending && foreground && _sprintToggleable && _sprintIntendedHeld && !_sprintInjected)
                        {
                            if (PostAutoRunKey(_sprintKey, true))
                            {
                                _sprintInjected = true;
                                _sprintInjectedKey = _sprintKey;
                            }
                        }
                        else if (sprintPending && unchecked(Environment.TickCount - sprintDueTick) >= 0)
                        {
                            sprintPending = false;
                            if (_sprintToggleable && _sprintIntendedHeld && !_sprintInjected && foreground
                                && PostAutoRunKey(_sprintKey, true))
                            {
                                _sprintInjected = true;
                                _sprintInjectedKey = _sprintKey;
                            }
                        }
                        else if (!sprintPending && _moveInjected)
                        {
                            PostKeyToWindow(_targetHwnd, Key.W, true, repeat: true);
                        }
                    }
                }
                if (stop) break;
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
            lock (_autoRunLock)
            {
                if (_backgroundThread == Thread.CurrentThread && _active && _isBackground)
                {
                    var hwnd = _targetHwnd;
                    bool releaseW = _moveInjected || _suppressedPhysicalWUp;
                    bool releaseSprint = _sprintInjected || (_sprintIntendedHeld && !_sprintPending);
                    var sprintKey = _sprintInjected ? _sprintInjectedKey : _sprintKey;
                    Interlocked.Increment(ref _injectionGeneration);
                    ResetRunState();
                    try
                    {
                        if (hwnd != IntPtr.Zero && releaseW) PostKeyToWindow(hwnd, Key.W, false, false);
                        if (hwnd != IntPtr.Zero && releaseSprint) PostKeyToWindow(hwnd, sprintKey, false, false);
                    }
                    catch { }
                }
            }
        }
        finally
        {
            lock (_autoRunLock)
            {
                if (_backgroundThread == Thread.CurrentThread)
                {
                    FlushBackgroundReleasesLocked();
                    _backgroundThread = null;
                }
            }
        }
    }

    private bool ResolveBackgroundTarget()
    {
        lock (_autoRunLock)
        {
            if (!_backgroundRun || _backgroundThread != Thread.CurrentThread || !_active || !_isBackground
                || !_runtime.IsRunning || _runtime.IsDisposed)
            {
                return false;
            }

            var frame = _targetHwnd;
            var foreground = _transport.GetForegroundWindow();
            _transport.GetWindowThreadProcessId(foreground, out var foregroundPid);
            if (frame == IntPtr.Zero || foreground != frame || foregroundPid == 0 || foregroundPid != _targetPid)
            {
                ReleaseLocked(includeBackground: true);
                return false;
            }

            var child = _transport.GetChildWindow(frame);
            if (child != IntPtr.Zero)
            {
                _transport.GetWindowThreadProcessId(child, out var childPid);
                if (childPid == _targetPid) _targetHwnd = child;
            }
            _backgroundTargetFocused = true;
            _backgroundTargetResolved = true;
            return true;
        }
    }

    private void FlushBackgroundReleasesLocked()
    {
        try
        {
            if (_backgroundTargetResolved)
            {
                if (_backgroundReleaseW) PostAutoRunKey(Key.W, isDown: false);
                if (_backgroundReleaseSprint) PostAutoRunKey(_backgroundReleaseSprintKey, isDown: false);
            }
        }
        catch (Exception ex)
        {
            Log($"AutoRun background release failed: {ex.Message}");
        }
        finally
        {
            _backgroundReleaseW = false;
            _backgroundReleaseSprint = false;
            _backgroundReleaseSprintKey = Key.None;
            _targetHwnd = IntPtr.Zero;
            _targetPid = 0;
            _backgroundTargetResolved = false;
        }
    }

    private bool EnsureBackgroundMovementStarted()
    {
        while (true)
        {
            lock (_autoRunLock)
            {
                if (!_backgroundRun || _backgroundThread != Thread.CurrentThread || !_active
                    || !_isBackground || !_runtime.IsRunning || _runtime.IsDisposed)
                {
                    return false;
                }
                if (!_physicalWHandoff)
                {
                    if (!PostAutoRunKey(Key.W, true, _suppressedPhysicalWUp, forceAttach: true))
                    {
                        ReleaseLocked(includeBackground: true);
                        return false;
                    }
                    _moveInjected = true;
                    _suppressedPhysicalWUp = false;
                    return true;
                }
            }
            Thread.Sleep(1);
        }
    }

    private void DoDelayedBackgroundSprintActivation()
    {
        bool hold;
        Key sprintKey;
        lock (_autoRunLock)
        {
            if (!_backgroundRun || _backgroundThread != Thread.CurrentThread || !_active || !_sprintPending) return;
            hold = _sprintToggleable;
            sprintKey = _sprintKey;
        }
        Thread.Sleep(RandomDelay(BG_SPRINT_PREDELAY_MIN_MS, BG_SPRINT_PREDELAY_MAX_MS));
        lock (_autoRunLock)
        {
            if (!_backgroundRun || _backgroundThread != Thread.CurrentThread || !_active || _runtime.IsDisposed
                || !BackgroundTargetValid(_targetHwnd) || !_sprintPending)
            {
                return;
            }
            _sprintPending = false;
            if (hold)
            {
                if (!_sprintIntendedHeld || _sprintInjected || !ForegroundIsTargetProcess()
                    || !PostAutoRunKey(sprintKey, true)) return;
                _sprintInjected = true;
                _sprintInjectedKey = sprintKey;
                return;
            }
            if (!PostAutoRunKey(sprintKey, true)) return;
        }
        Thread.Sleep(RandomDelay(BG_SPRINT_TAP_MIN_MS, BG_SPRINT_TAP_MAX_MS));
        lock (_autoRunLock)
        {
            if (_backgroundRun && _backgroundThread == Thread.CurrentThread && _active && _isBackground
                && BackgroundTargetValid(_targetHwnd))
            {
                PostAutoRunKey(sprintKey, false);
            }
        }
    }

    private bool ForegroundIsTargetProcess()
    {
        var foreground = _transport.GetForegroundWindow();
        if (foreground == IntPtr.Zero || _targetPid == 0) return false;
        _transport.GetWindowThreadProcessId(foreground, out var processId);
        return processId == _targetPid;
    }

    private bool BackgroundTargetValid(IntPtr window)
    {
        if (window == IntPtr.Zero || _targetPid == 0) return false;
        _transport.GetWindowThreadProcessId(window, out var processId);
        return processId != 0 && processId == _targetPid;
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
