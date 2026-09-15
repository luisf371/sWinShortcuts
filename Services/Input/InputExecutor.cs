using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.Services.Input;

internal interface IInputQueue
{
    bool Enqueue(in InputCommand command);

    bool EnqueuePair(in InputCommand down, in InputCommand up);
}

internal interface IInputCommandGuard
{
    bool CanExecute(in InputCommand command);

    void OnCompleted(in InputCommand command)
    {
    }
}

// Hook publications distinguish an observed physical pair from synthetic/GetAsyncKeyState state.
internal interface IMacroInputContext
{
    bool PhysicalModifiersDown { get; }
    bool IsPhysicalKeyDown(int virtualKey);
    bool IsPhysicalMouseButtonDown(MouseButton button);
    bool HasPhysicalKeyTakeover(int virtualKey);
    bool HasPhysicalMouseTakeover(MouseButton button);
}

internal enum InputCommandKind
{
    KeyTransition,
    KeyTap,
    DummyKey,
    Sequence,
    WheelTap,
    MouseButtonTransition,
    MoveTo,
    MouseWheel,
    MacroReserveModifiers,
    MacroCleanup
}

[Flags]
internal enum InputHoldOwner : byte
{
    None = 0,
    Combined = 1,
    Caps = 2,
    HoldBreath = 4,
    AutoRunMovement = 8,
    AutoRunSprint = 16,
    Macro = 32
}

internal readonly record struct TapStep(Key Key, int DownMs, int GapMs);

internal class InputCommandAcknowledgement
{
    private int _downSent;
    private int _cancelled;
    private int _deferredForPhysicalModifiers;

    internal bool DownSent => Volatile.Read(ref _downSent) != 0;

    internal bool IsCancelled => Volatile.Read(ref _cancelled) != 0;

    internal bool DeferredForPhysicalModifiers => Volatile.Read(ref _deferredForPhysicalModifiers) != 0;

    internal void MarkDownSent() => Volatile.Write(ref _downSent, 1);

    internal void Cancel() => Volatile.Write(ref _cancelled, 1);

    internal void DeferForPhysicalModifiers() => Volatile.Write(ref _deferredForPhysicalModifiers, 1);
}

internal readonly record struct InputCommand(
    Key Key,
    bool IsDown,
    int DelayBeforeMs = 0,
    InputCommandKind Kind = InputCommandKind.KeyTransition,
    TapStep[]? Sequence = null,
    IInputCommandGuard? Guard = null,
    InputCommandAcknowledgement? Acknowledgement = null,
    bool RequireAcknowledgement = false,
    TaskCompletionSource<bool>? Completion = null,
    long Generation = 0,
    long ForegroundGeneration = 0,
    Profile? ExpectedProfile = null,
    string? ExpectedExecutable = null,
    long Token = 0,
    bool RequirePreviousCommandSuccess = false,
    long TapPairToken = 0,
    bool RequireTapPairToken = false,
    long CreatedTick = 0,
    InputHoldOwner HoldOwner = InputHoldOwner.None,
    MouseButton? Button = null,
    int X = 0,
    int Y = 0,
    int WheelDelta = 0,
    bool HorizontalWheel = false,
    bool RequireAllReleased = false);

/// <summary>
/// Single-consumer FIFO for synthetic key input. Producers may hold a feature lock while enqueueing;
/// the worker never takes feature locks, and guards must remain safe for lock-free worker reads.
/// </summary>
internal sealed class InputExecutor : IInputQueue, IDisposable
{
    private const int DEFAULT_DRAIN_TIMEOUT_MS = 2000;
    private const int MAX_WHEEL_TAP_AGE_MS = 250;
    private const int WHEEL_KEY_UP_GAP_MS = 10;

    private readonly InputRuntimeState _runtime;
    private readonly IInputSender _inputSender;
    private readonly ILoggerService _logger;
    private readonly Func<long> _clock;
    private readonly Func<int, bool> _keyState;
    private readonly IMacroInputContext? _macroInputContext;
    private readonly bool[] _keysDown = new bool[256];
    private readonly Key[] _pendingReleases = new Key[256];
    private readonly InputHoldOwner[] _pendingReleaseOwners = new InputHoldOwner[256];
    private readonly InputHoldOwner[] _holdOwners = new InputHoldOwner[256];
    private readonly InputHoldOwner[] _rejectedHoldOwners = new InputHoldOwner[256];
    private readonly int[] _macroKeysOwned = new int[256];
    private readonly bool[] _mouseDown = new bool[6];
    private readonly bool[] _pendingMouseReleases = new bool[6];
    private readonly InputHoldOwner[] _mouseHoldOwners = new InputHoldOwner[6];
    private readonly InputHoldOwner[] _pendingMouseReleaseOwners = new InputHoldOwner[6];
    private readonly int[] _macroMouseOwned = new int[6];
    private long _macroReservationToken;
    private bool _macroCleanupRequested;
    private readonly object _enqueueLock = new();
    private BlockingCollection<InputCommand>? _queue;
    private Thread? _worker;
    private bool _disposed;

