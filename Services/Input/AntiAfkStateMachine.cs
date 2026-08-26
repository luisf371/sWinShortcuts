using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Input;
using sWinShortcuts.Models;
using Timer = System.Threading.Timer;

namespace sWinShortcuts.Services.Input;

/// <summary>
/// Anti-AFK's timer is only a producer. Each callback fails closed at the runtime, lifecycle,
/// profile, foreground, and Auto-Run boundaries before enqueueing. The executor calls the same
/// component as a per-step lock-free guard, so a started step always releases but later steps abort.
/// </summary>
internal sealed class AntiAfkStateMachine : IInputCommandGuard, IDisposable
{
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
        }
        if (timer is null) return;
        timer.Change(Timeout.Infinite, Timeout.Infinite);
        timer.Dispose();
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

            var profile = _runtime.ActiveProfile;
            if (profile is not { IsEnabled: true } || !profile.AntiAfk.IsEnabled)
            {
                if (logReason) Log("Anti-AFK skip: profile unavailable or disabled");
                return;
            }
            if (_autoRun.IsActive)
            {
                if (logReason) Log("Anti-AFK skip: auto-run active");
                return;
            }

            var intervalMs = (uint)(Math.Clamp(profile.AntiAfk.IntervalMinutes, 1, 15) * 60_000);
            var keyboardIdleMs = (_timestamp() - Volatile.Read(ref _lastPhysicalKeyboardTick)) * TickToMilliseconds;
            var now = _tickCount();
            var sinceLastFireMs = unchecked(now - _lastFireTick);
            if (keyboardIdleMs < intervalMs || sinceLastFireMs < intervalMs) return;

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
        finally
        {
            Volatile.Write(ref _tickRunning, 0);
        }
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

    private void Log(string message)
    {
        if (_logger.IsEnabled) _logger.Log(message);
    }
}
