using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using sWinShortcuts.Utilities;
using Tests.Fakes;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

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

        // Windows saw the original down, so the up must reach it. The sink never hears about the held key.
        Assert.False(gate.TryHandle(VK_A, isDown: false, isUp: true, now: 3));
        Assert.Empty(sink.Edges);
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
        Assert.Equal(new (int, bool)[] { (VK_A, true), (VK_A, false) }, sink.Edges.ToArray());
    }

    [Fact]
    public void TryHandle_PreheldModifierWithChord_OnlyChordKeysOwned()
    {
        const int VK_LSHIFT = 0xA0;
        // Hook captures give the accumulator no held-key snapshot: the gate alone filters held keys.
        var accumulatorSink = new AccumulatorSink(preheldVks: []);
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
    public void TryHandle_FinishedWhileKeyOwned_EndsAndLaterEdgesPassThrough()
    {
        var sink = new RecordingSink { FinishOnFirstUp = true };
        var gate = Begun(sink);
        gate.TryHandle(VK_A, isDown: true, isUp: false, now: 1);
        gate.TryHandle(VK_B, isDown: true, isUp: false, now: 1);

        Assert.True(gate.TryHandle(VK_A, isDown: false, isUp: true, now: 2));
        Assert.False(gate.IsActive);
        var forwarded = sink.Edges.Count;

        // The box's apply would end the capture a moment later anyway: the still-held key's up is a lone up.
        Assert.False(gate.TryHandle(VK_B, isDown: false, isUp: true, now: 3));
        Assert.False(gate.TryHandle(VK_C, isDown: true, isUp: false, now: 3));
        Assert.Equal(forwarded, sink.Edges.Count);
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
    public void TryHandle_MouseStartsCapture_IdleDeadlineDoesNotDropChord()
    {
        const int VK_LSHIFT = 0xA0;
        var sink = new AccumulatorSink(preheldVks: []);
        var gate = Begun(sink);
        var afterIdleDeadline = KeyCaptureGate.IdleLimitTicks + 1;
        Assert.Equal(KeyCaptureStatus.Pending, sink.Accumulator.OnMouseDown(MouseButton.Middle));

        Assert.True(gate.TryHandle(VK_LSHIFT, isDown: true, isUp: false, now: afterIdleDeadline));
        Assert.Equal(KeyCaptureStatus.Pending, sink.Accumulator.OnMouseUp(MouseButton.Middle));
        Assert.True(gate.TryHandle(VK_LSHIFT, isDown: false, isUp: true, now: afterIdleDeadline + 1));

        Assert.Equal(KeyChordResult.Completed(KeyChord.FromTrigger(InputTrigger.FromMouseButton(MouseButton.Middle),
            ModifierKeys.Shift)), sink.Accumulator.Result);
        Assert.False(gate.IsActive);
    }

    [Fact]
    public void TryHandle_PauseDown_CompletesWithoutWaitingForUp()
    {
        const int VK_PAUSE = 0x13;
        var sink = new AccumulatorSink(preheldVks: []);
        var gate = Begun(sink);

        Assert.True(gate.TryHandle(VK_PAUSE, isDown: true, isUp: false, now: 1));

        Assert.Equal(KeyChordResult.Completed(KeyChord.FromKey(Key.Pause)), sink.Accumulator.Result);
        Assert.False(gate.IsActive);
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
    public void DispatchDecodedMouseEvent_ActiveCapture_PassesThroughExistingMacroShortcut()
    {
        using var service = MacroPlaybackTests.Create(new RecordingInputSender(), out var profile,
            MacroPresses.Press(Key.A));
        profile.Macros.Definitions = [profile.Macros.Definitions[0] with
        {
            ShortcutKey = Key.None,
            ShortcutMouseButton = MouseButton.Middle
        }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        var gate = GateOf(service);
        gate.Begin(new RecordingSink(), Stopwatch.GetTimestamp());

        Assert.False(service.DispatchDecodedMouseEvent(NativeMethods.WM_MBUTTONDOWN, 0));
        Assert.False(service.DispatchDecodedMouseEvent(NativeMethods.WM_MBUTTONUP, 0));
        Assert.Equal(MacroSessionMode.Idle, service.GetMacroSession().Mode);

        gate.End();
        Assert.True(service.DispatchDecodedMouseEvent(NativeMethods.WM_MBUTTONDOWN, 0));
        Assert.True(service.DispatchDecodedMouseEvent(NativeMethods.WM_MBUTTONUP, 0));
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
        public bool HasStarted => Accumulator.HasStarted;

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
        public bool HasStarted { get; private set; }

        public KeyCaptureStatus OnKey(int virtualKey, bool isDown)
        {
            Edges.Add((virtualKey, isDown));
            if (isDown) HasStarted = true;
            return FinishOnFirstUp && !isDown ? KeyCaptureStatus.Finished : KeyCaptureStatus.Pending;
        }
    }
}
