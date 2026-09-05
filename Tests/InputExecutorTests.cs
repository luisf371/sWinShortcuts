using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using sWinShortcuts.Utilities;
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
    public void PreparePairedUp_GuardedDownWithoutAcknowledgement_UsesFifoValueState()
    {
        var guard = new MutableGuard(true);
        var down = new InputCommand(Key.A, IsDown: true, Guard: guard);
        var up = new InputCommand(Key.A, IsDown: false);

        var pairedUp = InputExecutor.PreparePairedUp(in down, in up);

        Assert.Null(down.Acknowledgement);
        Assert.Null(pairedUp.Acknowledgement);
        Assert.False(pairedUp.RequireAcknowledgement);
        Assert.True(pairedUp.RequirePreviousCommandSuccess);
    }

    [Fact]
    public void PreparePairedUp_GuardedDownWithAcknowledgement_PreservesExistingObject()
    {
        var guard = new MutableGuard(true);
        var acknowledgement = new InputCommandAcknowledgement();
        var down = new InputCommand(
            Key.A,
            IsDown: true,
            Guard: guard,
            Acknowledgement: acknowledgement);
        var up = new InputCommand(Key.A, IsDown: false);

        var pairedUp = InputExecutor.PreparePairedUp(in down, in up);

        Assert.Same(acknowledgement, pairedUp.Acknowledgement);
        Assert.True(pairedUp.RequireAcknowledgement);
        Assert.False(pairedUp.RequirePreviousCommandSuccess);
    }

    [Fact]
    public void CapsDoubleNormal_CommandsUseSharedValueTokenWithoutAcknowledgementObjects()
    {
        var profile = DoubleNormalProfile();
        var runtime = RunningRuntime(profile);
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var remaps = new RemapStateMachine(runtime, queue, random, new NullLoggerService());

        Assert.True(HandleCaps(remaps, isDown: true));
        Assert.True(HandleCaps(remaps, isDown: false));

        var commands = queue.Commands.ToArray();
        Assert.Equal(2, commands.Length);
        Assert.Null(commands[0].Acknowledgement);
        Assert.Null(commands[1].Acknowledgement);
        Assert.NotEqual(0, commands[0].TapPairToken);
        Assert.Equal(commands[0].TapPairToken, commands[1].TapPairToken);
        Assert.False(commands[0].RequireTapPairToken);
        Assert.True(commands[1].RequireTapPairToken);
    }

    [Fact]
    public void CapsDoubleNormal_DisposePublishedWhileInitialDownBlocked_CompletesSecondTap()
    {
        var profile = DoubleNormalProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender(blockFirstDown: true);
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var remaps = new RemapStateMachine(runtime, executor, random, new NullLoggerService());
        executor.Start();

        try
        {
            Assert.True(HandleCaps(remaps, isDown: true));
            Assert.True(sender.DownEntered.Wait(TimeSpan.FromSeconds(2)));

            Assert.True(runtime.TryBeginDispose());
            runtime.SetRunning(false);
            remaps.ReleaseCapsStateOnly(preservePhysicalPairing: false);
            sender.ReleaseDown.Set();

            Assert.True(executor.StopAndDrain());
            Assert.Equal(
                new[] { true, false, true, false },
                sender.Transitions.Select(item => item.IsDown));
        }
        finally
        {
            sender.ReleaseDown.Set();
            executor.StopAndDrain();
        }
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

    [Fact]
    public void WindowsInputSender_EarlyReturnSkips_LogDefinitiveReasonsWithoutErrorCodes()
    {
        var logger = new NullLoggerService { IsEnabled = true };
        var sender = new WindowsInputSender(logger);

        // vk==0: no virtual-key mapping — the request never reaches SendInput, so the skip states
        // its reason without pretending an error code explains it.
        Assert.False(sender.SendKey(Key.None, isKeyDown: true));
        Assert.Contains("[Input] SendInput skipped: no virtual-key mapping for None", logger.Messages);

        // Range guard: same shape, with the rejected key visible in the entry.
        Assert.False(sender.SendVirtualKeyTap(0));
        Assert.False(sender.SendVirtualKeyTap(0x10000));
        Assert.Contains("[Input] SendInput skipped: virtual key 0x0 out of range", logger.Messages);
        Assert.Contains("[Input] SendInput skipped: virtual key 0x10000 out of range", logger.Messages);
    }

    private static InputRuntimeState RunningRuntime()
    {
        var runtime = new InputRuntimeState();
        runtime.SetRunning(true);
        return runtime;
    }

    private static InputRuntimeState RunningRuntime(Profile profile)
    {
        var runtime = RunningRuntime();
        runtime.SetActiveProfile(profile, 1);
        runtime.SetForegroundIdentity(IntPtr.Zero, 0, profile.NormalizedExecutable, 1);
        return runtime;
    }

    private static Profile DoubleNormalProfile() => new()
    {
        Name = "Game",
        Executable = "game.exe",
        CapsLock =
        {
            IsEnabled = true,
            Mode = CapsLockMode.DoubleNormal
        }
    };

    private static bool HandleCaps(RemapStateMachine remaps, bool isDown) =>
        remaps.HandleKeyboardEvent(
            KeyInteropUtilities.ToVirtualKey(Key.CapsLock),
            isKeyDown: isDown,
            isKeyUp: !isDown,
            rightButtonPressed: false);

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
