using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Input;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class WheelInputExecutorTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task WheelTap_FailedRelease_RetriesWithoutUnrelatedSyntheticUp(bool restart, bool throws)
    {
        var upAttempts = 0;
        var sender = new ScriptedSender((_, down) => down || ++upAttempts > 1 ||
            (throws ? throw new InvalidOperationException("Release failed") : false));
        using var executor = new InputExecutor(RunningRuntime(), sender, new NullLoggerService(),
            clock: () => 0, keyState: _ => false);
        executor.Start();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(executor.Enqueue(Wheel() with { Completion = completion }));
        Assert.False(await completion.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        if (restart)
        {
            Assert.True(executor.StopAndDrain());
            executor.Start();
        }
        Assert.True(executor.Enqueue(Wheel()));
        await Fence(executor);
        Assert.Equal(new[] { (Key.A, true), (Key.A, false), (Key.A, false), (Key.A, true), (Key.A, false) },
            sender.Transitions);
        Assert.True(executor.StopAndDrain());
    }

    [Fact]
    public async Task WheelTap_PersistentReleaseFailure_RetryIsBoundedAndTargetStaysBusy()
    {
        var sender = new ScriptedSender((_, down) => down);
        using var executor = new InputExecutor(RunningRuntime(), sender, new NullLoggerService(),
            clock: () => 0, keyState: _ => false);
        executor.Start();
        Assert.True(executor.Enqueue(Wheel()));
        Assert.True(executor.Enqueue(Wheel()));
        await Fence(executor);
        Assert.True(executor.StopAndDrain());
        Assert.Single(sender.Transitions, item => item.IsDown);
        Assert.InRange(sender.Transitions.Count(item => !item.IsDown), 2, 5);
    }

    [Fact]
    public async Task FailedRelease_NewHolderWaitsForRecovery_ThenRetriesCannotReleaseIt()
    {
        var rejectUp = true;
        var sender = new ScriptedSender((_, down) => down || !rejectUp);
        using var executor = new InputExecutor(RunningRuntime(), sender, new NullLoggerService(),
            clock: () => 0, keyState: _ => false);
        executor.Start();
        Assert.True(executor.Enqueue(Wheel()));
        var hold = new InputCommand(Key.A, true, HoldOwner: InputHoldOwner.HoldBreath);
        Assert.True(executor.Enqueue(hold));
        await Fence(executor);
        Assert.Single(sender.Transitions, item => item.IsDown);
        rejectUp = false;
        Assert.True(executor.Enqueue(hold));
        await Fence(executor);
        var acceptedCount = sender.Transitions.Count;
        Assert.True(sender.Transitions.Last().IsDown);
        Assert.True(executor.Enqueue(Wheel()));
        await Fence(executor);
        Assert.Equal(acceptedCount, sender.Transitions.Count);
        Assert.True(executor.Enqueue(hold with { IsDown = false }));
        await Fence(executor);
        Assert.False(sender.Transitions.Last().IsDown);
        Assert.True(executor.StopAndDrain());
    }

    [Theory]
    [InlineData((int)InputCommandKind.KeyTap)]
    [InlineData((int)InputCommandKind.Sequence)]
    [InlineData((int)InputCommandKind.KeyTransition)]
    public async Task Tap_SharedHeldTarget_SkipsWithoutReleasingHolder(int kind)
    {
        var sender = new RecordingInputSender();
        using var executor = new InputExecutor(RunningRuntime(), sender, new NullLoggerService());
        executor.Start();
        var held = new InputCommand(Key.A, true, HoldOwner: InputHoldOwner.Caps);
        Assert.True(executor.Enqueue(held));
        var tap = new InputCommand(Key.A, true, Kind: (InputCommandKind)kind,
            Sequence: [new TapStep(Key.A, 0, 0)], Guard: new CompletionGuard());
        if (kind == (int)InputCommandKind.KeyTransition)
            Assert.True(executor.EnqueuePair(tap, new InputCommand(Key.A, false)));
        else Assert.True(executor.Enqueue(tap));
        await Fence(executor);
        Assert.Single(sender.Transitions, item => item.IsDown);
        Assert.DoesNotContain(sender.Transitions, item => !item.IsDown);
        Assert.True(executor.Enqueue(held with { IsDown = false }));
        await Fence(executor);
        Assert.Single(sender.Transitions, item => !item.IsDown);
        Assert.True(executor.StopAndDrain());
    }

    [Fact]
    public async Task Drain_RejectedAndSuccessfulCommands_CompleteEachGuardOnce()
    {
        var runtime = RunningRuntime();
        var sender = new RecordingInputSender();
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        var rejected = new CompletionGuard { Allows = false };
        var successful = new CompletionGuard();
        executor.Start();

        Assert.True(executor.Enqueue(new InputCommand(Key.A, IsDown: true, Guard: rejected)));
        Assert.True(executor.Enqueue(new InputCommand(Key.B, IsDown: true, Guard: successful)));
        await Fence(executor);

        Assert.Equal(1, rejected.Completions);
        Assert.Equal(1, successful.Completions);
        Assert.Equal(new[] { (Key.B, true) },
            sender.Transitions.Select(item => (item.Key, item.IsDown)));
        Assert.True(executor.StopAndDrain());
    }

    [Fact]
    public async Task Drain_SenderAndCompletionThrow_StillCompletesAndDrainsLaterCommands()
    {
        using var executor = new InputExecutor(RunningRuntime(),
            new ScriptedSender((_, _) => throw new InvalidOperationException("Sender failed")),
            new NullLoggerService());
        var throwing = new CompletionGuard { ThrowOnCompleted = true };
        var rejected = new CompletionGuard { Allows = false };
        executor.Start();

        Assert.True(executor.Enqueue(new InputCommand(Key.A, IsDown: true, Guard: throwing)));
        Assert.True(executor.Enqueue(new InputCommand(Key.B, IsDown: true, Guard: rejected)));
        await Fence(executor);

        Assert.Equal(1, throwing.Completions);
        Assert.Equal(1, rejected.Completions);
        Assert.True(executor.StopAndDrain());
    }

    [Theory]
    [InlineData(250, true)]
    [InlineData(251, false)]
    public async Task WheelTap_AgeLimit_DropsExpiredPulse(int ageMilliseconds, bool expectedPulse)
    {
        var sender = new RecordingInputSender();
        var guard = new CompletionGuard();
        using var executor = new InputExecutor(RunningRuntime(), sender, new NullLoggerService(),
            clock: () => Stopwatch.Frequency + ageMilliseconds * Stopwatch.Frequency / 1000,
            keyState: _ => false);
        executor.Start();

        Assert.True(executor.Enqueue(Wheel(guard) with { CreatedTick = Stopwatch.Frequency }));
        await Fence(executor);

        Assert.Equal(expectedPulse ? new[] { (Key.A, true), (Key.A, false) } : [],
            sender.Transitions.Select(item => (item.Key, item.IsDown)));
        Assert.Equal(1, guard.Completions);
        Assert.True(executor.StopAndDrain());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WheelTap_PhysicalTargetHeldOrQueryThrows_SkipsDownAndUp(bool throws)
    {
        var sender = new RecordingInputSender();
        var guard = new CompletionGuard();
        var queried = new ConcurrentQueue<(int VirtualKey, int ThreadId)>();
        using var executor = new InputExecutor(RunningRuntime(), sender, new NullLoggerService(),
            clock: () => 0, keyState: virtualKey =>
            {
                queried.Enqueue((virtualKey, Environment.CurrentManagedThreadId));
                return throws ? throw new InvalidOperationException("State unavailable") : true;
            });
        executor.Start();
        var enqueueThread = Environment.CurrentManagedThreadId;

        Assert.True(executor.Enqueue(Wheel(guard)));
        await Fence(executor);

        Assert.Empty(sender.Transitions);
        var query = Assert.Single(queried);
        Assert.Equal(0x41, query.VirtualKey);
        Assert.NotEqual(enqueueThread, query.ThreadId);
        Assert.Equal(1, guard.Completions);
        Assert.True(executor.StopAndDrain());
    }

    [Theory]
    [InlineData((int)InputCommandKind.KeyTransition)]
    [InlineData((int)InputCommandKind.KeyTap)]
    [InlineData((int)InputCommandKind.Sequence)]
    public async Task WheelTap_ExecutorHoldWithFailedUp_IsBusyUntilSuccessfulUp(int commandKind)
    {
        var kind = (InputCommandKind)commandKind;
        var rejectUp = true;
        var sender = new ScriptedSender((_, isDown) => isDown || !rejectUp);
        using var executor = new InputExecutor(RunningRuntime(), sender, new NullLoggerService(),
            clock: () => 0, keyState: _ => false);
        executor.Start();

        Assert.True(executor.Enqueue(new InputCommand(Key.A, IsDown: true, Kind: kind,
            Sequence: kind == InputCommandKind.Sequence ? [new TapStep(Key.A, 0, 0)] : null)));
        if (kind == InputCommandKind.KeyTransition)
        {
            Assert.True(executor.Enqueue(new InputCommand(Key.A, IsDown: false)));
        }
        Assert.True(executor.Enqueue(Wheel()));
        await Fence(executor);
        Assert.Single(sender.Transitions, item => item.IsDown);
        Assert.InRange(sender.Transitions.Count(item => !item.IsDown), 1, 3);

        rejectUp = false;
        Assert.True(executor.Enqueue(new InputCommand(Key.A, IsDown: false)));
        Assert.True(executor.Enqueue(Wheel()));
        await Fence(executor);
        Assert.Equal(2, sender.Transitions.Count(item => item.IsDown));
        Assert.Equal(new[] { (Key.A, true), (Key.A, false) }, sender.Transitions.TakeLast(2));
        Assert.True(executor.StopAndDrain());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WheelTap_FailedOrThrowingDown_SendsNoUpAndDoesNotMarkTargetHeld(bool throws)
    {
        var failDown = true;
        var sender = new ScriptedSender((_, isDown) =>
        {
            if (isDown && failDown)
            {
                failDown = false;
                return throws ? throw new InvalidOperationException("Down failed") : false;
            }
            return true;
        });
        var guard = new CompletionGuard();
        using var executor = new InputExecutor(RunningRuntime(), sender, new NullLoggerService(),
            clock: () => 0, keyState: _ => false);
        executor.Start();

        Assert.True(executor.Enqueue(Wheel(guard)));
        Assert.True(executor.Enqueue(Wheel(guard)));
        await Fence(executor);

        Assert.Equal(new[] { (Key.A, true), (Key.A, true), (Key.A, false) }, sender.Transitions);
        Assert.Equal(2, guard.Completions);
        Assert.True(executor.StopAndDrain());
    }

    [Theory]
    [InlineData("guard")]
    [InlineData("expiry")]
    [InlineData("dispose")]
    [InlineData("stop")]
    public async Task WheelTap_InvalidatedDuringStateQuery_RechecksBeforeDown(string invalidation)
    {
        var runtime = RunningRuntime();
        var sender = new RecordingInputSender();
        var guard = new CompletionGuard();
        long now = 0;
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService(),
            clock: () => now, keyState: _ =>
            {
                switch (invalidation)
                {
                    case "guard": guard.Allows = false; break;
                    case "expiry": now = Stopwatch.Frequency; break;
                    case "dispose": runtime.TryBeginDispose(); break;
                    case "stop": runtime.SetRunning(false); break;
                }
                return false;
            });
        executor.Start();

        Assert.True(executor.Enqueue(Wheel(guard)));
        await guard.Completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Empty(sender.Transitions);
        Assert.Equal(1, guard.Completions);
        Assert.True(executor.StopAndDrain());
    }

    [Fact]
    public async Task WheelTap_RejectedGuard_SkipsNativeStateQuery()
    {
        var sender = new RecordingInputSender();
        var guard = new CompletionGuard { Allows = false };
        var queries = 0;
        using var executor = new InputExecutor(RunningRuntime(), sender, new NullLoggerService(),
            clock: () => 0, keyState: _ => { queries++; return false; });
        executor.Start();

        Assert.True(executor.Enqueue(Wheel(guard)));
        await Fence(executor);

        Assert.Empty(sender.Transitions);
        Assert.Equal(0, queries);
        Assert.Equal(1, guard.Completions);
        Assert.True(executor.StopAndDrain());
    }

    [Fact]
    public async Task WheelTap_ConsecutivePulses_UseCapturedDownDurationAndReleaseGap()
    {
        var timestamps = new ConcurrentQueue<long>();
        var sender = new ScriptedSender((_, _) =>
        {
            timestamps.Enqueue(Stopwatch.GetTimestamp());
            return true;
        });
        using var executor = new InputExecutor(RunningRuntime(), sender, new NullLoggerService(),
            clock: () => 0, keyState: _ => false);
        executor.Start();

        Assert.True(executor.Enqueue(Wheel() with { DelayBeforeMs = 53 }));
        Assert.True(executor.Enqueue(Wheel()));
        await Fence(executor);

        var ticks = timestamps.ToArray();
        Assert.Equal(4, ticks.Length);
        Assert.True(Stopwatch.GetElapsedTime(ticks[0], ticks[1]).TotalMilliseconds >= 50);
        Assert.True(Stopwatch.GetElapsedTime(ticks[1], ticks[2]).TotalMilliseconds >= 8);
        Assert.True(Stopwatch.GetElapsedTime(ticks[2], ticks[3]).TotalMilliseconds >= 28);
        Assert.True(executor.StopAndDrain());
    }

    [Fact]
    public async Task WheelTap_StopDuringSuccessfulDown_ReleasesOnceAndCompletesQueuedPulse()
    {
        var runtime = RunningRuntime();
        var sender = new RecordingInputSender(blockFirstDown: true);
        var first = new CompletionGuard();
        var pending = new CompletionGuard();
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService(),
            clock: () => 0, keyState: _ => false);
        executor.Start();
        try
        {
            Assert.True(executor.Enqueue(Wheel(first)));
            Assert.True(sender.DownEntered.Wait(TimeSpan.FromSeconds(3)));
            Assert.True(executor.Enqueue(Wheel(pending)));
            first.Allows = false;
            runtime.SetRunning(false);
            Assert.False(executor.StopAndDrain(0));
            sender.ReleaseDown.Set();
            await pending.Completed.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(new[] { (Key.A, true), (Key.A, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)));
            Assert.Equal(1, first.Completions);
            Assert.Equal(1, pending.Completions);
            Assert.True(executor.StopAndDrain());
        }
        finally
        {
            sender.ReleaseDown.Set();
            executor.StopAndDrain();
        }
    }

    private static InputCommand Wheel(CompletionGuard? guard = null) =>
        new(Key.A, IsDown: true, DelayBeforeMs: 31, Kind: InputCommandKind.WheelTap, Guard: guard);

    private static InputRuntimeState RunningRuntime()
    {
        var runtime = new InputRuntimeState();
        runtime.SetRunning(true);
        return runtime;
    }

    private static async Task Fence(InputExecutor executor)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(executor.Enqueue(new InputCommand(
            Key.None, IsDown: false, Kind: InputCommandKind.DummyKey, Completion: completion)));
        Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private sealed class CompletionGuard : IInputCommandGuard
    {
        private int _completions;
        internal volatile bool Allows = true;
        internal bool ThrowOnCompleted;
        internal int Completions => Volatile.Read(ref _completions);
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanExecute(in InputCommand command) => Allows;

        public void OnCompleted(in InputCommand command)
        {
            Interlocked.Increment(ref _completions);
            Completed.TrySetResult();
            if (ThrowOnCompleted)
            {
                throw new InvalidOperationException("Completion failed");
            }
        }
    }

    private sealed class ScriptedSender(Func<Key, bool, bool> send) : IInputSender
    {
        internal ConcurrentQueue<(Key Key, bool IsDown)> Transitions { get; } = new();

        public bool SendKey(Key key, bool isKeyDown)
        {
            Transitions.Enqueue((key, isKeyDown));
            return send(key, isKeyDown);
        }

        public bool SendVirtualKeyTap(int virtualKey) => throw new NotSupportedException();
        public bool SendLeftClick(int holdMilliseconds) => throw new NotSupportedException();
        public bool SendDummyKey() => true;
    }
}
