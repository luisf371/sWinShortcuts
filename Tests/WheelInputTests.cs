using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class WheelInputTests
{
    [Theory]
    [InlineData(false, 120, Key.E)]
    [InlineData(false, -120, Key.Q)]
    [InlineData(true, 120, Key.F)]
    [InlineData(true, -120, Key.G)]
    public async Task DispatchWheel_BothDirectionsAndRoutes_SendOneTap(bool alt, int delta, Key target)
    {
        using var fixture = new ServiceFixture();
        if (alt) fixture.Alt(true);

        Assert.True(fixture.Wheel(delta));
        await fixture.Drain();

        Assert.Equal(new[] { (target, true), (target, false) }, fixture.Transitions);
    }

    [Theory]
    [InlineData(30, 4, 1)]
    [InlineData(240, 1, 2)]
    [InlineData(0, 1, 0)]
    public async Task DispatchWheel_DistanceAccumulatesIntoWholeIncrements(int delta, int packets, int taps)
    {
        using var fixture = new ServiceFixture();
        for (var i = 0; i < packets; i++) Assert.Equal(delta != 0, fixture.Wheel(delta));
        await fixture.Drain();
        Assert.Equal(taps * 2, fixture.Transitions.Length);
    }

    [Fact]
    public async Task DispatchWheel_DirectionReversal_DiscardsOppositeRemainder()
    {
        using var fixture = new ServiceFixture();
        Assert.True(fixture.Wheel(90));
        Assert.True(fixture.Wheel(-30));
        await fixture.Drain();
        Assert.Empty(fixture.Transitions);
        Assert.True(fixture.Wheel(-90));
        await fixture.Drain();
        Assert.Equal(new[] { (Key.Q, true), (Key.Q, false) }, fixture.Transitions);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public async Task DispatchWheel_RightClickAndSuppression_ReuseMappingPolicy(
        bool rightDown, bool advanced, bool suppress, bool expectedSuppression)
    {
        using var fixture = new ServiceFixture();
        fixture.Profile.CombinedMappings.Mappings[0].RightClickOnly = true;
        fixture.Profile.CombinedMappings.Mappings[0].SuppressOriginalKey = suppress;
        fixture.Service.AdvancedModeEnabled = advanced;
        if (rightDown) fixture.Right(true);
        Assert.Equal(expectedSuppression, fixture.Wheel(30));
        Assert.Equal(expectedSuppression, fixture.Wheel(90));
        await fixture.Drain();
        Assert.Equal(rightDown ? 2 : 0, fixture.Transitions.Length);
    }

    [Fact]
    public async Task DispatchWheel_AltWinsAndMissingDirectionFallsBackToMapping()
    {
        using var fixture = new ServiceFixture();
        fixture.Profile.AltMouse.WheelDownKey = null;
        fixture.Alt(true);
        fixture.Right(true);
        Assert.True(fixture.Wheel(120));
        Assert.True(fixture.Wheel(-120));
        await fixture.Drain();
        Assert.Equal(new[] { (Key.F, true), (Key.F, false), (Key.Q, true), (Key.Q, false) }, fixture.Transitions);
    }

    [Fact]
    public async Task DispatchWheel_BusyAltTarget_DoesNotFallThroughToMapping()
    {
        using var fixture = new ServiceFixture(keyState: vk => vk == KeyInterop.VirtualKeyFromKey(Key.F));
        fixture.Alt(true);
        Assert.True(fixture.Wheel(120));
        await fixture.Drain();
        Assert.Empty(fixture.Transitions);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("feature")]
    [InlineData("source")]
    [InlineData("target")]
    [InlineData("sentinel")]
    [InlineData("foreground")]
    public async Task DispatchWheel_InvalidRoute_PassesAndClearsFraction(string invalidation)
    {
        using var fixture = new ServiceFixture();
        Assert.True(fixture.Wheel(90));
        var mapping = fixture.Profile.CombinedMappings.Mappings[0];
        switch (invalidation)
        {
            case "profile": fixture.Profile.IsEnabled = false; break;
            case "feature": fixture.Profile.CombinedMappings.IsEnabled = false; break;
            case "source": mapping.Source = InputTrigger.FromKey(Key.E); break;
            case "target": mapping.TargetKey = (Key)int.MaxValue; break;
            case "sentinel": mapping.TargetKey = Key.System; break;
            case "foreground": fixture.Service.SetForegroundIdentity(IntPtr.Zero, 0, "game.exe", 2); break;
        }
        Assert.False(fixture.Wheel(120));
        fixture.Profile.IsEnabled = true;
        fixture.Profile.CombinedMappings.IsEnabled = true;
        mapping.Source = InputTrigger.FromWheel(MouseWheelDirection.Up);
        mapping.TargetKey = Key.E;
        if (invalidation == "foreground") fixture.Service.ActivateProfile(fixture.Profile, 2);
        Assert.True(fixture.Wheel(30));
        await fixture.Drain();
        Assert.Empty(fixture.Transitions);
    }

    [Theory]
    [InlineData("alt")]
    [InlineData("right")]
    [InlineData("mode")]
    [InlineData("mapping")]
    [InlineData("altMouse")]
    [InlineData("identity")]
    [InlineData("master")]
    [InlineData("removed")]
    [InlineData("foreground")]
    [InlineData("profile")]
    [InlineData("release")]
    [InlineData("reset")]
    [InlineData("rederive")]
    public async Task ServiceBoundary_InvalidatesQueuedWheelAndRemainder(string boundary)
    {
        using var fixture = new ServiceFixture(blockWorker: true);
        var blocked = fixture.Service.EnqueueDummyForTesting();
        Assert.True(fixture.Sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(fixture.Wheel(210));
        fixture.Boundary(boundary);
        Assert.Equal(1, fixture.Pending);
        Assert.True(fixture.Wheel(30));
        fixture.Sender.ReleaseDummy.Set();
        await blocked.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Drain();
        Assert.Empty(fixture.Transitions);
        Assert.Equal(0, fixture.Pending);
        Assert.True(fixture.Wheel(90));
        await fixture.Drain();
        Assert.Equal(2, fixture.Transitions.Length);
    }

    [Fact]
    public async Task AggregateAlt_OneSideStillHeld_PreservesWheelFraction()
    {
        var rightAlt = KeyInterop.VirtualKeyFromKey(Key.RightAlt);
        using var fixture = new ServiceFixture(keyState: vk => vk == rightAlt);
        fixture.Alt(true);
        fixture.Service.DispatchDecodedKeyboardEvent(rightAlt, true, false);
        Assert.True(fixture.Wheel(90));
        fixture.Alt(false);
        Assert.True(fixture.Wheel(30));
        await fixture.Drain();
        Assert.Equal(new[] { (Key.F, true), (Key.F, false) }, fixture.Transitions);
    }

    [Fact]
    public async Task BurstsAcrossEpochs_KeepFourReservationsUntilDrain()
    {
        using var fixture = new ServiceFixture(blockWorker: true);
        var blocked = fixture.Service.EnqueueDummyForTesting();
        Assert.True(fixture.Sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(fixture.Wheel(32760));
        Assert.Equal(4, fixture.Pending);
        for (var i = 0; i < 20; i++)
        {
            fixture.Right(true);
            fixture.Right(false);
            Assert.True(fixture.Wheel(32760));
            Assert.Equal(4, fixture.Pending);
        }
        fixture.Sender.ReleaseDummy.Set();
        await blocked.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Drain();
        Assert.Empty(fixture.Transitions);
        Assert.Equal(0, fixture.Pending);
        Assert.True(fixture.Wheel(120));
        await fixture.Drain();
        Assert.Equal(2, fixture.Transitions.Length);
    }

    [Fact]
    public async Task ExpiredWheel_RefundsReservationWithoutSending()
    {
        using var fixture = new ServiceFixture(blockWorker: true);
        var blocked = fixture.Service.EnqueueDummyForTesting();
        Assert.True(fixture.Sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(fixture.Wheel(120));
        fixture.NowTicks = Stopwatch.Frequency;
        fixture.Sender.ReleaseDummy.Set();
        await blocked.WaitAsync(TimeSpan.FromSeconds(2));
        await fixture.Drain();
        Assert.Empty(fixture.Transitions);
        Assert.Equal(0, fixture.Pending);
    }

    [Fact]
    public async Task Stop_RejectsQueuedDownAndRefundsReservations()
    {
        using var fixture = new ServiceFixture(blockWorker: true);
        var blocked = fixture.Service.EnqueueDummyForTesting();
        Assert.True(fixture.Sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(fixture.Wheel(120));
        var stop = Task.Factory.StartNew(fixture.Service.Stop, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        // Wait for the production Stop boundary before allowing the blocked worker to drain.
        Assert.True(SpinWait.SpinUntil(() => !fixture.Service.IsInputRunningForTesting(), TimeSpan.FromSeconds(2)));
        fixture.Sender.ReleaseDummy.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
        await blocked.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Empty(fixture.Transitions);
        Assert.Equal(0, fixture.Pending);
        Assert.False(fixture.Wheel(120));
    }

    [Fact]
    public async Task ForegroundChangesAfterDown_StillReleasesExactlyOnce()
    {
        using var fixture = new ServiceFixture(blockFirstDown: true);
        Assert.True(fixture.Wheel(120));
        Assert.True(fixture.Sender.DownEntered.Wait(TimeSpan.FromSeconds(2)));
        fixture.Service.SetForegroundIdentity(IntPtr.Zero, 0, "other.exe", 2);
        fixture.Sender.ReleaseDown.Set();
        await fixture.Drain();
        Assert.Equal(new[] { (Key.E, true), (Key.E, false) }, fixture.Transitions);
        Assert.Equal(0, fixture.Pending);
    }

    [Fact]
    public async Task ForegroundChangesDuringNativeKeyQuery_FinalGuardRejectsDown()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var fixture = new ServiceFixture(keyState: vk =>
        {
            if (vk == KeyInterop.VirtualKeyFromKey(Key.E))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(2));
            }
            return false;
        });
        try
        {
            Assert.True(fixture.Wheel(120));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            fixture.Service.SetForegroundIdentity(IntPtr.Zero, 0, "other.exe", 2);
        }
        finally { release.Set(); }
        await fixture.Drain();
        Assert.Empty(fixture.Transitions);
        Assert.Equal(0, fixture.Pending);
    }

    [Theory]
    [InlineData("physical")]
    [InlineData("remap")]
    [InlineData("holdBreath")]
    public async Task BusyWheelTarget_SkipsBothEdgesAndPreservesHold(string owner)
    {
        using var fixture = new ServiceFixture(keyState: vk => owner == "physical" && vk == KeyInterop.VirtualKeyFromKey(Key.E));
        if (owner == "remap")
        {
            fixture.Profile.CombinedMappings.Mappings.Add(new CombinedMappingEntry
            {
                Source = InputTrigger.FromKey(Key.A), TargetKey = Key.E
            });
            fixture.Service.DispatchDecodedKeyboardEvent(KeyInterop.VirtualKeyFromKey(Key.A), true, false);
        }
        else if (owner == "holdBreath")
        {
            fixture.Profile.RightClickHoldBreath.IsEnabled = true;
            fixture.Profile.RightClickHoldBreath.DelayMilliseconds = 0;
            fixture.Profile.RightClickHoldBreath.HoldBreathKey = Key.E;
            fixture.Profile.RightClickHoldBreath.Mode = HoldBreathMode.Hold;
            fixture.Service.AdvancedModeEnabled = true;
            fixture.Right(true);
        }
        await fixture.Drain();
        Assert.True(fixture.Wheel(120));
        await fixture.Drain();
        Assert.Equal(owner == "physical" ? [] : new[] { (Key.E, true) }, fixture.Transitions);
        if (owner == "remap") fixture.Service.DispatchDecodedKeyboardEvent(KeyInterop.VirtualKeyFromKey(Key.A), false, true);
        if (owner == "holdBreath") fixture.Right(false);
        await fixture.Drain();
        Assert.Equal(owner == "physical" ? 0 : 2, fixture.Transitions.Length);
    }

    [Fact]
    public async Task PendingHoldBreathAndUnsupportedWheelPanic_WheelDoesNotCancelOrArm()
    {
        using var fixture = new ServiceFixture();
        fixture.Service.AdvancedModeEnabled = true;
        fixture.Profile.RightClickHoldBreath.IsEnabled = true;
        fixture.Profile.RightClickHoldBreath.DelayMilliseconds = 60_000;
        fixture.Profile.RightClickHoldBreath.HoldBreathKey = Key.LeftShift;
        fixture.Profile.RightClickHoldBreath.PanicTrigger = InputTrigger.FromWheel(MouseWheelDirection.Up);
        fixture.Right(true);
        Assert.True(fixture.Wheel(120));
        fixture.Gestures.FireHoldBreathTimerForTesting();
        await fixture.Drain();
        Assert.Contains((Key.LeftShift, true), fixture.Transitions);
        fixture.Right(false);
        await fixture.Drain();
        Assert.Equal((Key.LeftShift, false), fixture.Transitions.Last());
    }

    [Fact]
    public void DisabledAndUnmatchedWheel_WarmedPathAllocatesNothing()
    {
        using var fixture = new ServiceFixture();
        fixture.Profile.CombinedMappings.IsEnabled = false;
        for (var i = 0; i < 100; i++) fixture.Wheel(120);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) fixture.Wheel(120);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(0, fixture.Pending);
        fixture.Profile.CombinedMappings.IsEnabled = true;
        fixture.Profile.CombinedMappings.Mappings.Clear();
        for (var i = 0; i < 100; i++) fixture.Wheel(120);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) fixture.Wheel(120);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("replacement")]
    [InlineData("stopped")]
    [InlineData("disposed")]
    [InlineData("move")]
    [InlineData("horizontal")]
    [InlineData("injected")]
    public void MouseCallback_FilteredPackets_DoNotReadInvalidPayloadOrRouteWheel(string filter)
    {
        using var fixture = new ServiceFixture();
        var callback = typeof(InputHookService).GetMethod("MouseCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var payload = (IntPtr)1;
        var allocated = IntPtr.Zero;
        var message = filter == "move" ? 0x0200 : filter == "horizontal" ? 0x020E : 0x020A;
        try
        {
            if (filter == "replacement")
                typeof(InputHookService).GetField("_mouseReplacementInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Service, true);
            if (filter == "stopped") fixture.Service.Stop();
            if (filter == "disposed") fixture.Service.Dispose();
            if (filter == "injected")
            {
                allocated = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MSLLHOOKSTRUCT>());
                payload = allocated;
                Marshal.StructureToPtr(new NativeMethods.MSLLHOOKSTRUCT
                {
                    mouseData = MouseData(120), flags = NativeMethods.MouseLlFlags.LLMHF_INJECTED
                }, payload, false);
            }
            Assert.Equal(IntPtr.Zero, callback.Invoke(fixture.Service,
                [filter == "negative" ? -1 : 0, (IntPtr)message, payload]));
            Assert.Equal(0, fixture.Pending);
            Assert.Empty(fixture.Transitions);
        }
        finally
        {
            if (allocated != IntPtr.Zero) Marshal.FreeHGlobal(allocated);
        }
    }

    [Fact]
    public async Task MouseCallback_VerticalPhysicalWheel_ReachesRealDispatcher()
    {
        using var fixture = new ServiceFixture();
        var payload = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MSLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(new NativeMethods.MSLLHOOKSTRUCT { mouseData = MouseData(120) }, payload, false);
            var callback = typeof(InputHookService).GetMethod("MouseCallback", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal((IntPtr)1, callback.Invoke(fixture.Service, [0, (IntPtr)0x020A, payload]));
            await fixture.Drain();
            Assert.Equal(new[] { (Key.E, true), (Key.E, false) }, fixture.Transitions);
        }
        finally { Marshal.FreeHGlobal(payload); }
    }

    private static uint MouseData(int delta) => unchecked((uint)(ushort)(short)delta << 16);

    private sealed class ServiceFixture : IDisposable
    {
        internal readonly Profile Profile = new()
        {
            Name = "Game", Executable = "game.exe",
            AltMouse = { IsEnabled = true, WheelUpKey = Key.F, WheelDownKey = Key.G },
            CombinedMappings =
            {
                IsEnabled = true,
                Mappings =
                [
                    new() { Source = InputTrigger.FromWheel(MouseWheelDirection.Up), TargetKey = Key.E },
                    new() { Source = InputTrigger.FromWheel(MouseWheelDirection.Down), TargetKey = Key.Q }
                ]
            }
        };
        internal readonly RecordingInputSender Sender;
        internal readonly InputHookService Service;
        internal long NowTicks;
        internal GestureChordStateMachine Gestures => Service.GetGesturesForTesting();
        internal int Pending => (int)typeof(GestureChordStateMachine).GetField("_pendingWheelTaps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Gestures)!;
        internal (Key, bool)[] Transitions => Sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray();

        internal ServiceFixture(bool blockWorker = false, bool blockFirstDown = false, Func<int, bool>? keyState = null)
        {
            Sender = new RecordingInputSender(blockDummy: blockWorker, blockFirstDown: blockFirstDown);
            Service = new InputHookService(new NullLoggerService(), Sender, () => Volatile.Read(ref NowTicks), keyState ?? (_ => false));
            Service.StartInputExecutorForTesting();
            Service.ConfigureActiveProfileForTesting(Profile, 1, altPressed: false);
        }

        internal bool Wheel(int delta) => Service.DispatchDecodedMouseEvent(0x020A, MouseData(delta));
        internal void Alt(bool down) => Service.DispatchDecodedKeyboardEvent(0xA4, down, !down);
        internal void Right(bool down) => Service.DispatchDecodedMouseEvent(down ? NativeMethods.WM_RBUTTONDOWN : NativeMethods.WM_RBUTTONUP, 0);
        internal async Task Drain() => Assert.True(await Service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));

        internal void Boundary(string kind)
        {
            switch (kind)
            {
                case "alt": Alt(true); Alt(false); break;
                case "right": Right(true); Right(false); break;
                case "mode": Service.AdvancedModeEnabled = true; Service.AdvancedModeEnabled = false; break;
                case "mapping":
                    Profile.CombinedMappings.IsEnabled = false;
                    Service.ReconcileProfileSettings(Profile, ProfileChangeKind.CombinedMappings);
                    Profile.CombinedMappings.IsEnabled = true;
                    Service.ReconcileProfileSettings(Profile, ProfileChangeKind.CombinedMappings);
                    break;
                case "altMouse": Service.ReconcileProfileSettings(Profile, ProfileChangeKind.AltMouse); break;
                case "identity": Service.ReconcileProfileSettings(Profile, ProfileChangeKind.Identity); break;
                case "master":
                    Profile.IsEnabled = false;
                    Service.ReconcileProfileSettings(Profile, ProfileChangeKind.Master);
                    Profile.IsEnabled = true;
                    Service.ActivateProfile(Profile, 1);
                    break;
                case "removed":
                    Service.ReconcileProfileSettings(Profile, ProfileChangeKind.Removed);
                    Service.ActivateProfile(Profile, 1);
                    break;
                case "foreground":
                    Service.SetForegroundIdentity(IntPtr.Zero, 0, "game.exe", 2);
                    Service.ActivateProfile(Profile, 2);
                    break;
                case "profile":
                    Service.ActivateProfile(new Profile { Name = "Other", Executable = "other.exe" }, 1);
                    Service.ActivateProfile(Profile, 1);
                    break;
                case "release": Service.ReleaseForegroundState(); break;
                case "reset": Service.ResetInputStateForTesting(); break;
                case "rederive": Gestures.RederivePhysicalState(_ => false); break;
            }
        }

        public void Dispose()
        {
            Sender.ReleaseDummy.Set();
            Sender.ReleaseDown.Set();
            Service.StopInputExecutorForTesting();
            Service.Dispose();
        }
    }
}
