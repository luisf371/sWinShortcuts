using System.Diagnostics;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class AntiAfkStateMachineTests
{
    [Fact]
    public void Tick_AtExactInterval_EnqueuesWasdSequence()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, queue, _, _, _) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            machine.Tick();

            var command = Assert.Single(queue.Commands);
            Assert.Equal(InputCommandKind.Sequence, command.Kind);
            Assert.Equal(new[] { Key.W, Key.A, Key.S, Key.D }, command.Sequence!.Select(step => step.Key));
        }
    }

    [Fact]
    public void Tick_TickCountWrap_UsesUnsignedElapsedTime()
    {
        long timestamp = 0;
        uint tick = uint.MaxValue - 30_000;
        var (machine, queue, _, _, _) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            timestamp = Stopwatch.Frequency * 60;
            tick = unchecked(tick + 60_000);

            machine.Tick();

            Assert.Single(queue.Commands);
        }
    }

    [Fact]
    public void Tick_WhileTickInFlight_DoesNotReenter()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, queue, transport, _, _) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;
            transport.BlockForegroundReads = true;
            var first = Task.Run(machine.Tick);
            try
            {
                Assert.True(transport.ForegroundEntered.Wait(TimeSpan.FromSeconds(2)));

                machine.Tick();

                Assert.Equal(1, transport.ForegroundCallCount);
            }
            finally
            {
                transport.ReleaseForeground.Set();
                first.GetAwaiter().GetResult();
            }

            Assert.Single(queue.Commands);
        }
    }

    [Fact]
    public void Tick_AutoRunActivatesBeforeFinalArbitration_DoesNotEnqueueSequence()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, queue, transport, autoRun, profile) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;
            profile.AutoRun.IsEnabled = true;
            profile.AutoRun.TriggerKey = Key.R;
            profile.AutoRun.TriggerModifier = ModifierKeys.None;
            transport.BlockForegroundReads = true;
            var antiAfkTick = Task.Run(machine.Tick);
            try
            {
                Assert.True(transport.ForegroundEntered.Wait(TimeSpan.FromSeconds(2)));
                Assert.True(ActivateAutoRun(autoRun, profile));
            }
            finally
            {
                transport.ReleaseForeground.Set();
                antiAfkTick.GetAwaiter().GetResult();
            }

            Assert.DoesNotContain(queue.Commands, command => command.Kind == InputCommandKind.Sequence);
            autoRun.Release(includeBackground: true);
        }
    }

    [Fact]
    public void SequenceGuard_ForegroundChangesAfterFirstDown_ReleasesCurrentStepAndAbortsRest()
    {
        long timestamp = 0;
        uint tick = 0;
        var runtime = new InputRuntimeState();
        var profile = CreateProfile();
        var transport = CreateTransport();
        ConfigureRuntime(runtime, profile);
        var sender = new RecordingInputSender(blockFirstDown: true);
        var random = new ThreadLocal<Random>(() => new Random(1));
        var logger = new NullLoggerService();
        using var executor = new InputExecutor(runtime, sender, logger);
        var autoRun = new AutoRunStateMachine(runtime, executor, random, logger, transport);
        using var machine = new AntiAfkStateMachine(
            runtime,
            autoRun,
            random,
            logger,
            transport,
            () => timestamp,
            () => tick);
        executor.Start();

        try
        {
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;
            machine.Tick();
            Assert.True(sender.DownEntered.Wait(TimeSpan.FromSeconds(2)));

            runtime.SetForegroundIdentity((IntPtr)200, 9, profile.NormalizedExecutable, 2);
            sender.ReleaseDown.Set();
            Assert.True(SpinWait.SpinUntil(() => sender.Transitions.Count == 2, TimeSpan.FromSeconds(2)));

            runtime.SetRunning(false);
            Assert.True(executor.StopAndDrain());
            Assert.Equal(
                new[] { (Key.W, true), (Key.W, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        }
        finally
        {
            sender.ReleaseDown.Set();
            runtime.SetRunning(false);
            executor.StopAndDrain();
        }
    }

    private static bool ActivateAutoRun(AutoRunStateMachine machine, Profile profile)
    {
        var vk = KeyInterop.VirtualKeyFromKey(profile.AutoRun.TriggerKey);
        var physical = machine.ObservePhysicalEvent(vk, isKeyDown: true, isKeyUp: false);
        return machine.Handle(vk, isKeyDown: true, isKeyUp: false, physical);
    }

    private static (AntiAfkStateMachine Machine, RecordingInputQueue Queue,
        FakeAutoRunTransport Transport, AutoRunStateMachine AutoRun, Profile Profile) CreateMachine(
        Func<long> timestamp,
        Func<uint> tickCount)
    {
        var runtime = new InputRuntimeState();
        var profile = CreateProfile();
        var transport = CreateTransport();
        ConfigureRuntime(runtime, profile);
        var queue = new RecordingInputQueue();
        var random = new ThreadLocal<Random>(() => new Random(1));
        var logger = new NullLoggerService();
        var autoRun = new AutoRunStateMachine(runtime, queue, random, logger, transport);
        var machine = new AntiAfkStateMachine(
            runtime,
            autoRun,
            random,
            logger,
            transport,
            timestamp,
            tickCount);
        return (machine, queue, transport, autoRun, profile);
    }

    private static Profile CreateProfile() => new()
    {
        Name = "Game",
        Executable = "game.exe",
        AntiAfk =
        {
            IsEnabled = true,
            IntervalMinutes = 1
        }
    };

    private static FakeAutoRunTransport CreateTransport()
    {
        var transport = new FakeAutoRunTransport();
        transport.ProcessIds[(IntPtr)100] = 7;
        return transport;
    }

    private static void ConfigureRuntime(InputRuntimeState runtime, Profile profile)
    {
        runtime.SetAdvancedMode(true);
        runtime.SetActiveProfile(profile, 1);
        runtime.SetForegroundIdentity((IntPtr)100, 7, profile.NormalizedExecutable, 1);
        runtime.SetRunning(true);
    }
}
