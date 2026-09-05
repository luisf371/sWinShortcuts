using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using Timer = System.Threading.Timer;

namespace sWinShortcuts.Services.Input;

/// <summary>
/// Anti-AFK's timer is only a producer. Each callback fails closed at the runtime, lifecycle,
/// profile, foreground, and Auto-Run boundaries before enqueueing. The executor calls the same
/// component as a per-step lock-free guard, so a started step always releases but later steps abort.
/// Foreground SendMode keeps that exact path; Background/Forced instead post the WASD taps straight
/// to a PID-validated window of the profile's executable (never SendInput), using a target retained
/// from the profile's last activation and snapshotted once per tick so the gates and the posted
/// ripple always evaluate the same owner.
/// </summary>
internal sealed class AntiAfkStateMachine : IInputCommandGuard, IDisposable
{
    /// <summary>
    /// The retained game window a Background/Forced ripple posts to. Captured when the owning
    /// profile activates (focus gained), kept after focus is lost, and revalidated (window => PID)
    /// before every posted DOWN. Single slot, last-focused-game-wins.
    /// </summary>
    internal sealed record AntiAfkTarget(Profile Profile, IntPtr WindowHandle, uint ProcessId);

    private const int ANTI_AFK_PERIOD_MS = 5_000;
    private const int TAP_DURATION_MIN_MS = 20;
    private const int TAP_DURATION_MAX_MS = 30;
    private const int GAP_MIN_MS = 90;
    private const int GAP_MAX_MS = 160;
    private const int RNG_WARMUP_MIN_CALLS = 1;
    private const int RNG_WARMUP_MAX_CALLS = 5;
    private static readonly double TickToMilliseconds = 1000.0 / Stopwatch.Frequency;

    private readonly InputRuntimeState _runtime;
    private readonly AutoRunStateMachine _autoRun;
    private readonly ThreadLocal<Random> _random;
    private readonly ILoggerService _logger;
    private readonly IAutoRunTransport _transport;
    private readonly Func<long> _timestamp;
    private readonly Func<uint> _tickCount;
    private readonly object _lifecycleLock = new();

    private Timer? _timer;
    private int _tickRunning;
    private uint _lastFireTick;
    private long _lastPhysicalKeyboardTick;
    private int _diagnosticTicks;
    private long _lifecycleGeneration;
    private volatile bool _started;
    private int _disposed;

    // Retained background-posting target. Explicit Volatile/Interlocked ops (the codebase idiom —
    // avoids CS0420 on a volatile field): the capture publish is a volatile write; every clear is
    // interlocked so a stale release can never erase a newer capture. Only Stop()/session teardown
    // clear unconditionally (Interlocked.Exchange). Compare CAS results by reference: record
    // equality can match a newer same-valued capture.
    private AntiAfkTarget? _retainedTarget;
    private AntiAfkTarget? _reportedPostFailureTarget;
    private int _lastPostError;

    internal AntiAfkStateMachine(
        InputRuntimeState runtime,
        AutoRunStateMachine autoRun,
        ThreadLocal<Random> random,
        ILoggerService logger,
        IAutoRunTransport? transport = null,
        Func<long>? timestamp = null,
        Func<uint>? tickCount = null)
    {
        _runtime = runtime;
        _autoRun = autoRun;
        _random = random;
        _logger = logger;
        _transport = transport ?? new NativeAutoRunTransport();
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _tickCount = tickCount ?? (() => unchecked((uint)Environment.TickCount));
        _lastPhysicalKeyboardTick = _timestamp();
        _lastFireTick = _tickCount();
    }