    internal InputExecutor(
        InputRuntimeState runtime,
        IInputSender inputSender,
        ILoggerService logger,
        Func<long>? clock = null,
        Func<int, bool>? keyState = null,
        IMacroInputContext? macroInputContext = null)
    {
        _runtime = runtime;
        _inputSender = inputSender;
        _logger = logger;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _keyState = keyState ?? (static virtualKey => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0);
        _macroInputContext = macroInputContext;
    }

    internal bool IsWorkerAlive => _worker?.IsAlive == true;

    internal bool IsStarted => _queue is not null;

    internal bool IsMacroSessionReserved(long token) => token != 0 && Volatile.Read(ref _macroReservationToken) == token;

    // Includes the native DOWN in flight, so the hook can give a fresh physical pair priority.
    internal bool IsMacroKeyOwned(int virtualKey) =>
        virtualKey > 0 && virtualKey < _macroKeysOwned.Length && Volatile.Read(ref _macroKeysOwned[virtualKey]) != 0;

    internal bool IsMacroMouseOwned(MouseButton button) =>
        (int)button > 0 && (int)button < _macroMouseOwned.Length && Volatile.Read(ref _macroMouseOwned[(int)button]) != 0;

    internal void Start(string threadName = "InputExecutor")
    {
        lock (_enqueueLock)
        {
            ObjectDisposedException.ThrowIf(_disposed || _runtime.IsDisposed, this);

            if (_worker?.IsAlive == true)
            {
                throw new InvalidOperationException("The previous input executor is still draining.");
            }

            _queue?.Dispose();
            _queue = new BlockingCollection<InputCommand>();
            _worker = new Thread(Drain)
            {
                IsBackground = true,
                Name = threadName
            };
            _worker.Start();
        }
    }

    public bool Enqueue(in InputCommand command)
    {
        lock (_enqueueLock)
        {
            return EnqueueLocked(command);
        }
    }

    public bool EnqueuePair(in InputCommand down, in InputCommand up)
    {
        lock (_enqueueLock)
        {
            var pairedUp = PreparePairedUp(in down, in up);
            return EnqueueLocked(down) && EnqueueLocked(pairedUp);
        }
    }

    internal static InputCommand PreparePairedUp(
        in InputCommand down,
        in InputCommand up)
    {
        if (down.Guard is null)
        {
            return up;
        }

        if (down.Acknowledgement is { } acknowledgement)
        {
            return up with
            {
                Acknowledgement = acknowledgement,
                RequireAcknowledgement = true,
                RequirePreviousCommandSuccess = false
            };
        }

        return up with
        {
            Acknowledgement = null,
            RequireAcknowledgement = false,
            RequirePreviousCommandSuccess = true
        };
    }

    internal bool StopAndDrain(int timeoutMilliseconds = DEFAULT_DRAIN_TIMEOUT_MS)
    {
        Thread? worker;
        lock (_enqueueLock)
        {
            _queue?.CompleteAdding();
            worker = _worker;
        }

        var drained = worker is null || worker.Join(timeoutMilliseconds);
        if (drained)
        {
            DisposeCompletedQueue();
        }

        return drained;
    }

