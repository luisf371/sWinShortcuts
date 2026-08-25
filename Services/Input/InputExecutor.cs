using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services.Input;

internal interface IInputQueue
{
    bool Enqueue(in InputCommand command);

    bool EnqueuePair(in InputCommand down, in InputCommand up);
}

internal interface IInputCommandGuard
{
    bool CanExecute(in InputCommand command);
}

internal enum InputCommandKind
{
    KeyTransition,
    KeyTap,
    DummyKey,
    Sequence
}

internal readonly record struct TapStep(Key Key, int DownMs, int GapMs);

internal class InputCommandAcknowledgement
{
    private int _downSent;
    private int _cancelled;

    internal bool DownSent => Volatile.Read(ref _downSent) != 0;

    internal bool IsCancelled => Volatile.Read(ref _cancelled) != 0;

    internal void MarkDownSent() => Volatile.Write(ref _downSent, 1);

    internal void Cancel() => Volatile.Write(ref _cancelled, 1);
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
    long Token = 0);

/// <summary>
/// Single-consumer FIFO for synthetic key input. Producers may hold a feature lock while enqueueing;
/// the worker never takes feature locks, and guards must remain safe for lock-free worker reads.
/// </summary>
internal sealed class InputExecutor : IInputQueue, IDisposable
{
    private const int DEFAULT_DRAIN_TIMEOUT_MS = 2000;

    private readonly InputRuntimeState _runtime;
    private readonly IInputSender _inputSender;
    private readonly ILoggerService _logger;
    private readonly object _enqueueLock = new();
    private BlockingCollection<InputCommand>? _queue;
    private Thread? _worker;
    private bool _disposed;

    internal InputExecutor(
        InputRuntimeState runtime,
        IInputSender inputSender,
        ILoggerService logger)
    {
        _runtime = runtime;
        _inputSender = inputSender;
        _logger = logger;
    }

    internal bool IsWorkerAlive => _worker?.IsAlive == true;

    internal bool IsStarted => _queue is not null;

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
            var pairedDown = down;
            var pairedUp = up;
            if (down.Guard is not null)
            {
                var acknowledgement = down.Acknowledgement ?? new InputCommandAcknowledgement();
                pairedDown = down with { Acknowledgement = acknowledgement };
                pairedUp = up with
                {
                    Acknowledgement = acknowledgement,
                    RequireAcknowledgement = true
                };
            }

            return EnqueueLocked(pairedDown) && EnqueueLocked(pairedUp);
        }
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
            InputCommandKind.KeyTap or InputCommandKind.DummyKey or InputCommandKind.Sequence;
        if (_disposed || queue is null || queue.IsAddingCompleted ||
            (_runtime.IsDisposed && startsInput))
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

        try
        {
            foreach (var command in queue.GetConsumingEnumerable())
            {
                try
                {
                    Execute(queue, in command);
                }
                catch (Exception ex)
                {
                    command.Completion?.TrySetResult(false);
                    _logger.Log($"Input executor error: {ex.Message}");
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The queue is disposed only after the worker exits; retained for defensive shutdown races.
        }
    }

    private void Execute(BlockingCollection<InputCommand> queue, in InputCommand command)
    {
        switch (command.Kind)
        {
            case InputCommandKind.Sequence:
                ExecuteSequence(queue, in command);
                return;
            case InputCommandKind.DummyKey:
                ExecuteDummy(queue, in command);
                return;
            case InputCommandKind.KeyTap:
                ExecuteTap(queue, in command);
                return;
        }

        ExecuteTransition(queue, in command);
    }

    private void ExecuteSequence(BlockingCollection<InputCommand> queue, in InputCommand command)
    {
        if (command.Sequence is not { } steps)
        {
            return;
        }

        foreach (var step in steps)
        {
            if (queue.IsAddingCompleted || _runtime.IsDisposed || !GuardAllows(in command))
            {
                break;
            }

            try
            {
                SendKey(step.Key, true);
                Thread.Sleep(step.DownMs);
            }
            finally
            {
                SendKey(step.Key, false);
            }

            Thread.Sleep(step.GapMs);
        }
    }

    private void ExecuteDummy(BlockingCollection<InputCommand> queue, in InputCommand command)
    {
        if (queue.IsAddingCompleted || _runtime.IsDisposed || !GuardAllows(in command))
        {
            command.Completion?.TrySetResult(false);
            return;
        }

        var sent = _inputSender.SendDummyKey();
        if (!sent && _logger.IsEnabled)
        {
            _logger.Log("WindowsLauncher dummy key injection failed");
        }
        command.Completion?.TrySetResult(true);
    }

    private void ExecuteTap(BlockingCollection<InputCommand> queue, in InputCommand command)
    {
        if (queue.IsAddingCompleted || _runtime.IsDisposed || !GuardAllows(in command) ||
            (command.RequireAcknowledgement && command.Acknowledgement?.DownSent != true))
        {
            command.Completion?.TrySetResult(false);
            return;
        }

        var downSent = SendKey(command.Key, true);
        try
        {
            Thread.Sleep(command.DelayBeforeMs);
        }
        finally
        {
            SendKey(command.Key, false);
        }

        if (downSent)
        {
            command.Acknowledgement?.MarkDownSent();
        }
        command.Completion?.TrySetResult(downSent);
    }

    private void ExecuteTransition(BlockingCollection<InputCommand> queue, in InputCommand command)
    {
        if (command.IsDown)
        {
            if (queue.IsAddingCompleted || _runtime.IsDisposed || !GuardAllows(in command))
            {
                command.Completion?.TrySetResult(false);
                return;
            }
        }
        else if (command.RequireAcknowledgement && command.Acknowledgement?.DownSent != true)
        {
            command.Completion?.TrySetResult(false);
            return;
        }

        if (command.DelayBeforeMs > 0)
        {
            Thread.Sleep(command.DelayBeforeMs);
        }

        var sent = SendKey(command.Key, command.IsDown);
        if (command.IsDown && sent)
        {
            command.Acknowledgement?.MarkDownSent();
        }
        command.Completion?.TrySetResult(sent);
    }

    private static bool GuardAllows(in InputCommand command) =>
        command.Guard?.CanExecute(in command) != false;

    private bool SendKey(Key key, bool isKeyDown)
    {
        var sent = _inputSender.SendKey(key, isKeyDown);
        if (!sent && _logger.IsEnabled)
        {
            _logger.Log($"SendKey FAILED: {key} ({(isKeyDown ? "DOWN" : "UP")})");
        }

        return sent;
    }
}
