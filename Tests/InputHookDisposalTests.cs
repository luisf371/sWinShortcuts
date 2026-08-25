using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class InputHookDisposalTests
{
    [Fact]
    public void StartAfterDispose_ThrowsBeforeCreatingResources()
    {
        var service = new InputHookService(new NullLoggerService(), new RecordingInputSender());
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(service.Start);
    }

    [Fact]
    public void NeverStartedDispose_RepeatedDisposeAndStopAreNoOps()
    {
        var sender = new RecordingInputSender();
        var service = new InputHookService(new NullLoggerService(), sender);

        var error = Record.Exception(() =>
        {
            service.Dispose();
            service.Dispose();
            service.Stop();
        });

        Assert.Null(error);
        Assert.Empty(sender.Transitions);
    }

    [Fact]
    public void GestureAndAntiAfk_AfterTerminalPublication_DoNotEnqueue()
    {
        var profile = RunningProfile();
        profile.RightClickHoldBreath.IsEnabled = true;
        profile.RightClickHoldBreath.DelayMilliseconds = 0;
        profile.AntiAfk.IsEnabled = true;
        profile.AntiAfk.IntervalMinutes = 1;
        var runtime = RunningRuntime(profile);
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var gestures = new GestureChordStateMachine(
            runtime,
            queue,
            random,
            new NullLoggerService(),
            () => true);
        var transport = new FakeAutoRunTransport();
        transport.ProcessIds[(IntPtr)1] = 1;
        var autoRun = new AutoRunStateMachine(runtime, queue, random, new NullLoggerService(), transport);
        using var antiAfk = new AntiAfkStateMachine(
            runtime,
            autoRun,
            random,
            new NullLoggerService(),
            transport,
            () => Stopwatch.Frequency * 60,
            () => 60_000);

        Assert.True(runtime.TryBeginDispose());
        gestures.HandleRightButtonDown(rightButtonPressed: true);
        gestures.FireHoldBreathTimerForTesting();
        antiAfk.Tick();

        Assert.Empty(queue.Commands);
    }

    [Fact]
    public void LauncherGuardAndLeaf_AfterTerminalPublication_DoNotLaunch()
    {
        var runtime = RunningRuntime();
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var launchCount = 0;
        var remaps = new RemapStateMachine(
            runtime,
            queue,
            random,
            new NullLoggerService(),
            isPhysicalKeyDown: virtualKey => virtualKey == KeyInterop.VirtualKeyFromKey(Key.LWin),
            launchProcess: (_, _, _) => Interlocked.Increment(ref launchCount));
        var windows = new Profile
        {
            Name = "Windows",
            Kind = ProfileKind.Windows,
            Executable = string.Empty
        };
        windows.WindowsLauncher.Launchers[Key.NumPad1] = new LauncherBinding { Path = "tool.exe" };
        remaps.SetWindowsProfile(windows);
        Assert.True(remaps.HandleKeyboardEvent(
            KeyInterop.VirtualKeyFromKey(Key.NumPad1),
            isKeyDown: true,
            isKeyUp: false,
            rightButtonPressed: false));
        var command = Assert.Single(queue.Commands);

        Assert.True(runtime.TryBeginDispose());
        Assert.False(remaps.CanExecute(command));
        typeof(RemapStateMachine)
            .GetMethod("LaunchProcess", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(remaps, new object?[] { "tool.exe", string.Empty, false });

        Assert.Equal(0, Volatile.Read(ref launchCount));
    }

    [Fact]
    public void RapidFire_DisposedWhileClickBlocked_DoesNotScheduleSuccessor()
    {
        var runtime = new InputRuntimeState();
        runtime.SetRunning(true);
        runtime.SetAdvancedMode(true);
        var profile = RunningProfile();
        profile.RapidFire.IsEnabled = true;
        profile.RapidFire.IntervalMilliseconds = RapidFireSettings.MaxIntervalMilliseconds;
        profile.RapidFire.JitterMilliseconds = 0;
        var sender = new RecordingInputSender(blockMouse: true);
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var machine = new RapidFireStateMachine(
            runtime,
            sender,
            random,
            new NullLoggerService(),
            new object());
        machine.ConfigureForTesting(profile, foregroundGeneration: 1);
        machine.HandleLeftButton(isDown: true, allowStart: true);
        var fire = Task.Run(machine.FireTimerForTesting);

        try
        {
            Assert.True(sender.MouseEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(runtime.TryBeginDispose());
            machine.Release(preservePhysicalPairing: false);
            machine.Dispose();
        }
        finally
        {
            sender.ReleaseMouse.Set();
            machine.Dispose();
        }

        Assert.True(fire.Wait(TimeSpan.FromSeconds(2)));
        machine.FireTimerForTesting();
        Assert.Single(sender.MouseClickThreadIds);
    }

    [Fact]
    public void TimedOutExecutorDisposal_SkipsGuardedDownAndDrainsRecordedUp()
    {
        var runtime = new InputRuntimeState();
        runtime.SetRunning(true);
        var sender = new RecordingInputSender(blockFirstDown: true);
        var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        executor.Start();
        executor.Enqueue(new InputCommand(Key.A, IsDown: true));

        try
        {
            Assert.True(sender.DownEntered.Wait(TimeSpan.FromSeconds(2)));
            executor.Enqueue(new InputCommand(Key.B, IsDown: true, Guard: AlwaysGuard.Instance));
            Assert.True(runtime.TryBeginDispose());

            Assert.False(executor.StopAndDrain(
                () => executor.Enqueue(new InputCommand(Key.A, IsDown: false)),
                TimeSpan.Zero));
            executor.Dispose();
            sender.ReleaseDown.Set();

            Assert.True(SpinWait.SpinUntil(() => !executor.IsWorkerAlive, TimeSpan.FromSeconds(2)));
            Assert.Equal(
                new[] { (Key.A, true), (Key.A, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
            Assert.True(executor.DisposeCompletedQueue());
        }
        finally
        {
            sender.ReleaseDown.Set();
            executor.Dispose();
            executor.DisposeCompletedQueue();
        }
    }

    private static InputRuntimeState RunningRuntime(Profile? profile = null)
    {
        var runtime = new InputRuntimeState();
        runtime.SetAdvancedMode(true);
        runtime.SetActiveProfile(profile, 1);
        runtime.SetForegroundIdentity(
            (IntPtr)1,
            1,
            profile?.NormalizedExecutable,
            1);
        runtime.SetRunning(true);
        return runtime;
    }

    private static Profile RunningProfile() => new()
    {
        Name = "Game",
        Executable = "game.exe"
    };

    private sealed class AlwaysGuard : IInputCommandGuard
    {
        internal static AlwaysGuard Instance { get; } = new();

        public bool CanExecute(in InputCommand command) => true;
    }
}
