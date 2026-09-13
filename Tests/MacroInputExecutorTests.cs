using System.Windows.Input;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using sWinShortcuts.Utilities;
using System.Drawing;
using Tests.Fakes;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class MacroInputExecutorTests
{
    [Fact]
    public async Task MacroDown_AnotherFeatureOwnsKey_RefusesSharingTheHold()
    {
        var runtime = new InputRuntimeState();
        runtime.SetRunning(true);
        var sender = new RecordingInputSender();
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService(),
            macroInputContext: new PhysicalState());
        executor.Start();

        Assert.True(await Send(executor, new InputCommand(Key.A, true, HoldOwner: InputHoldOwner.HoldBreath)));
        Assert.True(await Send(executor, Reserve()));
        Assert.False(await Send(executor, MacroKey(Key.A, true)));
        Assert.True(await Send(executor, MacroKey(Key.A, false)));
        Assert.True(await Send(executor, new InputCommand(Key.A, false, HoldOwner: InputHoldOwner.HoldBreath)));

        Assert.Equal(new[] { (Key.A, true), (Key.A, false) },
            sender.Transitions.Select(item => (item.Key, item.IsDown)));
        Assert.True(executor.StopAndDrain());
    }

    [Theory]
    [InlineData((int)InputHoldOwner.HoldBreath, false)]
    [InlineData((int)InputHoldOwner.None, false)]
    [InlineData((int)InputHoldOwner.None, true)]
    public async Task ReserveModifiers_ForeignModifierHeldOrPending_RefusesAdmission(int ownerValue, bool failedRelease)
    {
        var owner = (InputHoldOwner)ownerValue;
        var sender = new RecordingInputSender { KeyResult = (_, down, _) => down || !failedRelease };
        using var executor = Create(sender);
        Assert.True(await Send(executor, new InputCommand(Key.LeftCtrl, true, HoldOwner: owner)));
        if (failedRelease)
        {
            Assert.False(await Send(executor, new InputCommand(Key.LeftCtrl, false, HoldOwner: owner)));
        }

        Assert.False(await Send(executor, Reserve()));
        Assert.False(await Send(executor, MacroKey(Key.S, true)));
        Assert.DoesNotContain(sender.Transitions, item => item.Key == Key.S);
    }

    [Theory]
    [InlineData((int)InputCommandKind.KeyTransition)]
    [InlineData((int)InputCommandKind.KeyTap)]
    [InlineData((int)InputCommandKind.Sequence)]
    public async Task ReservedModifiers_ForeignDirectOrOwnedDown_SendsNeitherEdge(int kindValue)
    {
        var kind = (InputCommandKind)kindValue;
        var sender = new RecordingInputSender();
        using var executor = Create(sender);
        Assert.True(await Send(executor, Reserve()));

        Assert.False(await Send(executor, new InputCommand(Key.LeftCtrl, true, Kind: kind,
            Sequence: [new TapStep(Key.LeftCtrl, 0, 0)], HoldOwner: InputHoldOwner.HoldBreath)));
        Assert.Empty(sender.Transitions);

        Assert.True(await Send(executor, MacroKey(Key.LeftCtrl, true)));
        Assert.True(await Send(executor, MacroKey(Key.S, true)));
        Assert.True(await Send(executor, Cleanup()));
        Assert.Equal(new[] { (Key.LeftCtrl, true), (Key.S, true), (Key.S, false), (Key.LeftCtrl, false) },
            sender.Transitions.Select(item => (item.Key, item.IsDown)));
        Assert.All(sender.KeyReleases, item => Assert.True(item.MacroRelease));
    }

    [Fact]
    public async Task RejectedForeignModifierDown_LaterOwnedUpCannotReleaseNewPhysicalHold()
    {
        var physical = new PhysicalState();
        var sender = new RecordingInputSender();
        using var executor = Create(sender, physical);
        Assert.True(await Send(executor, Reserve()));
        Assert.False(await Send(executor, new InputCommand(Key.LeftCtrl, true, HoldOwner: InputHoldOwner.HoldBreath)));
        physical.SetKey(Key.LeftCtrl, isDown: true, takeover: false);
        Assert.True(await Send(executor, new InputCommand(Key.LeftCtrl, false, HoldOwner: InputHoldOwner.HoldBreath)));
        Assert.Empty(sender.Transitions);
        Assert.True(await Send(executor, Cleanup()));
    }

    [Fact]
    public async Task PhysicalModifierArrivesAfterEnqueue_DefersOnlyThatAttemptAndAllowsOwedUp()
    {
        var physical = new PhysicalState();
        var sender = new RecordingInputSender(blockDummy: true);
        using var executor = Create(sender, physical);
        Assert.True(await Send(executor, Reserve()));
        Assert.True(await Send(executor, MacroKey(Key.A, true)));
        var blocked = Send(executor, new InputCommand(Key.None, false, Kind: InputCommandKind.DummyKey));
        Assert.True(sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
        var deferred = new InputCommandAcknowledgement();
        var down = Send(executor, MacroKey(Key.B, true) with { Acknowledgement = deferred });
        physical.ModifiersDown = true;
        sender.ReleaseDummy.Set();
        Assert.True(await blocked);
        Assert.False(await down);
        Assert.True(deferred.DeferredForPhysicalModifiers);
        Assert.False(deferred.DownSent);
        Assert.True(await Send(executor, MacroKey(Key.A, false)));

        physical.ModifiersDown = false;
        var resumed = new InputCommandAcknowledgement();
        Assert.True(await Send(executor, MacroKey(Key.B, true) with { Acknowledgement = resumed }));
        Assert.False(resumed.DeferredForPhysicalModifiers);
        Assert.True(resumed.DownSent);
        Assert.True(await Send(executor, Cleanup()));
    }

    [Theory]
    [InlineData((int)InputCommandKind.MoveTo)]
    [InlineData((int)InputCommandKind.MouseWheel)]
    [InlineData((int)InputCommandKind.MouseButtonTransition)]
    public async Task NewMouseOutput_PhysicalModifiersDeferAndDisposedRuntimeRejects(int kindValue)
    {
        var kind = (InputCommandKind)kindValue;
        var physical = new PhysicalState();
        var sender = new RecordingInputSender();
        var runtime = new InputRuntimeState();
        runtime.SetRunning(true);
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService(), macroInputContext: physical);
        executor.Start();
        Assert.True(await Send(executor, Reserve()));
        physical.ModifiersDown = true;
        var acknowledgement = new InputCommandAcknowledgement();
        var command = new InputCommand(Key.None, kind == InputCommandKind.MouseButtonTransition,
            Kind: kind, HoldOwner: InputHoldOwner.Macro, Token: 1, Button: MouseButton.Left,
            X: -42, Y: 10, WheelDelta: -120, Acknowledgement: acknowledgement);
        Assert.False(await Send(executor, command));
        Assert.True(acknowledgement.DeferredForPhysicalModifiers);

        physical.ModifiersDown = false;
        runtime.TryBeginDispose();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.False(executor.Enqueue(command with { Completion = completion }));
        Assert.False(await completion.Task);
        Assert.True(await Send(executor, Cleanup()));
        Assert.Empty(sender.MouseMoves);
        Assert.Empty(sender.MouseWheels);
        Assert.Empty(sender.MouseTransitions);
    }

    [Fact]
    public async Task CancellationDuringNativeDown_CleanupWaitsForOwnershipAndReleasesOnlyMacro()
    {
        var sender = new RecordingInputSender();
        using var executor = Create(sender);
        Assert.True(await Send(executor, new InputCommand(Key.Z, true, HoldOwner: InputHoldOwner.Caps)));
        Assert.True(await Send(executor, Reserve()));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        sender.KeyResult = (_, down, _) =>
        {
            if (down)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(3)));
            }
            return true;
        };
        var guard = new MutableGuard();
        var down = Send(executor, MacroKey(Key.A, true) with { Guard = guard });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(executor.IsMacroKeyOwned(KeyInteropUtilities.ToVirtualKey(Key.A)));
            guard.Allows = false;
            var cleanup = Send(executor, Cleanup());
            Assert.False(cleanup.IsCompleted);
            release.Set();
            Assert.True(await down);
            Assert.True(await cleanup);
            Assert.False(await Send(executor, MacroKey(Key.B, true) with { Guard = guard }));
            Assert.Equal(new[] { (Key.Z, true), (Key.A, true), (Key.A, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)));
            Assert.True(await Send(executor, new InputCommand(Key.Z, false, HoldOwner: InputHoldOwner.Caps)));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task FailedMacroDown_CreatesNoReleaseAndNativeFailureIsNotDeferral()
    {
        var sender = new RecordingInputSender(failFirstDown: true);
        using var executor = Create(sender);
        Assert.True(await Send(executor, Reserve()));
        var acknowledgement = new InputCommandAcknowledgement();
        Assert.False(await Send(executor, MacroKey(Key.A, true) with { Acknowledgement = acknowledgement }));
        Assert.False(acknowledgement.DeferredForPhysicalModifiers);
        Assert.False(executor.IsMacroKeyOwned(KeyInteropUtilities.ToVirtualKey(Key.A)));
        Assert.True(await Send(executor, MacroKey(Key.A, false)));
        Assert.True(await Send(executor, Cleanup()));
        Assert.Single(sender.Transitions);
        Assert.Empty(sender.KeyReleases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMacroRelease_PhysicalTakeoverClearsRetryWithoutSendingAnotherUp(bool throws)
    {
        var physical = new PhysicalState();
        var sender = new RecordingInputSender
        {
            KeyResult = (_, down, _) => down || (throws ? throw new InvalidOperationException("UP failed") : false)
        };
        using var executor = Create(sender, physical);
        Assert.True(await Send(executor, Reserve()));
        Assert.True(await Send(executor, MacroKey(Key.A, true)));
        Assert.False(await Send(executor, MacroKey(Key.A, false)));
        Assert.True(executor.IsMacroKeyOwned(KeyInteropUtilities.ToVirtualKey(Key.A)));
        physical.SetKey(Key.A, isDown: true, takeover: true);
        Assert.True(await Send(executor, Cleanup()));
        Assert.False(executor.IsMacroKeyOwned(KeyInteropUtilities.ToVirtualKey(Key.A)));
        Assert.Single(sender.KeyReleases);
        Assert.True(await Send(executor, Reserve() with { Token = 2 }));
        Assert.Single(sender.KeyReleases);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MacroUp_OnlyConfirmedPassedThroughHoldPreservesPhysicalInput(bool takeover)
    {
        var physical = new PhysicalState();
        var sender = new RecordingInputSender();
        using var executor = Create(sender, physical);
        Assert.True(await Send(executor, Reserve()));
        Assert.True(await Send(executor, MacroKey(Key.A, true)));
        physical.SetKey(Key.A, isDown: true, takeover);
        Assert.True(await Send(executor, MacroKey(Key.A, false)));
        Assert.True(await Send(executor, MacroKey(Key.A, false)));
        Assert.Equal(takeover ? 0 : 1, sender.KeyReleases.Count);
        Assert.True(await Send(executor, Cleanup()));
    }

    [Fact]
    public async Task FailedCleanup_ReservationAndConflictBlockRemainUntilReleaseRecovers()
    {
        var rejectUp = true;
        var sender = new RecordingInputSender { KeyResult = (_, down, _) => down || !rejectUp };
        using var executor = Create(sender);
        Assert.True(await Send(executor, Reserve()));
        Assert.True(await Send(executor, MacroKey(Key.A, true)));
        Assert.False(await Send(executor, Cleanup()));
        Assert.False(await Send(executor, MacroKey(Key.B, true)));
        Assert.False(await Send(executor, Reserve() with { Token = 2 }));
        Assert.False(await Send(executor, new InputCommand(Key.A, true, HoldOwner: InputHoldOwner.HoldBreath)));
        Assert.False(await Send(executor, new InputCommand(Key.LeftCtrl, true)));
        rejectUp = false;
        Assert.True(await Send(executor, Cleanup()));
        Assert.True(await Send(executor, Reserve() with { Token = 2 }));
        Assert.True(await Send(executor, Cleanup())); // stale cleanup must leave run 2 reserved
        Assert.False(await Send(executor, new InputCommand(Key.LeftCtrl, true)));
        Assert.True(await Send(executor, MacroKey(Key.A, true) with { Token = 2 }));
        Assert.True(await Send(executor, Cleanup() with { Token = 2 }));
    }

    [Fact]
    public async Task RequireAllReleased_FailedForeignReleaseSurvivesMacroCleanup()
    {
        var rejectForeignUp = true;
        var sender = new RecordingInputSender { KeyResult = (key, down, _) => key != Key.Z || down || !rejectForeignUp };
        using var executor = Create(sender);
        Assert.True(await Send(executor, new InputCommand(Key.Z, true, HoldOwner: InputHoldOwner.Caps)));
        Assert.False(await Send(executor, new InputCommand(Key.Z, false, HoldOwner: InputHoldOwner.Caps)));
        Assert.True(await Send(executor, Reserve()));
        Assert.True(await Send(executor, MacroKey(Key.A, true)));
        Assert.True(await Send(executor, Cleanup()));
        Assert.False(await Send(executor, Reserve() with { Token = 2, RequireAllReleased = true }));
        rejectForeignUp = false;
        Assert.True(await Send(executor, Reserve() with { Token = 2, RequireAllReleased = true }));
        Assert.Contains(sender.KeyReleases, item => item.Key == Key.Z && !item.MacroRelease);
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Middle)]
    [InlineData(MouseButton.XButton1)]
    [InlineData(MouseButton.XButton2)]
    public async Task MouseHold_DuplicateDownRefusedAndCleanupRetainsFailedUp(MouseButton button)
    {
        var rejectUp = true;
        var physical = new PhysicalState();
        var sender = new RecordingInputSender { MouseResult = (_, down, _) => down || !rejectUp };
        using var executor = Create(sender, physical);
        Assert.True(await Send(executor, Reserve()));
        var command = new InputCommand(Key.None, true, Kind: InputCommandKind.MouseButtonTransition,
            HoldOwner: InputHoldOwner.Macro, Token: 1, Button: button);
        Assert.True(await Send(executor, command));
        Assert.False(await Send(executor, command));
        Assert.False(await Send(executor, Cleanup()));
        Assert.True(executor.IsMacroMouseOwned(button));
        var attemptedUps = sender.MouseTransitions.Count;
        physical.SetMouse(button, isDown: true, takeover: true);
        Assert.True(await Send(executor, Cleanup()));
        Assert.Equal(attemptedUps, sender.MouseTransitions.Count);
        Assert.False(executor.IsMacroMouseOwned(button));
        Assert.All(sender.MouseTransitions.Where(item => !item.IsDown), item => Assert.True(item.MacroRelease));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMouseDown_CreatesNoReleaseObligation(bool throws)
    {
        var sender = new RecordingInputSender
        {
            MouseResult = (_, _, _) => throws ? throw new InvalidOperationException("DOWN failed") : false
        };
        using var executor = Create(sender);
        Assert.True(await Send(executor, Reserve()));
        Assert.False(await Send(executor, new InputCommand(Key.None, true,
            Kind: InputCommandKind.MouseButtonTransition, HoldOwner: InputHoldOwner.Macro,
            Token: 1, Button: MouseButton.XButton2)));
        Assert.False(executor.IsMacroMouseOwned(MouseButton.XButton2));
        Assert.True(await Send(executor, Cleanup()));
        Assert.Single(sender.MouseTransitions);
    }

    [Fact]
    public async Task ThrowingMouseUp_KeepsRetryOriginUntilPhysicalTakeover()
    {
        var physical = new PhysicalState();
        var sender = new RecordingInputSender
        {
            MouseResult = (_, down, _) => down ? true : throw new InvalidOperationException("UP failed")
        };
        using var executor = Create(sender, physical);
        Assert.True(await Send(executor, Reserve()));
        var command = new InputCommand(Key.None, true, Kind: InputCommandKind.MouseButtonTransition,
            HoldOwner: InputHoldOwner.Macro, Token: 1, Button: MouseButton.Right);
        Assert.True(await Send(executor, command));
        Assert.False(await Send(executor, command with { IsDown = false }));
        Assert.True(executor.IsMacroMouseOwned(MouseButton.Right));
        physical.SetMouse(MouseButton.Right, isDown: true, takeover: true);
        Assert.True(await Send(executor, Cleanup()));
        Assert.Equal(2, sender.MouseTransitions.Count);
    }

    [Fact]
    public async Task RepeatedKeyboardDown_StillOwnsOnlyOneFinalRelease()
    {
        var sender = new RecordingInputSender();
        using var executor = Create(sender);
        Assert.True(await Send(executor, Reserve()));
        Assert.True(await Send(executor, MacroKey(Key.A, true)));
        Assert.True(await Send(executor, MacroKey(Key.A, true)));
        Assert.False(await Send(executor, new InputCommand(Key.A, true, HoldOwner: InputHoldOwner.HoldBreath)));
        Assert.True(await Send(executor, Cleanup()));
        Assert.Equal(new[] { (Key.A, true), (Key.A, true), (Key.A, false) },
            sender.Transitions.Select(item => (item.Key, item.IsDown)));
    }

    [Fact]
    public async Task PhysicalHold_RefusesAffectedDownAndMovementButAllowsOwnedDrag()
    {
        var physical = new PhysicalState();
        physical.SetKey(Key.A, isDown: true, takeover: false);
        physical.SetMouse(MouseButton.Left, isDown: true, takeover: false);
        var sender = new RecordingInputSender();
        using var executor = Create(sender, physical);
        Assert.True(await Send(executor, Reserve()));
        Assert.False(await Send(executor, MacroKey(Key.A, true)));
        var mouse = new InputCommand(Key.None, true, Kind: InputCommandKind.MouseButtonTransition,
            HoldOwner: InputHoldOwner.Macro, Token: 1, Button: MouseButton.Left);
        Assert.False(await Send(executor, mouse));
        var move = new InputCommand(Key.None, false, Kind: InputCommandKind.MoveTo,
            HoldOwner: InputHoldOwner.Macro, Token: 1, X: -42, Y: 8);
        Assert.False(await Send(executor, move));
        physical.SetMouse(MouseButton.Left, isDown: false, takeover: false);
        Assert.True(await Send(executor, mouse));
        Assert.True(await Send(executor, move));
        Assert.True(await Send(executor, new InputCommand(Key.None, false, Kind: InputCommandKind.MouseWheel,
            HoldOwner: InputHoldOwner.Macro, Token: 1, WheelDelta: -60, HorizontalWheel: true)));
        Assert.Equal(new[] { (-42, 8) }, sender.MouseMoves);
        Assert.Equal(new[] { (-60, true) }, sender.MouseWheels);
        Assert.True(await Send(executor, Cleanup()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeGeometryPreparation_PhysicalModifierOrCancellationArrives_RejectsBeforeInsertion(bool cancel)
    {
        var physical = new PhysicalState();
        var guard = new MutableGuard();
        var inserted = 0;
        var sender = new WindowsInputSender(new NullLoggerService(), inputs =>
        {
            inserted += inputs.Length;
            return (uint)inputs.Length;
        }, () =>
        {
            if (cancel) guard.Allows = false;
            else physical.ModifiersDown = true;
            return [new Rectangle(0, 0, 100, 100)];
        });
        var runtime = new InputRuntimeState();
        runtime.SetRunning(true);
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService(), macroInputContext: physical);
        executor.Start();
        Assert.True(await Send(executor, Reserve()));
        var acknowledgement = new InputCommandAcknowledgement();
        Assert.False(await Send(executor, new InputCommand(Key.None, false,
            Kind: InputCommandKind.MoveTo, X: 10, Y: 10, HoldOwner: InputHoldOwner.Macro,
            Token: 1, Guard: guard, Acknowledgement: acknowledgement)));
        Assert.Equal(!cancel, acknowledgement.DeferredForPhysicalModifiers);
        Assert.Equal(0, inserted);
        Assert.True(await Send(executor, Cleanup()));
    }

    [Theory]
    [InlineData((int)InputCommandKind.KeyTransition)]
    [InlineData((int)InputCommandKind.KeyTap)]
    [InlineData((int)InputCommandKind.Sequence)]
    [InlineData((int)InputCommandKind.DummyKey)]
    public async Task RecordingPause_QueuedForeignOutputIsRejectedAndAlreadyOwedUpCompletes(int kindValue)
    {
        var runtime = new InputRuntimeState();
        runtime.SetRunning(true);
        var sender = new RecordingInputSender(blockDummy: true);
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService(),
            macroInputContext: new PhysicalState());
        executor.Start();
        Assert.True(await Send(executor, new InputCommand(Key.A, true, HoldOwner: InputHoldOwner.Caps)));
        var blocker = Send(executor, new InputCommand(Key.None, false, Kind: InputCommandKind.DummyKey));
        Assert.True(sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
        var late = Send(executor, new InputCommand(Key.B, true, Kind: (InputCommandKind)kindValue,
            Sequence: [new TapStep(Key.B, 0, 0)]));
        var release = Send(executor, new InputCommand(Key.A, false, HoldOwner: InputHoldOwner.Caps));
        runtime.BeginMacroInhibition(recording: true);
        sender.ReleaseDummy.Set();
        Assert.True(await blocker);
        Assert.False(await late);
        Assert.True(await release);
        Assert.True(await Send(executor, Reserve() with { RequireAllReleased = true }));
        Assert.Equal(new[] { (Key.A, true), (Key.A, false) },
            sender.Transitions.Select(item => (item.Key, item.IsDown)));
        Assert.Single(sender.DummyThreadIds);
        Assert.True(await Send(executor, Cleanup()));
    }

    [Fact]
    public async Task Shutdown_FinalReleaseRetrySucceeds_RetiresReservationAfterFailedFence()
    {
        var ups = 0;
        var sender = new RecordingInputSender { KeyResult = (_, down, _) => down || ++ups > 1 };
        using var executor = Create(sender);
        Assert.True(await Send(executor, Reserve()));
        Assert.True(await Send(executor, MacroKey(Key.A, true)));
        Assert.False(await Send(executor, Cleanup()));
        Assert.True(executor.IsMacroSessionReserved(1));
        Assert.True(executor.StopAndDrain());
        Assert.False(executor.IsMacroSessionReserved(1));
        Assert.Equal(2, ups);
    }

    private static InputExecutor Create(RecordingInputSender sender, PhysicalState? physical = null)
    {
        var runtime = new InputRuntimeState();
        runtime.SetRunning(true);
        var executor = new InputExecutor(runtime, sender, new NullLoggerService(),
            keyState: _ => false, macroInputContext: physical ?? new PhysicalState());
        executor.Start();
        return executor;
    }

    private static InputCommand Reserve() => new(Key.None, false,
        Kind: InputCommandKind.MacroReserveModifiers, HoldOwner: InputHoldOwner.Macro, Token: 1);

    private static InputCommand Cleanup() => new(Key.None, false,
        Kind: InputCommandKind.MacroCleanup, HoldOwner: InputHoldOwner.Macro, Token: 1);

    private static InputCommand MacroKey(Key key, bool down) => new(key, down,
        HoldOwner: InputHoldOwner.Macro, Token: 1);

    private sealed class MutableGuard : IInputCommandGuard
    {
        internal volatile bool Allows = true;
        public bool CanExecute(in InputCommand command) => Allows;
    }

    private sealed class PhysicalState : IMacroInputContext
    {
        private readonly int[] _keys = new int[256];
        private readonly int[] _keyTakeovers = new int[256];
        private readonly int[] _mouse = new int[6];
        private readonly int[] _mouseTakeovers = new int[6];
        internal volatile bool ModifiersDown;
        public bool PhysicalModifiersDown => ModifiersDown;
        public bool IsPhysicalKeyDown(int virtualKey) => Volatile.Read(ref _keys[virtualKey]) != 0;
        public bool IsPhysicalMouseButtonDown(MouseButton button) => Volatile.Read(ref _mouse[(int)button]) != 0;
        public bool HasPhysicalKeyTakeover(int virtualKey) => Volatile.Read(ref _keyTakeovers[virtualKey]) != 0;
        public bool HasPhysicalMouseTakeover(MouseButton button) => Volatile.Read(ref _mouseTakeovers[(int)button]) != 0;
        internal void SetKey(Key key, bool isDown, bool takeover)
        {
            var vk = KeyInteropUtilities.ToVirtualKey(key);
            Volatile.Write(ref _keys[vk], isDown ? 1 : 0);
            Volatile.Write(ref _keyTakeovers[vk], takeover ? 1 : 0);
        }
        internal void SetMouse(MouseButton button, bool isDown, bool takeover)
        {
            Volatile.Write(ref _mouse[(int)button], isDown ? 1 : 0);
            Volatile.Write(ref _mouseTakeovers[(int)button], takeover ? 1 : 0);
        }
    }

    private static async Task<bool> Send(InputExecutor executor, InputCommand command)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(executor.Enqueue(command with { Completion = completion }));
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
}
