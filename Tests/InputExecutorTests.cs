using System.Windows.Input;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class InputExecutorTests
{
    [Fact]
    public async Task EnqueuePair_DrainsPairsInFifoOrder()
    {
        var runtime = RunningRuntime();
        var sender = new RecordingInputSender();
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        executor.Start();

        Assert.True(executor.EnqueuePair(
            new InputCommand(Key.A, IsDown: true),
            new InputCommand(Key.A, IsDown: false)));
        Assert.True(executor.EnqueuePair(
            new InputCommand(Key.B, IsDown: true),
            new InputCommand(Key.B, IsDown: false)));
        Assert.True(await EnqueueFence(executor));

        Assert.Equal(
            new[] { (Key.A, true), (Key.A, false), (Key.B, true), (Key.B, false) },
            sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        runtime.SetRunning(false);
        Assert.True(executor.StopAndDrain());
    }

    [Fact]
    public async Task EnqueuePair_ConcurrentProducersKeepPairsAdjacent()
    {
        var runtime = RunningRuntime();
        var sender = new RecordingInputSender();
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        executor.Start();

        var enqueued = await Task.WhenAll(new[] { Key.A, Key.B, Key.C, Key.D }.Select(key =>
            Task.Run(() => executor.EnqueuePair(
                new InputCommand(key, IsDown: true),
                new InputCommand(key, IsDown: false)))));
        Assert.All(enqueued, value => Assert.True(value));
        Assert.True(await EnqueueFence(executor));

        var transitions = sender.Transitions.ToArray();
        Assert.Equal(8, transitions.Length);
        for (var i = 0; i < transitions.Length; i += 2)
        {
            Assert.True(transitions[i].IsDown);
            Assert.False(transitions[i + 1].IsDown);
            Assert.Equal(transitions[i].Key, transitions[i + 1].Key);
        }

        runtime.SetRunning(false);
        Assert.True(executor.StopAndDrain());
    }

    [Fact]
    public async Task GuardedDown_StaleCommandSkipsDownButUnconditionalUpDrains()
    {
        var runtime = RunningRuntime();
        var sender = new RecordingInputSender(blockDummy: true);
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        executor.Start();
        var blocker = EnqueueFence(executor);

        try
        {
            Assert.True(sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
            var guard = new MutableGuard(true);
            Assert.True(executor.Enqueue(new InputCommand(Key.A, IsDown: true, Guard: guard)));
            Assert.True(executor.Enqueue(new InputCommand(Key.A, IsDown: false, Guard: guard)));

            guard.Allows = false;
            sender.ReleaseDummy.Set();
            Assert.True(await blocker);
            Assert.True(await EnqueueFence(executor));

            Assert.Equal(new[] { (Key.A, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        }
        finally
        {
            sender.ReleaseDummy.Set();
            runtime.SetRunning(false);
            executor.StopAndDrain();
        }
    }

    [Fact]
    public async Task FailedDown_DoesNotStopLaterCommands()
    {
        var runtime = RunningRuntime();
        var sender = new RecordingInputSender(failFirstDown: true);
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        executor.Start();
        var failedDown = Completion();

        Assert.True(executor.Enqueue(new InputCommand(Key.A, IsDown: true, Completion: failedDown)));
        Assert.True(executor.Enqueue(new InputCommand(Key.B, IsDown: true)));
        Assert.True(executor.Enqueue(new InputCommand(Key.B, IsDown: false)));

        Assert.False(await failedDown.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await EnqueueFence(executor));
        Assert.Equal(
            new[] { (Key.A, true), (Key.B, true), (Key.B, false) },
            sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());

        runtime.SetRunning(false);
        Assert.True(executor.StopAndDrain());
    }

    [Fact]
    public async Task EnqueuePair_FailedGuardedDown_SkipsAcknowledgedUp()
    {
        var runtime = RunningRuntime();
        var sender = new RecordingInputSender(failFirstDown: true);
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        executor.Start();
        var guard = new MutableGuard(true);

        Assert.True(executor.EnqueuePair(
            new InputCommand(Key.A, IsDown: true, Guard: guard),
            new InputCommand(Key.A, IsDown: false)));
        Assert.True(await EnqueueFence(executor));

        Assert.Equal(new[] { (Key.A, true) },
            sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        runtime.SetRunning(false);
        Assert.True(executor.StopAndDrain());
    }

    [Fact]
    public async Task KeyTap_CompletesAtomicallyBeforeFollowingTransition()
    {
        var runtime = RunningRuntime();
        var sender = new RecordingInputSender();
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        executor.Start();

        Assert.True(executor.Enqueue(new InputCommand(
            Key.A,
            IsDown: true,
            Kind: InputCommandKind.KeyTap)));
        Assert.True(executor.Enqueue(new InputCommand(Key.B, IsDown: true)));
        Assert.True(executor.Enqueue(new InputCommand(Key.B, IsDown: false)));
        Assert.True(await EnqueueFence(executor));

        Assert.Equal(
            new[] { (Key.A, true), (Key.A, false), (Key.B, true), (Key.B, false) },
            sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        runtime.SetRunning(false);
        Assert.True(executor.StopAndDrain());
    }

    [Fact]
    public void StopAndDrain_StoppedRuntimeStillEnqueuesReleaseBeforeCompletingQueue()
    {
        var runtime = RunningRuntime();
        var sender = new RecordingInputSender();
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        executor.Start();
        runtime.SetRunning(false);

        Assert.True(executor.StopAndDrain(
            () =>
            {
                Assert.False(runtime.IsRunning);
                Assert.True(executor.Enqueue(new InputCommand(Key.A, IsDown: false)));
            },
            TimeSpan.FromSeconds(2)));

        Assert.Equal(new[] { (Key.A, false) },
            sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        Assert.False(executor.Enqueue(new InputCommand(Key.B, IsDown: false)));
    }

    [Fact]
    public async Task TimedOutDrain_RefusesRestartUntilWorkerExitsThenAllowsRetry()
    {
        var runtime = RunningRuntime();
        var sender = new RecordingInputSender(blockDummy: true);
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        executor.Start();
        var blocker = EnqueueFence(executor);

        try
        {
            Assert.True(sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(executor.StopAndDrain(timeoutMilliseconds: 1));
            Assert.Throws<InvalidOperationException>(() => executor.Start());

            sender.ReleaseDummy.Set();
            Assert.True(await blocker);
            Assert.True(SpinWait.SpinUntil(() => !executor.IsWorkerAlive, TimeSpan.FromSeconds(2)));

            executor.Start();
            Assert.True(await EnqueueFence(executor));
            Assert.True(executor.StopAndDrain());
        }
        finally
        {
            sender.ReleaseDummy.Set();
            executor.StopAndDrain();
        }
    }

    [Fact]
    public void DisposePublishedAfterTapDown_StillSendsFinallyUp()
    {
        var runtime = RunningRuntime();
        var sender = new RecordingInputSender(blockFirstDown: true);
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        executor.Start();

        try
        {
            Assert.True(executor.Enqueue(new InputCommand(
                Key.A,
                IsDown: true,
                Kind: InputCommandKind.KeyTap)));
            Assert.True(sender.DownEntered.Wait(TimeSpan.FromSeconds(2)));

            Assert.True(runtime.TryBeginDispose());
            sender.ReleaseDown.Set();
            Assert.True(SpinWait.SpinUntil(() => sender.Transitions.Count == 2, TimeSpan.FromSeconds(2)));

            Assert.Equal(
                new[] { (Key.A, true), (Key.A, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
            Assert.True(executor.StopAndDrain());
        }
        finally
        {
            sender.ReleaseDown.Set();
            executor.StopAndDrain();
        }
    }

    private static InputRuntimeState RunningRuntime()
    {
        var runtime = new InputRuntimeState();
        runtime.SetRunning(true);
        return runtime;
    }

    private static TaskCompletionSource<bool> Completion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<bool> EnqueueFence(InputExecutor executor)
    {
        var completion = Completion();
        Assert.True(executor.Enqueue(new InputCommand(
            Key.None,
            IsDown: false,
            Kind: InputCommandKind.DummyKey,
            Completion: completion)));
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class MutableGuard(bool allows) : IInputCommandGuard
    {
        private volatile bool _allows = allows;

        internal bool Allows
        {
            set => _allows = value;
        }

        public bool CanExecute(in InputCommand command) => _allows;
    }
}
