using System.Reflection;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using sWinShortcuts.Utilities;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class KeyCaptureGateTests
{
    private const int VK_A = 0x41;
    private const int VK_B = 0x42;
    private const int VK_C = 0x43;

    [Fact]
    public void TryHandle_SwallowedDown_OwnsItsUp()
    {
        var sink = new RecordingSink();
        var gate = Begun(sink);

        Assert.True(gate.TryHandle(VK_A, isDown: true, isUp: false, now: 1));
        Assert.True(gate.TryHandle(VK_A, isDown: false, isUp: true, now: 2));
        Assert.Equal(new (int, bool)[] { (VK_A, true), (VK_A, false) }, sink.Edges.ToArray());
    }

    [Fact]
    public void TryHandle_PreheldKeyUp_PassesThroughUnforwarded()
    {
        var sink = new RecordingSink();
        var gate = Begun(sink);

        Assert.False(gate.TryHandle(VK_A, isDown: false, isUp: true, now: 1));
        Assert.Empty(sink.Edges);
        Assert.True(gate.IsActive);
    }

    [Fact]
    public void TryHandle_PreheldKeyRepeatsThenUp_RepeatsSwallowedUnownedAndUpPassesThrough()
    {
        var sink = new RecordingSink();
        var gate = new KeyCaptureGate();
        gate.Begin(sink, now: 0, isKeyDown: vk => vk == VK_A);

        // Windows auto-repeats a key held when Assign was clicked: keep the repeats from the features.
        Assert.True(gate.TryHandle(VK_A, isDown: true, isUp: false, now: 1));
        Assert.True(gate.TryHandle(VK_A, isDown: true, isUp: false, now: 2));

        // Windows saw the original down, so the up must reach it. The sink sees it to clear its own held flag.
        Assert.False(gate.TryHandle(VK_A, isDown: false, isUp: true, now: 3));
        Assert.Equal(new (int, bool)[] { (VK_A, false) }, sink.Edges.ToArray());
        Assert.True(gate.IsActive);
    }

    [Fact]
    public void TryHandle_PreheldKeyReleasedThenPressedAgain_IsOwnedLikeAnyKey()
    {
        var sink = new RecordingSink();
        var gate = new KeyCaptureGate();
        gate.Begin(sink, now: 0, isKeyDown: vk => vk == VK_A);
        gate.TryHandle(VK_A, isDown: false, isUp: true, now: 1);

        Assert.True(gate.TryHandle(VK_A, isDown: true, isUp: false, now: 2));
        Assert.True(gate.TryHandle(VK_A, isDown: false, isUp: true, now: 3));
        Assert.Equal(new (int, bool)[] { (VK_A, false), (VK_A, true), (VK_A, false) }, sink.Edges.ToArray());
    }

    [Fact]
    public void TryHandle_PreheldModifierWithChord_OnlyChordKeysOwned()
    {
        const int VK_LSHIFT = 0xA0;
        var accumulatorSink = new AccumulatorSink(preheldVks: [VK_LSHIFT]);
        var gate = new KeyCaptureGate();
        gate.Begin(accumulatorSink, now: 0, isKeyDown: vk => vk == VK_LSHIFT);

        Assert.True(gate.TryHandle(VK_LSHIFT, isDown: true, isUp: false, now: 1));
        Assert.True(gate.TryHandle(VK_A, isDown: true, isUp: false, now: 2));
        Assert.True(gate.TryHandle(VK_LSHIFT, isDown: true, isUp: false, now: 3));
        Assert.False(gate.TryHandle(VK_LSHIFT, isDown: false, isUp: true, now: 4));
        Assert.True(gate.TryHandle(VK_A, isDown: false, isUp: true, now: 5));

        Assert.False(gate.IsActive);
        Assert.True(accumulatorSink.Accumulator.Result.IsCompleted);
        Assert.Equal(ModifierKeys.None, accumulatorSink.Accumulator.Result.Chord.Modifiers);
    }

    [Fact]
    public void End_ClearsPreheldSnapshot_NextCaptureOwnsTheKey()
    {
        var sink = new RecordingSink();
        var gate = new KeyCaptureGate();
        gate.Begin(sink, now: 0, isKeyDown: vk => vk == VK_A);
        gate.End();

        gate.Begin(sink, now: 0);

        Assert.True(gate.TryHandle(VK_A, isDown: true, isUp: false, now: 1));
        Assert.True(gate.TryHandle(VK_A, isDown: false, isUp: true, now: 2));
    }

    [Fact]
    public void TryHandle_FinishedWithNothingOwned_GoesInactive()
    {
        var sink = new RecordingSink { FinishOnFirstUp = true };
        var gate = Begun(sink);

        gate.TryHandle(VK_A, isDown: true, isUp: false, now: 1);
        Assert.True(gate.TryHandle(VK_A, isDown: false, isUp: true, now: 2));
        Assert.False(gate.IsActive);
        Assert.False(gate.TryHandle(VK_B, isDown: true, isUp: false, now: 3));
    }

    [Fact]
    public void TryHandle_FinishedWhileKeyOwned_DrainsOwnedUpsOnly()
    {
        var sink = new RecordingSink { FinishOnFirstUp = true };
        var gate = Begun(sink);
        gate.TryHandle(VK_A, isDown: true, isUp: false, now: 1);
        gate.TryHandle(VK_B, isDown: true, isUp: false, now: 1);

        Assert.True(gate.TryHandle(VK_A, isDown: false, isUp: true, now: 2));
        Assert.True(gate.IsDraining);
        var forwarded = sink.Edges.Count;

        // A new key reaches the features; the owned key's repeat and up stay swallowed and are not forwarded.
        Assert.False(gate.TryHandle(VK_C, isDown: true, isUp: false, now: 3));
        Assert.True(gate.TryHandle(VK_B, isDown: true, isUp: false, now: 3));
        Assert.False(gate.TryHandle(VK_C, isDown: false, isUp: true, now: 4));
        Assert.True(gate.TryHandle(VK_B, isDown: false, isUp: true, now: 4));
        Assert.Equal(forwarded, sink.Edges.Count);
        Assert.False(gate.IsActive);
    }

    [Fact]
    public void TryHandle_IdleDeadlineWithNothingPressed_FailsOpen()
    {
        var sink = new RecordingSink();
        var gate = Begun(sink, now: 0);

        Assert.False(gate.TryHandle(VK_A, isDown: true, isUp: false, now: KeyCaptureGate.IdleLimitTicks + 1));
        Assert.False(gate.IsActive);
        Assert.Empty(sink.Edges);
    }

    [Fact]
    public void TryHandle_AfterFirstPress_IdleDeadlineNoLongerApplies()
    {
        var sink = new RecordingSink();
        var gate = Begun(sink, now: 0);
        gate.TryHandle(VK_A, isDown: true, isUp: false, now: 1);

        Assert.True(gate.TryHandle(VK_A, isDown: false, isUp: true, now: KeyCaptureGate.IdleLimitTicks + 5));
        Assert.True(gate.IsActive);
    }

    [Fact]
    public void TryHandle_HardDeadline_FailsOpenEvenWhileKeysAreHeld()
    {
        var sink = new RecordingSink();
        var gate = Begun(sink, now: 0);
        gate.TryHandle(VK_A, isDown: true, isUp: false, now: 1);

        Assert.False(gate.TryHandle(VK_A, isDown: false, isUp: true, now: KeyCaptureGate.HardLimitTicks + 1));
        Assert.False(gate.IsActive);
    }

    [Fact]
    public void End_ClearsOwnership_LaterUpPassesThrough()
    {
        var sink = new RecordingSink();
        var gate = Begun(sink);
        gate.TryHandle(VK_A, isDown: true, isUp: false, now: 1);

        gate.End();

        Assert.False(gate.IsActive);
        Assert.False(gate.TryHandle(VK_A, isDown: false, isUp: true, now: 2));
    }

    [Fact]
    public void EndWithSink_OtherSink_IsNoOp()
    {
        var sink = new RecordingSink();
        var gate = Begun(sink);

        gate.End(new RecordingSink());
        Assert.True(gate.IsActive);

        gate.End(sink);
        Assert.False(gate.IsActive);
    }

    [Theory]
    [InlineData(256, true, false)]
    [InlineData(-1, true, false)]
    [InlineData(VK_A, false, false)]
    public void TryHandle_OutOfRangeOrNoEdge_IsIgnored(int vk, bool isDown, bool isUp)
    {
        var sink = new RecordingSink();
        var gate = Begun(sink);

        Assert.False(gate.TryHandle(vk, isDown, isUp, now: 1));
        Assert.Empty(sink.Edges);
        Assert.True(gate.IsActive);
    }

    [Fact]
    public void TryBeginKeyCapture_NeverStartedService_ReturnsFalse()
    {
        using var service = InputHookServiceTestExtensions.CreateWithFakeForeground(new NullLoggerService(), new RecordingInputSender());

        Assert.False(service.TryBeginKeyCapture(new RecordingSink()));
        Assert.False(GateOf(service).IsActive);
    }

    [Fact]
    public void ReleaseAllState_EndsActiveCapture()
    {
        using var service = InputHookServiceTestExtensions.CreateWithFakeForeground(new NullLoggerService(), new RecordingInputSender());
        var gate = GateOf(service);
        service.StartInputExecutorForTesting();
        try
        {
            gate.Begin(new RecordingSink(), now: 0);

            service.ResetInputStateForTesting();

            Assert.False(gate.IsActive);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void DispatchDecodedKeyboardEvent_LoneUpAfterCaptureEnds_ChangesNoToggleState()
    {
        // A key whose down the capture swallowed reaches the features as a lone up once the capture ends.
        using var service = InputHookServiceTestExtensions.CreateWithFakeForeground(new NullLoggerService(), new RecordingInputSender());
        var toggles = 0;
        service.ColorVariantToggleRequested += (_, _) => toggles++;
        service.SetColorToggleKey(Key.F8);
        var vk = KeyInterop.VirtualKeyFromKey(Key.F8);

        Assert.False(service.DispatchDecodedKeyboardEvent(vk, isKeyDown: false, isKeyUp: true));
        Assert.Equal(0, toggles);

        // The next real press still toggles exactly once.
        Assert.False(service.DispatchDecodedKeyboardEvent(vk, isKeyDown: true, isKeyUp: false));
        Assert.False(service.DispatchDecodedKeyboardEvent(vk, isKeyDown: false, isKeyUp: true));
        Assert.Equal(1, toggles);
    }

    [Fact]
    public void RemapHandleKeyboardEvent_LoneUpOfMappedSource_SendsNothing()
    {
        var profile = new Profile
        {
            Name = "Game",
            Executable = "game.exe",
            CombinedMappings =
            {
                IsEnabled = true,
                Mappings = [new CombinedMappingEntry { Source = InputTrigger.FromKey(Key.E), TargetKey = Key.F }]
            }
        };
        var runtime = new InputRuntimeState();
        runtime.SetRunning(true);
        runtime.SetAdvancedMode(true);
        runtime.SetActiveProfile(profile, foregroundGeneration: 1);
        runtime.SetForegroundIdentity(IntPtr.Zero, 0, profile.NormalizedExecutable, foregroundGeneration: 1);
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var remaps = new RemapStateMachine(runtime, queue, random, new NullLoggerService());

        Assert.False(remaps.HandleKeyboardEvent(KeyInteropUtilities.ToVirtualKey(Key.E), isKeyDown: false, isKeyUp: true,
            rightButtonPressed: false));
        Assert.Empty(queue.Commands);
    }

    private static KeyCaptureGate Begun(IKeyCaptureSink sink, long now = 0)
    {
        var gate = new KeyCaptureGate();
        gate.Begin(sink, now);
        return gate;
    }

    private static KeyCaptureGate GateOf(InputHookService service) =>
        (KeyCaptureGate)typeof(InputHookService)
            .GetField("_keyCapture", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;

    private sealed class AccumulatorSink(int[] preheldVks) : IKeyCaptureSink
    {
        public KeyChordAccumulator Accumulator { get; } = CreateAccumulator(preheldVks);

        public KeyCaptureStatus OnKey(int virtualKey, bool isDown) =>
            isDown ? Accumulator.OnKeyDown(virtualKey) : Accumulator.OnKeyUp(virtualKey);

        private static KeyChordAccumulator CreateAccumulator(int[] preheldVks)
        {
            var accumulator = new KeyChordAccumulator();
            accumulator.Begin(preheldVks);
            return accumulator;
        }
    }

    private sealed class RecordingSink : IKeyCaptureSink
    {
        public List<(int Vk, bool IsDown)> Edges { get; } = [];
        public bool FinishOnFirstUp { get; init; }

        public KeyCaptureStatus OnKey(int virtualKey, bool isDown)
        {
            Edges.Add((virtualKey, isDown));
            return FinishOnFirstUp && !isDown ? KeyCaptureStatus.Finished : KeyCaptureStatus.Pending;
        }
    }
}
