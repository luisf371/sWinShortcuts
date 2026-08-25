using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class AutoRunStateMachineTests
{
    [Fact]
    public void BackgroundActivation_PostsOnlyFromOwnedWorker()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        var (machine, _, transport, profile) = CreateMachine(AutoRunSendMode.Background);

        try
        {
            Assert.True(Activate(machine, profile));
            Assert.True(transport.PostEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.All(transport.Posts, post => Assert.NotEqual(callerThread, post.ThreadId));
        }
        finally
        {
            machine.Release(includeBackground: true);
            machine.JoinBackgroundInputThread();
        }
    }

    [Fact]
    public void BackgroundActivation_FailedInitialW_StopsRun()
    {
        var (machine, _, transport, profile) = CreateMachine(AutoRunSendMode.Background);
        transport.FailNextPost();

        Assert.True(Activate(machine, profile));
        Assert.True(transport.PostEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => !machine.IsActive, TimeSpan.FromSeconds(2)));
        machine.JoinBackgroundInputThread();

        var post = Assert.Single(transport.Posts);
        Assert.Equal(NativeMethods.WM_KEYDOWN, post.Message);
        Assert.Equal(0x57, post.VirtualKey);
    }

    [Fact]
    public void BackgroundRelease_FinalWUpRunsOnOwnedWorker()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        var (machine, _, transport, profile) = CreateMachine(AutoRunSendMode.Background);

        Assert.True(Activate(machine, profile));
        Assert.True(transport.PostEntered.Wait(TimeSpan.FromSeconds(2)));

        machine.Release(includeBackground: true);
        machine.JoinBackgroundInputThread();

        Assert.Contains(transport.Posts, post =>
            post.Message == NativeMethods.WM_KEYDOWN && post.VirtualKey == 0x57);
        Assert.Contains(transport.Posts, post =>
            post.Message == NativeMethods.WM_KEYUP && post.VirtualKey == 0x57);
        Assert.All(transport.Posts, post => Assert.NotEqual(callerThread, post.ThreadId));
    }

    [Fact]
    public void ForegroundGuard_LiveWindowChanges_RejectsQueuedDown()
    {
        var (machine, queue, transport, profile) = CreateMachine(AutoRunSendMode.Foreground);

        Assert.True(Activate(machine, profile));
        Assert.True(queue.Commands.TryDequeue(out var command));
        Assert.True(machine.CanExecute(command));

        transport.ForegroundWindow = (IntPtr)200;
        transport.ProcessIds[(IntPtr)200] = 9;

        Assert.False(machine.CanExecute(command));
        machine.Release(includeBackground: true);
    }

    private static bool Activate(AutoRunStateMachine machine, Profile profile)
    {
        var vk = KeyInterop.VirtualKeyFromKey(profile.AutoRun.TriggerKey);
        var physical = machine.ObservePhysicalEvent(vk, isKeyDown: true, isKeyUp: false);
        return machine.Handle(vk, isKeyDown: true, isKeyUp: false, physical);
    }

    private static (AutoRunStateMachine Machine, RecordingInputQueue Queue,
        FakeAutoRunTransport Transport, Profile Profile) CreateMachine(AutoRunSendMode sendMode)
    {
        var profile = new Profile
        {
            Name = "Game",
            Executable = "game.exe",
            AutoRun =
            {
                IsEnabled = true,
                TriggerKey = Key.R,
                TriggerModifier = ModifierKeys.None,
                SendMode = sendMode
            }
        };
        var runtime = new InputRuntimeState();
        runtime.SetAdvancedMode(true);
        runtime.SetActiveProfile(profile, 1);
        runtime.SetForegroundIdentity((IntPtr)100, 7, profile.NormalizedExecutable, 1);
        runtime.SetRunning(true);

        var queue = new RecordingInputQueue();
        var transport = new FakeAutoRunTransport();
        transport.ProcessIds[(IntPtr)100] = 7;
        var machine = new AutoRunStateMachine(
            runtime,
            queue,
            new ThreadLocal<Random>(() => new Random(1)),
            new NullLoggerService(),
            transport);
        return (machine, queue, transport, profile);
    }
}