    internal void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || _runtime.IsDisposed, this);
            if (_started) return;
            _lastPhysicalKeyboardTick = _timestamp();
            _lastFireTick = _tickCount();
            var generation = Interlocked.Increment(ref _lifecycleGeneration);
            _started = true;
            try
            {
                _timer = new Timer(_ => Tick(generation), null, ANTI_AFK_PERIOD_MS, ANTI_AFK_PERIOD_MS);
            }
            catch
            {
                _started = false;
                Interlocked.Increment(ref _lifecycleGeneration);
                throw;
            }
        }
    }

    internal void Stop()
    {
        Timer? timer;
        lock (_lifecycleLock)
        {
            _started = false;
            Interlocked.Increment(ref _lifecycleGeneration);
            timer = _timer;
            _timer = null;
            // Hard lifecycle boundary: the retained target dies with the session (unconditional
            // clear — nothing can capture after Stop, so no CAS is needed). The entry reports only
            // an actual removal: after a session-teardown clear the slot is already null and this
            // Exchange returns null, so Stop cannot double-report.
            var retained = Interlocked.Exchange(ref _retainedTarget, null);
            if (retained is not null)
            {
                Log("Anti-AFK: background target released (Stop)");
            }
        }
        if (timer is null) return;
        timer.Change(Timeout.Infinite, Timeout.Infinite);
        timer.Dispose();
    }

    /// <summary>
    /// Captures the retained posting target from the published foreground identity. Called under
    /// _profileLock from InputHookService.ActivateProfile AFTER the caller has already published
    /// the profile as ActiveProfile and settled the generation (so the lock-free tick can observe
    /// the new profile before this runs). Fail-closed on the same identity checks as
    /// AutoRunStateMachine.Activate; on failure a target owned by a DIFFERENT profile is
    /// CAS-cleared — the newly focused profile owns the slot now, and the previous owner must not
    /// keep receiving background WASD after its capture could not be replaced.
    /// </summary>
    internal void CaptureForegroundTarget(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var snapshot = _runtime.ForegroundIdentity;
        var executable = profile.NormalizedExecutable;
        if (snapshot is not null
            && snapshot.WindowHandle != IntPtr.Zero
            && snapshot.ProcessId != 0
            && snapshot.Generation == _runtime.ActiveProfileGeneration
            && snapshot.Generation == _runtime.PublishedForegroundGeneration
            && !string.IsNullOrEmpty(executable)
            && string.Equals(snapshot.Executable, executable, StringComparison.OrdinalIgnoreCase))
        {
            var window = snapshot.WindowHandle;
            var child = _transport.GetChildWindow(window);
            if (child != IntPtr.Zero)
            {
                _transport.GetWindowThreadProcessId(child, out var childProcessId);
                if (childProcessId == snapshot.ProcessId) window = child;
            }

            // Last-focused-game-wins: a plain publish overwrites whatever was stored. A racing
            // release compares against its observed reference, so it can never erase this capture.
            Volatile.Write(ref _retainedTarget, new AntiAfkTarget(profile, window, snapshot.ProcessId));
            Log($"Anti-AFK background target captured: hwnd=0x{window.ToInt64():X} pid={snapshot.ProcessId}");
            return;
        }

        // Failed capture: `profile` has still just become the settled active — i.e. last-focused —
        // profile, so a retained target owned by a different profile must not survive. A target
        // owned by `profile` itself is kept (same executable — still its own window; per-DOWN PID
        // revalidation handles death/reuse).
        var observed = Volatile.Read(ref _retainedTarget);
        if (observed is not null
            && !ReferenceEquals(observed.Profile, profile)
            && ReferenceEquals(Interlocked.CompareExchange(ref _retainedTarget, null, observed), observed))
        {
            Log("Anti-AFK: background target released (capture failed for the newly focused profile)");
        }
    }

    /// <summary>
    /// Owner-scoped release (identity edit, removal, master disable, hard deactivation). CAS
    /// against the observed target so a racing newer capture (another game focused) survives.
    /// </summary>
    internal void ReleaseOwnedBy(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var observed = Volatile.Read(ref _retainedTarget);
        if (observed is not null
            && ReferenceEquals(observed.Profile, profile)
            && ReferenceEquals(Interlocked.CompareExchange(ref _retainedTarget, null, observed), observed))
        {
            Log($"Anti-AFK: background target released (owner removed/disabled: {profile.Name})");
        }
    }

    /// <summary>Unconditional lifecycle clear (session switch away, hard teardown).</summary>
    internal void ReleaseForegroundTarget()
    {
        if (Interlocked.Exchange(ref _retainedTarget, null) is { })
        {
            Log("Anti-AFK: background target released (session teardown)");
        }
    }

    internal void NotePhysicalKeyboardActivity(long timestamp) =>
        Volatile.Write(ref _lastPhysicalKeyboardTick, timestamp);

    internal void Tick() => Tick(Volatile.Read(ref _lifecycleGeneration), requireStarted: false);

    internal TapStep[] BuildSequence()
    {
        var rng = _random.Value!;
        int warmup = rng.Next(RNG_WARMUP_MIN_CALLS, RNG_WARMUP_MAX_CALLS + 1);
        for (int i = 0; i < warmup; i++) rng.Next();

        TapStep Tap(Key key) => new(
            key,
            rng.Next(TAP_DURATION_MIN_MS, TAP_DURATION_MAX_MS + 1),
            rng.Next(GAP_MIN_MS, GAP_MAX_MS + 1));

        return [Tap(Key.W), Tap(Key.A), Tap(Key.S), Tap(Key.D)];
    }

    public bool CanExecute(in InputCommand command)
    {
        return !_runtime.IsDisposed
            && _runtime.IsRunning
            && _runtime.AdvancedModeEnabled
            && command.ExpectedProfile is { IsEnabled: true } profile
            && profile.AntiAfk.IsEnabled
            && _runtime.ProfileInputGenerationIsCurrent(profile, command.ForegroundGeneration)
            && ForegroundMatches(profile, command.ForegroundGeneration);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
    }

    private void Tick(long generation, bool requireStarted = true)
    {
        if (_runtime.IsDisposed || !_runtime.IsRunning || Volatile.Read(ref _disposed) != 0
            || (requireStarted && (!_started || generation != Volatile.Read(ref _lifecycleGeneration))))
        {
            return;
        }
        if (Interlocked.CompareExchange(ref _tickRunning, 1, 0) != 0) return;

        try
        {
            if (_runtime.IsDisposed || !_runtime.IsRunning || Volatile.Read(ref _disposed) != 0
                || (requireStarted && (!_started || generation != Volatile.Read(ref _lifecycleGeneration))))
            {
                return;
            }

            bool logReason = _logger.IsEnabled && (++_diagnosticTicks % 12) == 0;
            if (!_runtime.AdvancedModeEnabled)
            {
                if (logReason) Log("Anti-AFK skip: advanced-mode-off");
                return;
            }
            if (!_runtime.ProfileInputGenerationIsCurrent())
            {
                if (logReason) Log("Anti-AFK skip: foreground activation generation is stale");
                return;
            }

            // Snapshot the retained target ONCE, before any gating: the background branch below
            // evaluates its owner's mode, idle gate, and cadence against this same snapshot and
            // posts to it, so a capture swap racing the tick can never apply one profile's gating
            // semantics to another profile's window (the per-step target-identity check aborts
            // anything still in flight after a swap).
            var target = Volatile.Read(ref _retainedTarget);

            // Resolve the governing profile. The ACTIVE profile wins when it can run Anti-AFK;
            // otherwise Background/Forced fall back to the snapshotted target's owner — the game
            // being unfocused is the normal Background case, and its IsEnabled/AntiAfk/SendMode
            // are re-read live on every use below. Foreground hard-bails: with no active profile
            // there is nothing to foreground-match against (the browser-leak guard).
            var activeProfile = _runtime.ActiveProfile;
            Profile? profile;
            if (activeProfile is { IsEnabled: true } candidate && candidate.AntiAfk.IsEnabled)
            {
                profile = candidate;
            }
            else if (target?.Profile is { IsEnabled: true } owner
                && owner.AntiAfk.IsEnabled
                && owner.AntiAfk.SendMode != AntiAfkSendMode.Foreground)
            {
                profile = owner;
            }
            else
            {
                profile = null;
            }

            if (profile is null)
            {
                if (logReason) Log("Anti-AFK skip: profile unavailable or disabled");
                return;
            }
            if (_autoRun.IsActive)
            {
                if (logReason) Log("Anti-AFK skip: auto-run active");
                return;
            }

            var now = _tickCount();
            if (profile.AntiAfk.SendMode == AntiAfkSendMode.Foreground)
            {
                var foregroundIntervalMs = (uint)(Math.Clamp(profile.AntiAfk.IntervalMinutes, 1, 15) * 60_000);
                var foregroundIdleMs = (_timestamp() - Volatile.Read(ref _lastPhysicalKeyboardTick)) * TickToMilliseconds;
                // Foreground keeps the dual idle+cadence gate: the ripple waits for real keyboard
                // inactivity AND fires at most once per interval.
                if (foregroundIdleMs < foregroundIntervalMs
                    || unchecked(now - _lastFireTick) < foregroundIntervalMs)
                {
                    return;
                }

                TickForeground(profile, generation, requireStarted, now, logReason);
                return;
            }

            FireBackgroundRipple(target, generation, requireStarted, now, logReason);
        }
        finally
        {
            Volatile.Write(ref _tickRunning, 0);
        }
    }

    // The unchanged Foreground (SendInput) path: live foreground HWND/PID verified, WASD sequence
    // enqueued on the FIFO executor via the Auto-Run arbitration.
    private void TickForeground(Profile profile, long generation, bool requireStarted, uint now, bool logReason)
    {
        var foregroundGeneration = _runtime.ActiveProfileGeneration;
        if (!ForegroundMatches(profile, foregroundGeneration))
        {
            if (logReason) Log("Anti-AFK skip: foreground is not the active game");
            return;
        }

        if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0
            || (requireStarted && (!_started || generation != Volatile.Read(ref _lifecycleGeneration))))
        {
            return;
        }

        var command = new InputCommand(
            Key.None,
            IsDown: false,
            Kind: InputCommandKind.Sequence,
            Sequence: BuildSequence(),
            Guard: this,
            ForegroundGeneration: foregroundGeneration,
            ExpectedProfile: profile,
            ExpectedExecutable: profile.NormalizedExecutable);

        if (_runtime.IsDisposed || Volatile.Read(ref _disposed) != 0
            || (requireStarted && (!_started || generation != Volatile.Read(ref _lifecycleGeneration))))
        {
            return;
        }
        if (_autoRun.TryEnqueueWhileInactive(command))
        {
            _lastFireTick = now;
            Log("Anti-AFK fired WASD ripple");
        }
    }

    // Background/Forced: post the WASD ripple straight to the retained game window. Never SendInput,
    // never the executor — so it is structurally impossible for these modes to type into whatever app
    // currently has focus. Runs only on this timer (pool) thread; no sleeps are taken under any lock.
    // The mode, idle, and cadence gates below evaluate the tick's TARGET SNAPSHOT — the same target
    // the loop posts to — so gating and dispatch can never disagree about whose window and whose
    // interval semantics apply.
    private void FireBackgroundRipple(
        AntiAfkTarget? target,
        long generation,
        bool requireStarted,
        uint now,
        bool logReason)
    {
        if (target is null)
        {
            if (logReason) Log("Anti-AFK skip: background target unavailable");
            return;
        }

        // The ripple is committed to this target/owner/mode trio; the owner's settings govern.
        var owner = target.Profile;
        var mode = owner.AntiAfk.SendMode;
        if (mode == AntiAfkSendMode.Foreground)
        {
            // The owner's live mode no longer selects a posted ripple (edited mid-tick); its next
            // tick takes the Foreground path instead.
            return;
        }

        // Ownership gate: activation publishes the new ActiveProfile BEFORE its capture runs, and
        // this tick takes no lock — a different enabled active profile means that profile's own
        // settings govern and the retained target must stay silent. null (game unfocused — the
        // normal Background case) and owner (game focused) both pass.
        var active = _runtime.ActiveProfile;
        if (active is not null && !ReferenceEquals(active, owner))
        {
            if (logReason) Log("Anti-AFK skip: background target owner superseded");
            return;
        }

        var intervalMs = (uint)(Math.Clamp(owner.AntiAfk.IntervalMinutes, 1, 15) * 60_000);
        var keyboardIdleMs = (_timestamp() - Volatile.Read(ref _lastPhysicalKeyboardTick)) * TickToMilliseconds;
        var sinceLastFireMs = unchecked(now - _lastFireTick);
        // Forced skips the keyboard-idle gate (fires on the timer regardless of activity); the
        // since-last-fire cadence gate applies to every mode so each interval fires exactly once.
        // Both are read from the SNAPSHOT owner — a target captured for a different profile
        // mid-tick must never fire under this profile's already-passed gates.
        if (mode != AntiAfkSendMode.Forced && keyboardIdleMs < intervalMs) return;
        if (sinceLastFireMs < intervalMs) return;

        var sentAny = false;
        try
        {
            foreach (var step in BuildSequence())
            {
                // Fail-closed per-step guards BEFORE the DOWN, mirroring CanExecute + the
                // executor's per-step abort: an in-flight ripple cannot keep sending after the
                // feature is disabled, the mode is switched, or another profile takes focus.
                // The generation check re-validates the foreground identity: it can advance while
                // the asynchronous activation worker has not published the new active profile
                // yet, and the previous game must not keep receiving the ripple through that
                // window.
                if (!CanPostBackgroundStep(target, owner, mode, generation, requireStarted)) break;

                if (!TargetStillValid(target))
                {
                    // Dead/reused window: stop posting and clear (fail closed, no retry storm).
                    ReleaseInvalidTarget(target);
                    break;
                }

                // The LAST guard before the DOWN — once acquired, the finally releases it on every
                // path (abort after acquire, failed post, exception).
                if (!_autoRun.TryBeginAntiAfkTap()) break;

                var downPosted = false;
                try
                {
                    // Arbitration can wait behind Auto-Run's lock. Recheck every live guard after
                    // acquiring it so a concurrent disable, mode/owner change, or recapture cannot
                    // leak one stale DOWN through that wait window.
                    if (!TargetStillValid(target))
                    {
                        ReleaseInvalidTarget(target);
                        break;
                    }
                    if (!CanPostBackgroundStep(target, owner, mode, generation, requireStarted)) break;

                    downPosted = PostTapToWindow(target, step.Key, isDown: true);
                    if (!downPosted)
                    {
                        if (!ReferenceEquals(_reportedPostFailureTarget, target))
                        {
                            _reportedPostFailureTarget = target;
                            Log(_lastPostError == 5
                                ? "Anti-AFK background post failed: Win32 error 5 (access denied; run sWinShortcuts at the target's integrity level)"
                                : $"Anti-AFK background post failed: Win32 error {_lastPostError}");
                        }
                        break;
                    }
                    sentAny |= downPosted;
                    Thread.Sleep(step.DownMs);
                }
                finally
                {
                    // Every started DOWN is paired by an UP, mirroring InputExecutor.ExecuteSequence.
                    try
                    {
                        if (downPosted) PostTapToWindow(target, step.Key, isDown: false);
                    }
                    catch (Exception ex)
                    {
                        Log($"Anti-AFK background tap release failed: {ex.Message}");
                    }

                    _autoRun.EndAntiAfkTap();
                }

                Thread.Sleep(step.GapMs);
            }
        }
        catch (Exception ex)
        {
            // The ripple runs on a System.Threading.Timer callback — an escaping exception would
            // take the process down, so contain it like the executor's per-command catch.
            Log($"Anti-AFK background ripple failed: {ex.Message}");
        }

        if (sentAny)
        {
            _lastFireTick = now;
            Log(mode == AntiAfkSendMode.Forced
                ? "Anti-AFK queued WASD ripple (forced)"
                : "Anti-AFK queued WASD ripple (background)");
        }
    }

    private bool CanPostBackgroundStep(
        AntiAfkTarget target,
        Profile owner,
        AntiAfkSendMode mode,
        long generation,
        bool requireStarted)
    {
        if (_runtime.IsDisposed || !_runtime.IsRunning || Volatile.Read(ref _disposed) != 0
            || (requireStarted && (!_started || generation != Volatile.Read(ref _lifecycleGeneration)))
            || !_runtime.AdvancedModeEnabled
            || !_runtime.ProfileInputGenerationIsCurrent()
            || owner is not { IsEnabled: true }
            || !owner.AntiAfk.IsEnabled
            || owner.AntiAfk.SendMode != mode
            || !ReferenceEquals(Volatile.Read(ref _retainedTarget), target))
        {
            return false;
        }

        var active = _runtime.ActiveProfile;
        return active is null || ReferenceEquals(active, owner);
    }

    private bool ForegroundMatches(Profile profile, long generation)
    {
        var snapshot = _runtime.ForegroundIdentity;
        if (snapshot is null || snapshot.Generation != generation
            || !string.Equals(snapshot.Executable, profile.NormalizedExecutable, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var foreground = _transport.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground != snapshot.WindowHandle) return false;
        _transport.GetWindowThreadProcessId(foreground, out var processId);
        return processId != 0 && processId == snapshot.ProcessId;
    }

    private bool TargetStillValid(AntiAfkTarget target)
    {
        if (target.WindowHandle == IntPtr.Zero || target.ProcessId == 0) return false;
        _transport.GetWindowThreadProcessId(target.WindowHandle, out var processId);
        return processId != 0 && processId == target.ProcessId;
    }

    private void ReleaseInvalidTarget(AntiAfkTarget target)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _retainedTarget, null, target), target))
        {
            Log($"Anti-AFK background target invalid: hwnd=0x{target.WindowHandle.ToInt64():X} expected-pid={target.ProcessId}");
        }
    }

    private bool ForegroundIsProcess(uint processId)
    {
        if (processId == 0) return false;
        var foreground = _transport.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        _transport.GetWindowThreadProcessId(foreground, out var foregroundPid);
        return foregroundPid == processId;
    }

    // Mirrors AutoRunStateMachine.PostKeyToWindow's core: vk/scan, WM_SYSKEY selection, lParam bits,
    // AttachThreadInput (only when the target is NOT the foreground process — attaching while the
    // game is focused resets the shared keyboard state and blocks A/D), a keyboard-state snapshot
    // restored after detach (CapsLock-safe), and the PostMessage result. NEVER called from the
    // hook/dispatcher thread — the Anti-AFK tick is a System.Threading.Timer pool callback, so this
    // always runs onBackgroundThread semantics.
    private bool PostTapToWindow(AntiAfkTarget target, Key key, bool isDown)
    {
        if (isDown && _runtime.IsDisposed) return false;
        var vk = KeyInteropUtilities.ToVirtualKey(key);
        if (vk == 0) return true;
        var scan = _transport.MapVirtualKey((uint)vk, 0);
        var systemKey = vk is 0x12 or 0xA4 or 0xA5 or 0x79;
        var message = (uint)(isDown
            ? (systemKey ? NativeMethods.WM_SYSKEYDOWN : NativeMethods.WM_KEYDOWN)
            : (systemKey ? NativeMethods.WM_SYSKEYUP : NativeMethods.WM_KEYUP));
        var lParam = AutoRunStateMachine.BuildKeyLParam(scan, isDown, AutoRunStateMachine.IsExtendedKey(key), repeat: false);
        var hwnd = target.WindowHandle;
        var targetThread = _transport.GetWindowThreadProcessId(hwnd, out _);
        var currentThread = _transport.GetCurrentThreadId();
        var foreground = ForegroundIsProcess(target.ProcessId);
        var candidate = !foreground && targetThread != 0 && targetThread != currentThread;
        var targetHung = candidate && _transport.IsHungAppWindow(hwnd);
        var willAttach = AutoRunStateMachine.ShouldAttachBackgroundInput(
            onBackgroundThread: true,
            targetIsForegroundProcess: foreground,
            targetThread,
            currentThread,
            targetHung);

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
            var posted = _transport.PostMessage(hwnd, message, (IntPtr)vk, lParam);
            _lastPostError = posted ? 0 : _transport.GetLastWin32Error();
            return posted;
        }
        finally
        {
            if (attached) _transport.AttachThreadInput(currentThread, targetThread, false);
            if (savedState is not null) _transport.SetKeyboardState(savedState);
        }
    }

    private void Log(string message)
    {
        if (_logger.IsEnabled) _logger.Log(message);
    }
}
