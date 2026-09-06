using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class AutoRunStateMachineTests
{
    [Theory]
    [InlineData(AutoRunSendMode.Foreground, 200, 7)]
    [InlineData(AutoRunSendMode.Foreground, 100, 9)]
    [InlineData(AutoRunSendMode.Background, 200, 7)]
    [InlineData(AutoRunSendMode.Background, 100, 9)]
    public void Activation_LiveForegroundDoesNotMatchSnapshot_FailsClosed(
        AutoRunSendMode sendMode,
        int liveWindow,
        int liveProcessId)
    {
        var (machine, queue, transport, profile) = CreateMachine(sendMode);
        transport.ForegroundWindow = (IntPtr)liveWindow;
        transport.ProcessIds[transport.ForegroundWindow] = (uint)liveProcessId;

        try
        {
            Assert.False(Activate(machine, profile));
            Assert.False(machine.IsActive);
            Assert.Empty(queue.Commands);
            Assert.Empty(transport.Posts);
        }
        finally
        {
            machine.Release(includeBackground: true);
            machine.JoinBackgroundInputThread();
        }
    }

    [Fact]
    public void BackgroundPhysicalWHandoff_FocusMovesAway_CancelAndSprintPassThrough()
    {
        var (machine, _, transport, profile) = CreateMachine(AutoRunSendMode.Background);
        profile.AutoRun.SprintEnabled = true;
        profile.AutoRun.SprintMode = SprintActivation.Hold;
        profile.AutoRun.SprintKey = Key.LeftShift;

        var w = KeyInterop.VirtualKeyFromKey(Key.W);
        machine.ObservePhysicalEvent(w, isKeyDown: true, isKeyUp: false);

        try
        {
            Assert.True(Activate(machine, profile));
            Assert.True(SpinWait.SpinUntil(
                () => transport.ForegroundCallCount >= 2,
                TimeSpan.FromSeconds(2)));

            transport.ForegroundWindow = (IntPtr)200;
            transport.ProcessIds[transport.ForegroundWindow] = 9;

            var sprint = KeyInterop.VirtualKeyFromKey(Key.LeftShift);
            var sprintPhysical = machine.ObservePhysicalEvent(sprint, isKeyDown: true, isKeyUp: false);
            Assert.False(machine.Handle(sprint, isKeyDown: true, isKeyUp: false, sprintPhysical));

            var s = KeyInterop.VirtualKeyFromKey(Key.S);
            var cancelPhysical = machine.ObservePhysicalEvent(s, isKeyDown: true, isKeyUp: false);
            Assert.False(machine.Handle(s, isKeyDown: true, isKeyUp: false, cancelPhysical));
            Assert.True(machine.IsActive);
        }
        finally
        {
            machine.Release(includeBackground: true);
            machine.JoinBackgroundInputThread();
        }
    }

    [Fact]
    public void ForegroundActivation_PriorBackgroundWorkerStillAlive_FailsClosed()
    {
        var (machine, queue, _, profile) = CreateMachine(AutoRunSendMode.Foreground);
        using var releaseWorker = new ManualResetEventSlim(false);
        var priorWorker = new Thread(releaseWorker.Wait) { IsBackground = true };
        priorWorker.Start();
        machine.SetBackgroundThreadForTesting(priorWorker);

        try
        {
            Assert.False(Activate(machine, profile));
            Assert.False(machine.IsActive);
            Assert.Empty(queue.Commands);
        }
        finally
        {
            releaseWorker.Set();
            Assert.True(priorWorker.Join(TimeSpan.FromSeconds(2)));
            machine.SetBackgroundThreadForTesting(null);
            machine.Release(includeBackground: true);
        }
    }

    [Fact]
    public void AntiAfkTap_PriorBackgroundWorkerStillAlive_FailsClosed()
    {
        var (machine, _, _, _) = CreateMachine(AutoRunSendMode.Foreground);
        using var releaseWorker = new ManualResetEventSlim(false);
        var priorWorker = new Thread(releaseWorker.Wait) { IsBackground = true };
        priorWorker.Start();
        machine.SetBackgroundThreadForTesting(priorWorker);

        try
        {
            Assert.False(machine.TryBeginAntiAfkTap());
        }
        finally
        {
            releaseWorker.Set();
            Assert.True(priorWorker.Join(TimeSpan.FromSeconds(2)));
            machine.SetBackgroundThreadForTesting(null);
        }
    }

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
        var logger = new NullLoggerService { IsEnabled = true };
        var (machine, _, transport, profile) = CreateMachine(AutoRunSendMode.Background, logger);
        transport.FailNextPost();

        Assert.True(Activate(machine, profile));
        Assert.True(transport.PostEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => !machine.IsActive, TimeSpan.FromSeconds(2)));
        machine.JoinBackgroundInputThread();

        var post = Assert.Single(transport.Posts);
        Assert.Equal((uint)NativeMethods.WM_KEYDOWN, post.Message);
        Assert.Equal(0x57, post.VirtualKey);
        Assert.Single(logger.Messages, m => m == "AutoRun release requested (background movement injection failed)");
    }

    [Fact]
    public void ForegroundActivationAndRelease_AreReportedWithMode()
    {
        var logger = new NullLoggerService { IsEnabled = true };
        var (machine, _, _, profile) = CreateMachine(AutoRunSendMode.Foreground, logger);

        // No-op release while inactive: the early-return gate keeps it silent.
        machine.Release(includeBackground: true);
        Assert.Empty(logger.Messages);

        Assert.True(Activate(machine, profile));
        Assert.Equal("AutoRun activated (foreground) for profile: Game", Assert.Single(logger.Messages));

        machine.Release(includeBackground: true);
        Assert.Equal("AutoRun release requested (foreground)", logger.Messages[^1]);
    }

    [Fact]
    public void BackgroundActivationAndRelease_AreReportedWithMode()
    {
        var logger = new NullLoggerService { IsEnabled = true };
        var (machine, _, transport, profile) = CreateMachine(AutoRunSendMode.Background, logger);

        Assert.True(Activate(machine, profile));
        Assert.True(transport.PostEntered.Wait(TimeSpan.FromSeconds(2)));

        machine.Release(includeBackground: true);
        machine.JoinBackgroundInputThread();

        Assert.Equal("AutoRun activated (background) for profile: Game", logger.Messages[0]);
        Assert.Equal("AutoRun release requested (background)", logger.Messages[^1]);
    }

    [Fact]
    public void BackgroundTargetInvalidation_RequestsReleaseOnceWithReason()
    {
        var logger = new NullLoggerService { IsEnabled = true };
        var (machine, _, transport, profile) = CreateMachine(AutoRunSendMode.Background, logger);

        Assert.True(Activate(machine, profile));
        // Readiness signal: ResolveBackgroundTarget consumed the first foreground read and the
        // loop's ForegroundIsTargetProcess the second, so initial resolution completed and the
        // loop is running.
        Assert.True(SpinWait.SpinUntil(() => transport.ForegroundCallCount >= 2, TimeSpan.FromSeconds(2)));

        // Invalidate the RETAINED target's PID entry, not the foreground: BackgroundTargetValid
        // revalidates the retained HWND->PID and never consults the foreground, and background
        // Auto-Run intentionally survives pure focus moves — moving the foreground would leave
        // this branch unexercised.
        transport.ProcessIds[(IntPtr)100] = 9;

        Assert.True(SpinWait.SpinUntil(() => !machine.IsActive, TimeSpan.FromSeconds(2)));
        machine.JoinBackgroundInputThread();

        // Exactly one: the release is requested once here and every subsequent ReleaseLocked call
        // is silenced by its !_active early-return gate.
        Assert.Single(logger.Messages, m => m == "AutoRun release requested (background target validation failed)");
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BackgroundSprintPress_StopAfterDown_ReleasesOnlySuccessfulOriginalTarget(
        bool failDown, bool reuseWindow)
    {
        var delays = new OnDelayRandom();
        var (machine, _, transport, profile) = CreateMachine(AutoRunSendMode.Background, random: delays);
        profile.AutoRun.SprintEnabled = true;
        profile.AutoRun.SprintMode = SprintActivation.Press;
        profile.AutoRun.SprintKey = Key.LeftShift;
        delays.OnDelay = call =>
        {
            if (call == 1 && failDown) transport.FailNextPost();
            if (call == 2)
            {
                if (reuseWindow) transport.ProcessIds[(IntPtr)100] = 9;
                machine.Release(includeBackground: true);
            }
        };

        try
        {
            Assert.True(Activate(machine, profile));
            var sprintVk = KeyInterop.VirtualKeyFromKey(Key.LeftShift);
            Assert.True(SpinWait.SpinUntil(
                () => transport.Posts.Any(p => p.VirtualKey == sprintVk && p.Message == NativeMethods.WM_KEYDOWN),
                TimeSpan.FromSeconds(2)));
            if (failDown) machine.Release(includeBackground: true);
            Assert.True(SpinWait.SpinUntil(() => !machine.IsActive, TimeSpan.FromSeconds(2)));
            machine.JoinBackgroundInputThread();
            Assert.Equal(!failDown && !reuseWindow,
                transport.Posts.Any(p => p.VirtualKey == sprintVk && p.Message == NativeMethods.WM_KEYUP));
        }
        finally
        {
            machine.Release(includeBackground: true);
            machine.JoinBackgroundInputThread();
        }
    }

    private sealed class OnDelayRandom : Random
    {
        private int _calls;
        internal Action<int>? OnDelay { get; set; }

        public override int Next(int minValue, int maxValue)
        {
            OnDelay?.Invoke(++_calls);
            return minValue;
        }
    }

    [Fact]
    public async Task BackgroundNativeLookupBlocked_PhysicalWUpStopsWithoutWaiting()
    {
        var (machine, _, transport, profile) = CreateMachine(AutoRunSendMode.Background);
        using var hookEntered = new ManualResetEventSlim(false);
        Task? hookUp = null;
        var downsAtBlock = 0;
        try
        {
            Assert.True(Activate(machine, profile));
            Assert.True(transport.PostEntered.Wait(TimeSpan.FromSeconds(2)));
            var w = KeyInterop.VirtualKeyFromKey(Key.W);
            var physical = machine.ObservePhysicalEvent(w, true, false);
            Assert.False(machine.Handle(w, true, false, physical));

            transport.BlockForegroundReads = true;
            Assert.True(transport.ForegroundEntered.Wait(TimeSpan.FromSeconds(2)));
            downsAtBlock = transport.Posts.Count(p => p.Message == NativeMethods.WM_KEYDOWN);
            hookUp = Task.Run(() =>
            {
                hookEntered.Set();
                machine.ObservePhysicalEvent(w, false, true);
            });
            Assert.True(hookEntered.Wait(TimeSpan.FromSeconds(2)));
            await hookUp.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.False(machine.IsActive);
            Assert.False(machine.TryBeginAntiAfkTap());
        }
        finally
        {
            transport.ReleaseForeground.Set();
            if (hookUp is not null) await hookUp.WaitAsync(TimeSpan.FromSeconds(2));
            machine.Release(includeBackground: true);
            machine.JoinBackgroundInputThread();
        }

        Assert.Equal(downsAtBlock,
            transport.Posts.Count(p => p.Message == NativeMethods.WM_KEYDOWN));
        Assert.Contains(transport.Posts,
            p => p.VirtualKey == 0x57 && p.Message == NativeMethods.WM_KEYUP);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    public async Task BackgroundNativePostBlocked_StopRemainsResponsiveAndRetainsReleaseOwnership(
        bool blockAttachment, bool failDown, bool reuseWindow)
    {
        var (machine, _, transport, profile) = CreateMachine(AutoRunSendMode.Background);
        using var entered = new ManualResetEventSlim(false);
        using var releaseNative = new ManualResetEventSlim(false);
        Task? stop = null;
        var blockOnce = 0;
        void BlockNative()
        {
            if (Interlocked.Exchange(ref blockOnce, 1) != 0) return;
            entered.Set();
            releaseNative.Wait(TimeSpan.FromSeconds(2));
        }
        if (blockAttachment) transport.OnAttach = attach => { if (attach) BlockNative(); };
        else transport.OnPost = (_, message, _, _) => { if (message == NativeMethods.WM_KEYDOWN) BlockNative(); };
        try
        {
            Assert.True(Activate(machine, profile));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            if (failDown) transport.FailNextPost();
            if (reuseWindow) transport.ProcessIds[(IntPtr)100] = 9;
            stop = Task.Run(() => machine.Release(includeBackground: true));
            await stop.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.False(machine.IsActive);
            Assert.False(machine.TryBeginAntiAfkTap());
            machine.ClearTriggerLatches();
            Assert.False(Activate(machine, profile));
        }
        finally
        {
            releaseNative.Set();
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(2));
            machine.Release(includeBackground: true);
            machine.JoinBackgroundInputThread();
        }
        Assert.Equal(blockAttachment ? 0 : 1,
            transport.Posts.Count(p => p.Message == NativeMethods.WM_KEYDOWN));
        Assert.Equal(!blockAttachment && !failDown && !reuseWindow ? 1 : 0,
            transport.Posts.Count(p => p.Message == NativeMethods.WM_KEYUP));
        Assert.True(machine.TryBeginAntiAfkTap());
        machine.EndAntiAfkTap();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task BackgroundSprintDownBlocked_TogglesChangeDesiredHoldWithoutWaiting(int toggles)
    {
        var (machine, _, transport, profile) = CreateMachine(AutoRunSendMode.Background);
        profile.AutoRun.SprintEnabled = true;
        profile.AutoRun.SprintMode = SprintActivation.Hold;
        profile.AutoRun.SprintKey = Key.LeftShift;
        using var sprintEntered = new ManualResetEventSlim(false);
        using var releaseSprint = new ManualResetEventSlim(false);
        using var nextRepeat = new ManualResetEventSlim(false);
        var blockOnce = 0;
        var sprintVk = KeyInterop.VirtualKeyFromKey(Key.LeftShift);
        transport.OnPost = (_, message, vk, _) =>
        {
            if (message == NativeMethods.WM_KEYDOWN && vk == sprintVk
                && Interlocked.Exchange(ref blockOnce, 1) == 0)
            {
                sprintEntered.Set();
                releaseSprint.Wait(TimeSpan.FromSeconds(2));
            }
            else if (vk == 0x57 && Volatile.Read(ref blockOnce) != 0) nextRepeat.Set();
        };
        Task? toggle = null;
        try
        {
            Assert.True(Activate(machine, profile));
            Assert.True(sprintEntered.Wait(TimeSpan.FromSeconds(2)));
            toggle = Task.Run(() =>
            {
                for (var i = 0; i < toggles; i++)
                {
                    var down = machine.ObservePhysicalEvent(sprintVk, true, false);
                    Assert.True(machine.Handle(sprintVk, true, false, down));
                    var up = machine.ObservePhysicalEvent(sprintVk, false, true);
                    Assert.True(machine.Handle(sprintVk, false, true, up));
                }
            });
            await toggle.WaitAsync(TimeSpan.FromMilliseconds(500));
            releaseSprint.Set();
            Assert.True(nextRepeat.Wait(TimeSpan.FromSeconds(2)));
            Assert.Equal(toggles == 1,
                transport.Posts.Any(p => p.VirtualKey == sprintVk && p.Message == NativeMethods.WM_KEYUP));
        }
        finally
        {
            releaseSprint.Set();
            if (toggle is not null) await toggle.WaitAsync(TimeSpan.FromSeconds(2));
            machine.Release(includeBackground: true);
            machine.JoinBackgroundInputThread();
        }
    }

    [Theory]
    [InlineData(AutoRunSendMode.Background)]
    [InlineData(AutoRunSendMode.Foreground)]
    public async Task ActivationNativeLookupBlocked_ReleaseInvalidatesReservationWithoutWaiting(AutoRunSendMode mode)
    {
        var (machine, queue, transport, profile) = CreateMachine(mode);
        transport.BlockForegroundReads = true;
        var activation = Task.Run(() => Activate(machine, profile));
        Task? stop = null;
        try
        {
            Assert.True(transport.ForegroundEntered.Wait(TimeSpan.FromSeconds(2)));
            stop = Task.Run(() => machine.Release(includeBackground: true));
            await stop.WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            transport.ReleaseForeground.Set();
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.False(await activation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(machine.IsActive);
        Assert.Empty(queue.Commands);
        Assert.Empty(transport.Posts);
        machine.JoinBackgroundInputThread();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BackgroundInitialTargetLookupBlocked_CanceledHandoffReleasesOriginalTarget(bool reuseWindow, bool childTarget)
    {
        var (machine, _, transport, profile) = CreateMachine(AutoRunSendMode.Background);
        machine.ObservePhysicalEvent(0x57, isKeyDown: true, isKeyUp: false);
        var target = childTarget ? (IntPtr)101 : (IntPtr)100;
        if (childTarget)
        {
            transport.ChildWindow = target;
            transport.ProcessIds[target] = 7;
        }
        transport.BlockProcessReadNumber = childTarget ? 3 : 2;
        Task? stop = null;
        try
        {
            Assert.True(Activate(machine, profile));
            Assert.True(transport.ProcessReadEntered.Wait(TimeSpan.FromSeconds(2)));
            var physical = machine.ObservePhysicalEvent(0x57, isKeyDown: false, isKeyUp: true);
            Assert.True(physical.SuppressPhysicalWHandoffUp);
            Assert.True(machine.Handle(0x57, isKeyDown: false, isKeyUp: true, physical));
            stop = Task.Run(() => machine.Release(includeBackground: true));
            await stop.WaitAsync(TimeSpan.FromMilliseconds(500));
            if (reuseWindow)
            {
                transport.ProcessIds[(IntPtr)100] = 9;
                transport.ProcessIds[target] = 9;
            }
            Assert.False(machine.TryBeginAntiAfkTap());
            machine.ClearTriggerLatches();
            Assert.False(Activate(machine, profile));
        }
        finally
        {
            transport.ReleaseProcessRead.Set();
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(2));
            machine.Release(includeBackground: true);
            machine.JoinBackgroundInputThread();
        }
        Assert.DoesNotContain(transport.Posts, p => p.Message == NativeMethods.WM_KEYDOWN);
        if (reuseWindow) Assert.Empty(transport.Posts);
        else
        {
            var release = Assert.Single(transport.Posts);
            Assert.Equal((target, (uint)NativeMethods.WM_KEYUP, 0x57),
                (release.Window, release.Message, release.VirtualKey));
        }
    }

    [Theory]
    [InlineData(SprintActivation.Press)]
    [InlineData(SprintActivation.Hold)]
    public void BackgroundSprintUsesMovementKey_PreservesMovementRepeats(SprintActivation activation)
    {
        var (machine, _, transport, profile) = CreateMachine(AutoRunSendMode.Background);
        profile.AutoRun.SprintEnabled = true;
        profile.AutoRun.SprintKey = Key.W;
        profile.AutoRun.SprintMode = activation;
        using var repeated = new ManualResetEventSlim(false);
        bool sawSprintUp = false;
        transport.OnPost = (_, message, vk, lParam) =>
        {
            if (vk != 0x57) return;
            if (message == NativeMethods.WM_KEYUP) sawSprintUp = true;
            if (message == NativeMethods.WM_KEYDOWN && (lParam.ToInt64() & (1L << 30)) != 0
                && (activation == SprintActivation.Hold || sawSprintUp)) repeated.Set();
        };
        try
        {
            Assert.True(Activate(machine, profile));
            Assert.True(repeated.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(machine.IsActive);
        }
        finally
        {
            machine.Release(includeBackground: true);
            machine.JoinBackgroundInputThread();
        }
    }

    private static bool Activate(AutoRunStateMachine machine, Profile profile)
    {
        var vk = KeyInterop.VirtualKeyFromKey(profile.AutoRun.TriggerKey);
        var physical = machine.ObservePhysicalEvent(vk, isKeyDown: true, isKeyUp: false);
        return machine.Handle(vk, isKeyDown: true, isKeyUp: false, physical);
    }

    private static (AutoRunStateMachine Machine, RecordingInputQueue Queue,
        FakeAutoRunTransport Transport, Profile Profile) CreateMachine(
        AutoRunSendMode sendMode,
        NullLoggerService? logger = null, Random? random = null)
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
            new ThreadLocal<Random>(() => random ?? new Random(1)),
            logger ?? new NullLoggerService(),
            transport);
        return (machine, queue, transport, profile);
    }
}