    internal bool StopAndDrain(Action enqueueReleases, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(enqueueReleases);

        // Lifecycle serialization belongs to InputHookService. Keep the queue open until every
        // recorded release has been offered, then close it atomically against ordinary producers.
        enqueueReleases();
        var timeoutMilliseconds = timeout == Timeout.InfiniteTimeSpan
            ? Timeout.Infinite
            : (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        return StopAndDrain(timeoutMilliseconds);
    }

    internal bool DisposeCompletedQueue()
    {
        lock (_enqueueLock)
        {
            if (_worker?.IsAlive == true)
            {
                return false;
            }

            _queue?.Dispose();
            _queue = null;
            _worker = null;
            return true;
        }
    }

    public void Dispose()
    {
        Thread? worker;
        bool shouldWait;
        lock (_enqueueLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            shouldWait = _queue?.IsAddingCompleted == false;
            _queue?.CompleteAdding();
            worker = _worker;
        }

        // StopAndDrain already spent the service's single bounded wait. If it timed out, retain
        // the live worker and its queue; the terminal runtime guard prevents new DOWN work.
        if (worker is null || !worker.IsAlive ||
            (shouldWait && worker.Join(DEFAULT_DRAIN_TIMEOUT_MS)))
        {
            DisposeCompletedQueue();
        }
    }

    private bool EnqueueLocked(in InputCommand command)
    {
        var queue = _queue;
        var startsInput = command.IsDown || command.Kind is
            InputCommandKind.KeyTap or InputCommandKind.DummyKey or InputCommandKind.Sequence or
            InputCommandKind.WheelTap or InputCommandKind.MoveTo or InputCommandKind.MouseWheel or
            InputCommandKind.MacroReserveModifiers;
        if (_disposed || queue is null || queue.IsAddingCompleted ||
            (_runtime.IsDisposed && startsInput && !IsCompensatingTapCandidate(in command)) ||
            (_runtime.RecordingPaused && startsInput && command.HoldOwner != InputHoldOwner.Macro &&
             command.Kind != InputCommandKind.MacroReserveModifiers && !IsCompensatingTapCandidate(in command)))
        {
            command.Completion?.TrySetResult(false);
            return false;
        }

        try
        {
            queue.Add(command);
            return true;
        }
        catch (InvalidOperationException)
        {
            command.Completion?.TrySetResult(false);
            return false;
        }
    }

    private void Drain()
    {
        var queue = _queue;
        if (queue is null)
        {
            return;
        }

        var previousCommandSucceeded = false;
        long acknowledgedTapPairToken = 0;

        try
        {
            foreach (var command in queue.GetConsumingEnumerable())
            {
                try
                {
                    RetryPendingReleases();
                    previousCommandSucceeded = Execute(
                        queue,
                        in command,
                        previousCommandSucceeded,
                        ref acknowledgedTapPairToken);
                }
                catch (Exception ex)
                {
                    previousCommandSucceeded = false;
                    command.Completion?.TrySetResult(false);
                    _logger.Log($"Input executor error: {ex.Message}");
                }
                finally
                {
                    try
                    {
                        command.Guard?.OnCompleted(in command);
                    }
                    catch (Exception ex)
                    {
                        if (_logger.IsEnabled)
                        {
                            _logger.Log($"Input executor completion error: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The queue is disposed only after the worker exits; retained for defensive shutdown races.
        }
        finally
        {
            RetryPendingReleases();
        }
    }

    private bool Execute(
        BlockingCollection<InputCommand> queue,
        in InputCommand command,
        bool previousCommandSucceeded,
        ref long acknowledgedTapPairToken)
    {
        if (command.RequirePreviousCommandSuccess && !previousCommandSucceeded)
        {
            command.Completion?.TrySetResult(false);
            return false;
        }

        switch (command.Kind)
        {
            case InputCommandKind.Sequence:
                return ExecuteSequence(queue, in command);
            case InputCommandKind.DummyKey:
                return ExecuteDummy(queue, in command);
            case InputCommandKind.KeyTap:
                return ExecuteTap(queue, in command, ref acknowledgedTapPairToken);
            case InputCommandKind.WheelTap:
                return ExecuteWheelTap(queue, in command);
            case InputCommandKind.MacroReserveModifiers:
                return ReserveMacroModifiers(queue, in command);
            case InputCommandKind.MacroCleanup:
                return CleanupMacro(in command);
            case InputCommandKind.MouseButtonTransition:
                return ExecuteMouseTransition(queue, in command);
            case InputCommandKind.MoveTo:
            case InputCommandKind.MouseWheel:
                return ExecuteMouseOutput(queue, in command);
        }

        return ExecuteTransition(queue, in command);
    }

    private bool ExecuteSequence(BlockingCollection<InputCommand> queue, in InputCommand command)
    {
        if (command.Sequence is not { } steps)
        {
            return false;
        }

        var sentAny = false;
        var releasesSucceeded = true;

        foreach (var step in steps)
        {
            if (queue.IsAddingCompleted || _runtime.IsDisposed || _runtime.RecordingPaused || !GuardAllows(in command))
            {
                break;
            }

            if (IsBusyForTap(step.Key)) continue;

            var downSent = false;
            var downAttempted = false;
            try
            {
                downSent = SendKey(step.Key, true, out downAttempted);
                sentAny |= downSent;
                Thread.Sleep(step.DownMs);
            }
            finally
            {
                if (downAttempted) releasesSucceeded &= SendKey(step.Key, false);
            }

            Thread.Sleep(step.GapMs);
        }

        command.Completion?.TrySetResult(sentAny && releasesSucceeded);
        return sentAny && releasesSucceeded;
    }

    private bool ExecuteDummy(BlockingCollection<InputCommand> queue, in InputCommand command)
    {
        if (queue.IsAddingCompleted || _runtime.IsDisposed || _runtime.RecordingPaused || !GuardAllows(in command))
        {
            command.Completion?.TrySetResult(false);
            return false;
        }

        // Short-count and skip diagnostics live at the SendInput boundary (WindowsInputSender).
        var sent = _inputSender.SendDummyKey();
        command.Completion?.TrySetResult(true);
        return sent;
    }

    private bool ExecuteTap(
        BlockingCollection<InputCommand> queue,
        in InputCommand command,
        ref long acknowledgedTapPairToken)
    {
        var tapPairAcknowledged = !command.RequireTapPairToken ||
            (command.TapPairToken != 0 &&
             command.TapPairToken == acknowledgedTapPairToken);
        var acknowledgedCompensation = command.RequireTapPairToken && tapPairAcknowledged;
        if (((queue.IsAddingCompleted || _runtime.IsDisposed || _runtime.RecordingPaused) &&
             !acknowledgedCompensation) || !GuardAllows(in command) ||
            (command.RequireAcknowledgement && command.Acknowledgement?.DownSent != true) ||
            !tapPairAcknowledged || IsBusyForTap(command.Key))
        {
            command.Completion?.TrySetResult(false);
            return false;
        }

        var downSent = SendKey(command.Key, true, out var downAttempted,
            acknowledgedCompensation: acknowledgedCompensation);
        var upSent = false;
        try
        {
            Thread.Sleep(command.DelayBeforeMs);
        }
        finally
        {
            if (downAttempted) upSent = SendKey(command.Key, false);
        }

        if (downSent)
        {
            command.Acknowledgement?.MarkDownSent();
        }
        if (command.TapPairToken != 0)
        {
            acknowledgedTapPairToken = command.RequireTapPairToken
                ? 0
                : downSent ? command.TapPairToken : 0;
        }
        command.Completion?.TrySetResult(downSent && upSent);
        return downSent && upSent;
    }

    private bool ExecuteWheelTap(BlockingCollection<InputCommand> queue, in InputCommand command)
    {
        var virtualKey = KeyInteropUtilities.ToVirtualKey(command.Key);
        if (!CanStartWheelTap(queue, in command) ||
            virtualKey == 0 || virtualKey >= _keysDown.Length ||
            _keysDown[virtualKey] || _keyState(virtualKey) ||
            !CanStartWheelTap(queue, in command) ||
            !SendKey(command.Key, true))
        {
            command.Completion?.TrySetResult(false);
            return false;
        }

        var upSent = false;
        try
        {
            Thread.Sleep(command.DelayBeforeMs);
        }
        finally
        {
            // Successful DOWN owns its UP even if the profile, guard or queue changed meanwhile.
            try
            {
                upSent = SendKey(command.Key, false);
            }
            finally
            {
                Thread.Sleep(WHEEL_KEY_UP_GAP_MS);
            }
        }

        command.Completion?.TrySetResult(upSent);
        return upSent;
    }

    private bool CanStartWheelTap(BlockingCollection<InputCommand> queue, in InputCommand command) =>
        !queue.IsAddingCompleted && !_runtime.IsDisposed && _runtime.IsRunning && !_runtime.RecordingPaused &&
        Stopwatch.GetElapsedTime(command.CreatedTick, _clock()).TotalMilliseconds <= MAX_WHEEL_TAP_AGE_MS &&
        GuardAllows(in command);

    private bool ExecuteTransition(BlockingCollection<InputCommand> queue, in InputCommand command)
    {
        if (!command.IsDown && command.RequireAcknowledgement && command.Acknowledgement?.DownSent != true)
        {
            command.Completion?.TrySetResult(false);
            return false;
        }

        if (command.DelayBeforeMs > 0)
        {
            Thread.Sleep(command.DelayBeforeMs);
        }

        if (command.IsDown &&
            (RejectForeignMacroDown(command.Key, command.HoldOwner) ||
             queue.IsAddingCompleted || _runtime.IsDisposed || !GuardAllows(in command) ||
             (_runtime.RecordingPaused && command.HoldOwner != InputHoldOwner.Macro) ||
             (command.HoldOwner == InputHoldOwner.Macro && !CanStartMacroOutput(queue, in command))))
        {
            command.Completion?.TrySetResult(false);
            return false;
        }

        // A retired producer cannot release an input belonging to a later run with the same key.
        Func<bool>? canSend = null;
        if (command.HoldOwner == InputHoldOwner.Macro && command.IsDown)
        {
            var attempt = command;
            canSend = () => CanStartMacroOutput(queue, attempt);
        }
        var sent = command.HoldOwner == InputHoldOwner.Macro && command.Token != _macroReservationToken
            ? !command.IsDown
            : SendTransition(command.Key, command.IsDown, command.HoldOwner, canSend);
        if (command.IsDown && sent)
        {
            command.Acknowledgement?.MarkDownSent();
        }
        command.Completion?.TrySetResult(sent);
        return sent;
    }

    private bool GuardAllows(in InputCommand command) =>
        command.Guard?.CanExecute(in command) != false &&
        (command.ExpectedProfile is not { } profile ||
         (_runtime.LiveForegroundMatches(profile, command.ForegroundGeneration) &&
          command.Guard?.CanExecute(in command) != false &&
          _runtime.ProfileInputGenerationIsCurrent(profile, command.ForegroundGeneration)));

    private bool CanStartMacroOutput(BlockingCollection<InputCommand> queue, in InputCommand command)
    {
        if (queue.IsAddingCompleted || _runtime.IsDisposed || !_runtime.IsRunning ||
            _macroInputContext is null || _macroCleanupRequested || command.Token == 0 ||
            command.Token != _macroReservationToken || !GuardAllows(in command)) return false;
        if (_macroInputContext.PhysicalModifiersDown)
        {
            command.Acknowledgement?.DeferForPhysicalModifiers();
            return false;
        }
        if (command.Kind == InputCommandKind.KeyTransition && command.IsDown)
            return !_macroInputContext.IsPhysicalKeyDown(KeyInteropUtilities.ToVirtualKey(command.Key));
        if (command.Kind == InputCommandKind.MouseButtonTransition && command.IsDown)
            return command.Button is { } button && !_macroInputContext.IsPhysicalMouseButtonDown(button);
        if (command.Kind == InputCommandKind.MoveTo)
        {
            for (var index = 1; index < _mouseDown.Length; index++)
                if (_macroInputContext.IsPhysicalMouseButtonDown((MouseButton)index)) return false;
        }
        return true;
    }

    private bool ReserveMacroModifiers(BlockingCollection<InputCommand> queue, in InputCommand command)
    {
        var allowed = !queue.IsAddingCompleted && !_runtime.IsDisposed && _runtime.IsRunning &&
            _macroInputContext is not null && command.Token != 0 && GuardAllows(in command) &&
            _macroReservationToken == 0;
        for (var virtualKey = 1; allowed && virtualKey < _keysDown.Length; virtualKey++)
        {
            if (!command.RequireAllReleased && !IsModifier(virtualKey)) continue;
            allowed = !_keysDown[virtualKey] && _holdOwners[virtualKey] == InputHoldOwner.None &&
                _pendingReleases[virtualKey] == Key.None;
        }
        if (command.RequireAllReleased)
        {
            for (var index = 1; allowed && index < _mouseDown.Length; index++)
            {
                allowed = !_mouseDown[index] && _mouseHoldOwners[index] == InputHoldOwner.None &&
                    !_pendingMouseReleases[index];
            }
        }
        if (allowed) Volatile.Write(ref _macroReservationToken, command.Token);
        command.Completion?.TrySetResult(allowed);
        return allowed;
    }

    private bool CleanupMacro(in InputCommand command)
    {
        if (command.Token != _macroReservationToken || command.Token == 0)
        {
            command.Completion?.TrySetResult(true);
            return true;
        }
        _macroCleanupRequested = true;

        for (var virtualKey = 1; virtualKey < _holdOwners.Length; virtualKey++)
        {
            if ((_holdOwners[virtualKey] & InputHoldOwner.Macro) == 0 &&
                _pendingReleaseOwners[virtualKey] != InputHoldOwner.Macro) continue;
            try
            {
                var key = _pendingReleases[virtualKey] != Key.None
                    ? _pendingReleases[virtualKey]
                    : KeyInteropUtilities.FromVirtualKey(virtualKey) ?? Key.None;
                SendTransition(key, false, InputHoldOwner.Macro);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled) _logger.Log($"Macro key cleanup error: {ex.Message}");
            }
        }
        for (var index = 1; index < _mouseHoldOwners.Length; index++)
        {
            if ((_mouseHoldOwners[index] & InputHoldOwner.Macro) == 0 &&
                _pendingMouseReleaseOwners[index] != InputHoldOwner.Macro) continue;
            try
            {
                SendMouseButton((MouseButton)index, false, InputHoldOwner.Macro);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled) _logger.Log($"Macro mouse cleanup error: {ex.Message}");
            }
        }

        var released = FinishMacroCleanupIfReleased();
        command.Completion?.TrySetResult(released);
        return released;
    }

    private bool FinishMacroCleanupIfReleased()
    {
        if (!_macroCleanupRequested) return false;
        for (var virtualKey = 1; virtualKey < _holdOwners.Length; virtualKey++)
            if ((_holdOwners[virtualKey] & InputHoldOwner.Macro) != 0 ||
                _pendingReleaseOwners[virtualKey] == InputHoldOwner.Macro) return false;
        for (var index = 1; index < _mouseHoldOwners.Length; index++)
            if ((_mouseHoldOwners[index] & InputHoldOwner.Macro) != 0 ||
                _pendingMouseReleaseOwners[index] == InputHoldOwner.Macro) return false;
        _macroCleanupRequested = false;
        Volatile.Write(ref _macroReservationToken, 0);
        return true;
    }

    private bool ExecuteMouseTransition(BlockingCollection<InputCommand> queue, in InputCommand command)
    {
        if (command.Button is not { } button || (int)button is < 1 or > 5 ||
            (!command.IsDown && command.RequireAcknowledgement && command.Acknowledgement?.DownSent != true) ||
            (command.IsDown &&
             (queue.IsAddingCompleted || _runtime.IsDisposed || !GuardAllows(in command) ||
              (_runtime.RecordingPaused && command.HoldOwner != InputHoldOwner.Macro) ||
              (command.HoldOwner == InputHoldOwner.Macro && !CanStartMacroOutput(queue, in command)))))
        {
            command.Completion?.TrySetResult(false);
            return false;
        }
        Func<bool>? canSend = null;
        if (command.HoldOwner == InputHoldOwner.Macro && command.IsDown)
        {
            var attempt = command;
            canSend = () => CanStartMacroOutput(queue, attempt);
        }
        var sent = command.HoldOwner == InputHoldOwner.Macro && command.Token != _macroReservationToken
            ? !command.IsDown
            : SendMouseButton(button, command.IsDown, command.HoldOwner, canSend);
        if (command.IsDown && sent) command.Acknowledgement?.MarkDownSent();
        command.Completion?.TrySetResult(sent);
        return sent;
    }

    private bool ExecuteMouseOutput(BlockingCollection<InputCommand> queue, in InputCommand command)
    {
        var allowed = command.HoldOwner == InputHoldOwner.Macro && CanStartMacroOutput(queue, in command);
        var attempt = command;
        bool CanSend() => CanStartMacroOutput(queue, attempt);
        var sent = allowed && (command.Kind == InputCommandKind.MoveTo
            ? _inputSender.MoveMouseTo(command.X, command.Y, CanSend)
            : command.WheelDelta is >= short.MinValue and <= short.MaxValue and not 0 &&
              _inputSender.SendMouseWheel(command.WheelDelta, command.HorizontalWheel, CanSend));
        command.Completion?.TrySetResult(sent);
        return sent;
    }

    private bool SendMouseButton(MouseButton button, bool isDown, InputHoldOwner owner, Func<bool>? canSend = null)
    {
        var index = (int)button;
        var owners = _mouseHoldOwners[index];
        if (isDown)
        {
            if ((_runtime.RecordingPaused && owner != InputHoldOwner.Macro) ||
                _mouseDown[index] || _pendingMouseReleases[index] || owners != InputHoldOwner.None ||
                (owner == InputHoldOwner.Macro && _macroInputContext?.IsPhysicalMouseButtonDown(button) != false))
                return false;
        }
        else if (owner == InputHoldOwner.Macro)
        {
            if ((owners & InputHoldOwner.Macro) == 0) return true;
            if (_macroInputContext?.HasPhysicalMouseTakeover(button) == true)
            {
                ForgetMacroMouse(index);
                return true;
            }
        }
        else
        {
            _mouseHoldOwners[index] &= ~owner;
            if (_mouseHoldOwners[index] != InputHoldOwner.None) return true;
        }

        if (!isDown)
        {
            _pendingMouseReleases[index] = true;
            _pendingMouseReleaseOwners[index] = owner;
        }
        if (isDown && owner == InputHoldOwner.Macro) Volatile.Write(ref _macroMouseOwned[index], 1);
        var sent = false;
        try
        {
            sent = _inputSender.SendMouseButton(button, isDown,
                macroRelease: !isDown && owner == InputHoldOwner.Macro, canSend: canSend);
        }
        finally
        {
            if (isDown && owner == InputHoldOwner.Macro && !sent) Volatile.Write(ref _macroMouseOwned[index], 0);
        }
        if (sent)
        {
            _mouseDown[index] = isDown;
            if (isDown) _mouseHoldOwners[index] = owner;
            else
            {
                _pendingMouseReleases[index] = false;
                _pendingMouseReleaseOwners[index] = InputHoldOwner.None;
                if (owner == InputHoldOwner.Macro) ForgetMacroMouse(index);
            }
        }
        return sent;
    }

    private void ForgetMacroKey(int virtualKey)
    {
        _holdOwners[virtualKey] &= ~InputHoldOwner.Macro;
        if (_pendingReleaseOwners[virtualKey] == InputHoldOwner.Macro)
        {
            _pendingReleases[virtualKey] = Key.None;
            _pendingReleaseOwners[virtualKey] = InputHoldOwner.None;
        }
        if (_holdOwners[virtualKey] == InputHoldOwner.None && _pendingReleases[virtualKey] == Key.None)
            _keysDown[virtualKey] = false;
        Volatile.Write(ref _macroKeysOwned[virtualKey], 0);
    }

    private void ForgetMacroMouse(int index)
    {
        _mouseHoldOwners[index] &= ~InputHoldOwner.Macro;
        if (_pendingMouseReleaseOwners[index] == InputHoldOwner.Macro)
        {
            _pendingMouseReleases[index] = false;
            _pendingMouseReleaseOwners[index] = InputHoldOwner.None;
        }
        if (_mouseHoldOwners[index] == InputHoldOwner.None && !_pendingMouseReleases[index]) _mouseDown[index] = false;
        Volatile.Write(ref _macroMouseOwned[index], 0);
    }

    private static bool IsModifier(int virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    private static bool IsCompensatingTapCandidate(in InputCommand command) =>
        command.Kind == InputCommandKind.KeyTap &&
        command.RequireTapPairToken &&
        command.TapPairToken != 0;

    private bool IsBusyForTap(Key key)
    {
        var virtualKey = KeyInteropUtilities.ToVirtualKey(key);
        return virtualKey > 0 && virtualKey < _keysDown.Length &&
            (_keysDown[virtualKey] || _pendingReleases[virtualKey] != Key.None ||
             (_macroReservationToken != 0 && IsModifier(virtualKey)));
    }

    private bool SendTransition(Key key, bool isDown, InputHoldOwner owner, Func<bool>? canSend = null)
    {
        var virtualKey = KeyInteropUtilities.ToVirtualKey(key);
        if (virtualKey <= 0 || virtualKey >= _holdOwners.Length)
            return owner != InputHoldOwner.Macro && SendKey(key, isDown, owner);

        var owners = _holdOwners[virtualKey];
        if (isDown)
        {
            if (owner == InputHoldOwner.Macro &&
                (_macroInputContext?.IsPhysicalKeyDown(virtualKey) != false ||
                 (owners & ~InputHoldOwner.Macro) != 0 ||
                 (_keysDown[virtualKey] && owners == InputHoldOwner.None))) return false;
            if (_pendingReleases[virtualKey] != Key.None ||
                (owner == InputHoldOwner.None && owners != InputHoldOwner.None)) return false;
            if (owners != InputHoldOwner.None && (owners & owner) == 0)
            {
                _holdOwners[virtualKey] = owners | owner;
                return true;
            }
            var sent = SendKey(key, true, owner, canSend);
            if (sent) _holdOwners[virtualKey] = owners | owner;
            return sent;
        }

        if (owner == InputHoldOwner.Macro)
        {
            if ((owners & InputHoldOwner.Macro) == 0) return true;
            if (_macroInputContext?.HasPhysicalKeyTakeover(virtualKey) == true)
            {
                ForgetMacroKey(virtualKey);
                return true;
            }
            // Macro ownership is retained on a failed/throwing UP until retry or takeover resolves it.
            if ((owners & ~InputHoldOwner.Macro) != 0)
            {
                ForgetMacroKey(virtualKey);
                return true;
            }
            return SendKey(key, false, owner);
        }
        // A rejected producer's physical source pair can outlive the macro reservation.
        // Shared Combined sources already collapse to one DOWN/final UP per target.
        var rejected = (_rejectedHoldOwners[virtualKey] & owner) != 0;
        _rejectedHoldOwners[virtualKey] &= ~owner;
        if ((rejected || _macroReservationToken != 0) && owner != InputHoldOwner.None &&
            (owners & owner) == 0 &&
            (_pendingReleases[virtualKey] == Key.None || _pendingReleaseOwners[virtualKey] != owner)) return true;
        _holdOwners[virtualKey] = owners & ~owner;
        return _holdOwners[virtualKey] != InputHoldOwner.None || SendKey(key, false, owner);
    }

    private bool RejectForeignMacroDown(Key key, InputHoldOwner owner)
    {
        var virtualKey = KeyInteropUtilities.ToVirtualKey(key);
        if (owner == InputHoldOwner.Macro || virtualKey <= 0 || virtualKey >= _holdOwners.Length ||
            (!IsMacroKeyOwned(virtualKey) && (_macroReservationToken == 0 || !IsModifier(virtualKey)))) return false;

        // Record before other guards can reject this DOWN; only the executor worker writes these bits.
        _rejectedHoldOwners[virtualKey] |= owner;
        return true;
    }

    private bool SendKey(Key key, bool isKeyDown, InputHoldOwner owner = InputHoldOwner.None, Func<bool>? canSend = null)
        => SendKey(key, isKeyDown, out _, owner, canSend);

    private bool SendKey(Key key, bool isKeyDown, out bool attempted,
        InputHoldOwner owner = InputHoldOwner.None, Func<bool>? canSend = null, bool acknowledgedCompensation = false)
    {
        attempted = false;
        var virtualKey = KeyInteropUtilities.ToVirtualKey(key);
        var tracked = virtualKey > 0 && virtualKey < _keysDown.Length;
        if (tracked)
        {
            // All key paths, including unowned taps/sequences, cross this final arbitration point.
            if (isKeyDown && ((_runtime.RecordingPaused && owner != InputHoldOwner.Macro && !acknowledgedCompensation) ||
                _pendingReleases[virtualKey] != Key.None ||
                (owner != InputHoldOwner.Macro &&
                 (IsMacroKeyOwned(virtualKey) || (_macroReservationToken != 0 && IsModifier(virtualKey))))))
            {
                return false;
            }
            if (!isKeyDown && owner == InputHoldOwner.Macro &&
                _macroInputContext?.HasPhysicalKeyTakeover(virtualKey) == true)
            {
                ForgetMacroKey(virtualKey);
                return true;
            }
            if (!isKeyDown)
            {
                // Retain the obligation even if the sender throws or the OS key-state query says UP.
                _pendingReleases[virtualKey] = key;
                _pendingReleaseOwners[virtualKey] = owner;
            }
        }
        // IInputSender exposes a bare bool: a false can be a vk-mapping skip that never reached
        // SendInput, and any last-error read here would be stale. The failure facts exist only at
        // the sender boundary (WindowsInputSender), which logs them.
        var sent = false;
        if (tracked && isKeyDown && owner == InputHoldOwner.Macro)
            Volatile.Write(ref _macroKeysOwned[virtualKey], 1);
        try
        {
            attempted = true;
            // A later native attempt supersedes rejection, including legacy failed-DOWN cleanup.
            if (tracked && isKeyDown) _rejectedHoldOwners[virtualKey] &= ~owner;
            sent = _inputSender.SendKey(key, isKeyDown,
                macroRelease: !isKeyDown && owner == InputHoldOwner.Macro, canSend: canSend);
        }
        finally
        {
            if (tracked && isKeyDown && owner == InputHoldOwner.Macro && !sent &&
                (_holdOwners[virtualKey] & InputHoldOwner.Macro) == 0)
                Volatile.Write(ref _macroKeysOwned[virtualKey], 0);
        }
        if (sent)
        {
            if (tracked)
            {
                _keysDown[virtualKey] = isKeyDown;
                if (!isKeyDown)
                {
                    _pendingReleases[virtualKey] = Key.None;
                    _pendingReleaseOwners[virtualKey] = InputHoldOwner.None;
                    if (owner == InputHoldOwner.Macro) ForgetMacroKey(virtualKey);
                }
            }
        }

        return sent;
    }

    private void RetryPendingReleases()
    {
        // One attempt per pending key at each worker/lifecycle opportunity; no retry loop or timer.
        for (var virtualKey = 1; virtualKey < _pendingReleases.Length; virtualKey++)
        {
            var key = _pendingReleases[virtualKey];
            if (key == Key.None) continue;
            try
            {
                SendKey(key, false, _pendingReleaseOwners[virtualKey]);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled) _logger.Log($"Input release retry error: {ex.Message}");
            }
        }
        for (var index = 1; index < _pendingMouseReleases.Length; index++)
        {
            if (!_pendingMouseReleases[index]) continue;
            try
            {
                SendMouseButton((MouseButton)index, false, _pendingMouseReleaseOwners[index]);
            }
            catch (Exception ex)
            {
                if (_logger.IsEnabled) _logger.Log($"Input mouse release retry error: {ex.Message}");
            }
        }
        // The last lifecycle retry can succeed after queue closure, when a new fence cannot be queued.
        FinishMacroCleanupIfReleased();
    }
}
