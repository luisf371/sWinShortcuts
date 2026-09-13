using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services.Input;
using Xunit;

namespace Tests;

public sealed class MacroRecorderTests
{
    [Fact]
    public void Capture_OverlappingChord_PreservesEdgesAndGaps()
    {
        var recorder = new MacroRecorder(30, 1000);
        recorder.Begin(100, new bool[256], new bool[6]);
        recorder.Capture(new(100, NativeMethods.WM_KEYDOWN, 0xA2, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(112, NativeMethods.WM_KEYDOWN, 0x43, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(145, NativeMethods.WM_KEYUP, 0x43, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(153, NativeMethods.WM_KEYUP, 0xA2, 0, 0, 0, 0, 0, 0));
        recorder.RequestStop(MacroRecordingEndReason.Stopped);
        var (steps, balanced) = recorder.BuildSteps();
        Assert.False(balanced);
        Assert.Equal(new[] { MacroStepKind.KeyDown, MacroStepKind.Wait, MacroStepKind.KeyDown,
            MacroStepKind.Wait, MacroStepKind.KeyUp, MacroStepKind.Wait, MacroStepKind.KeyUp }, steps.Select(x => x.Kind));
        Assert.Equal(new[] { 12, 33, 8 }, steps.Where(x => x.Kind == MacroStepKind.Wait).Select(x => x.DurationMs));
        Assert.Equal(Key.LeftCtrl, steps[0].Key);
        Assert.Equal(Key.C, steps[2].Key);
    }

    [Fact]
    public void Capture_RowBudget_StopsBeforeUnbalanceAndRetainsPrefix()
    {
        var recorder = new MacroRecorder(3, 1000);
        recorder.Begin(0, new bool[256], new bool[6]);
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, 0x41, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(1, NativeMethods.WM_KEYDOWN, 0x42, 0, 0, 0, 0, 0, 0));
        var (steps, balanced) = recorder.BuildSteps();
        Assert.Equal(MacroRecordingEndReason.RowLimit, recorder.EndReason);
        Assert.True(balanced);
        Assert.Collection(steps, down => Assert.Equal(MacroStepKind.KeyDown, down.Kind),
            up => Assert.Equal(MacroStepKind.KeyUp, up.Kind));
    }

    [Theory]
    [InlineData(1u, sWinShortcuts.Models.MouseButton.XButton1)]
    [InlineData(2u, sWinShortcuts.Models.MouseButton.XButton2)]
    public void Capture_XButton_RetainsIdentityAndSignedPosition(uint buttonData, sWinShortcuts.Models.MouseButton button)
    {
        var recorder = new MacroRecorder(20, 1000);
        recorder.Begin(0, new bool[256], new bool[6]);
        recorder.Capture(new(0, NativeMethods.WM_XBUTTONDOWN, 0, 0, 0, buttonData << 16, -900, 80, 0));
        recorder.RequestStop(MacroRecordingEndReason.Stopped);
        var (steps, _) = recorder.BuildSteps();
        Assert.Equal(-900, steps[0].X);
        Assert.Equal(MacroStepKind.MoveTo, steps[0].Kind);
        Assert.Equal(button, steps[1].MouseButton);
        Assert.Equal(button, steps[2].MouseButton);
    }

    [Fact]
    public void StopControl_RemovesItsDownAndBalancesEarlierHold()
    {
        var recorder = new MacroRecorder(20, 1000);
        recorder.Begin(0, new bool[256], new bool[6]);
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, 0x41, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(10, NativeMethods.WM_LBUTTONDOWN, 0, 0, 0, 0, 30, 40, 0));
        recorder.StopFromControl(NativeMethods.WM_LBUTTONDOWN);
        var (steps, _) = recorder.BuildSteps();
        Assert.Equal(2, steps.Length);
        Assert.All(steps, step => Assert.Equal(Key.A, step.Key));
    }

    [Fact]
    public void Capture_PreheldAndLateEvents_AreExcluded()
    {
        var preheld = new bool[256];
        preheld[0x41] = true;
        var recorder = new MacroRecorder(20, 1000);
        recorder.Begin(0, preheld, new bool[6]);
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, 0x41, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(10, NativeMethods.WM_KEYUP, 0x41, 0, 0, 0, 0, 0, 0));
        recorder.RequestStop(MacroRecordingEndReason.Stopped);
        recorder.Capture(new(20, NativeMethods.WM_KEYDOWN, 0x42, 0, 0, 0, 0, 0, 0));
        Assert.Empty(recorder.BuildSteps().Steps);
    }

    [Fact]
    public void Capture_PreheldReleaseBetweenCapturedEvents_PreservesElapsedGapAndCallerSnapshot()
    {
        var preheld = new bool[256];
        preheld[0x41] = true;
        var recorder = new MacroRecorder(20, 1000);
        recorder.Begin(0, preheld, new bool[6]);
        recorder.Capture(new(10, NativeMethods.WM_KEYDOWN, 0x42, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(20, NativeMethods.WM_KEYUP, 0x41, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(30, NativeMethods.WM_KEYUP, 0x42, 0, 0, 0, 0, 0, 0));
        recorder.RequestStop(MacroRecordingEndReason.Stopped);

        var steps = recorder.BuildSteps().Steps;

        Assert.True(preheld[0x41]);
        Assert.Equal(3, steps.Length);
        Assert.Equal(20, steps[1].DurationMs);
        Assert.Equal(Key.B, steps[0].Key);
        Assert.Equal(Key.B, steps[2].Key);
    }

    [Theory]
    [InlineData(0x2u)]
    [InlineData(0x10u)]
    public void Capture_InjectedKeyboardEventsAndEmergencyKey_AreExcluded(uint injectedFlags)
    {
        var recorder = new MacroRecorder(20, 1000);
        recorder.Begin(0, new bool[256], new bool[6]);
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, 0x41, 0, injectedFlags, 0, 0, 0, 0));
        recorder.Capture(new(1, NativeMethods.WM_KEYUP, 0x41, 0, injectedFlags, 0, 0, 0, 0));
        recorder.Capture(new(2, NativeMethods.WM_KEYDOWN, 0x7B, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(3, NativeMethods.WM_KEYDOWN, 0x7B, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(4, NativeMethods.WM_KEYUP, 0x7B, 0, 0, 0, 0, 0, 0));
        recorder.RequestStop(MacroRecordingEndReason.Stopped);

        Assert.Empty(recorder.BuildSteps().Steps);
    }

    [Theory]
    [InlineData(0x1u)]
    [InlineData(0x2u)]
    public void Capture_InjectedMouseInputAndFreeMovement_AreExcluded(uint injectedFlags)
    {
        var recorder = new MacroRecorder(20, 1000);
        recorder.Begin(0, new bool[256], new bool[6]);
        recorder.Capture(new(0, NativeMethods.WM_LBUTTONDOWN, 0, 0, injectedFlags, 0, 50, 60, 0));
        recorder.Capture(new(1, NativeMethods.WM_LBUTTONUP, 0, 0, injectedFlags, 0, 50, 60, 0));
        recorder.Capture(new(2, NativeMethods.WM_MOUSEWHEEL, 0, 0, injectedFlags, 0, 50, 60, -120));
        recorder.Capture(new(3, NativeMethods.WM_MOUSEMOVE, 0, 0, 0, 0, 50, 60, 0));
        recorder.RequestStop(MacroRecordingEndReason.Stopped);

        Assert.Empty(recorder.BuildSteps().Steps);
    }

    [Theory]
    [InlineData(NativeMethods.WM_MOUSEWHEEL, false, -32768)]
    [InlineData(NativeMethods.WM_MOUSEHWHEEL, true, -120)]
    [InlineData(NativeMethods.WM_MOUSEHWHEEL, true, 32767)]
    public void Capture_WheelEvent_PreservesSignedDeltaDirectionAndCoordinates(int message, bool horizontal, int delta)
    {
        var recorder = new MacroRecorder(20, 1000);
        recorder.Begin(0, new bool[256], new bool[6]);
        recorder.Capture(new(0, message, 0, 0, 0, (uint)(ushort)(short)delta << 16, -200, 75, delta));
        recorder.RequestStop(MacroRecordingEndReason.Stopped);

        var steps = recorder.BuildSteps().Steps;

        Assert.Equal(2, steps.Length);
        Assert.Equal(MacroStepKind.MoveTo, steps[0].Kind);
        Assert.Equal(MacroStepKind.MouseWheel, steps[1].Kind);
        Assert.Equal(delta, steps[1].WheelDelta);
        Assert.Equal(horizontal, steps[1].HorizontalWheel);
        Assert.Equal(-200, steps[0].X);
        Assert.Equal(75, steps[0].Y);
    }

    [Fact]
    public void Capture_InvalidInputMetadata_DoesNotProduceUnsavableRows()
    {
        var recorder = new MacroRecorder(20, 1000);
        recorder.Begin(0, new bool[256], new bool[6]);
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, int.MaxValue, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, -1, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(0, NativeMethods.WM_XBUTTONDOWN, 0, 0, 0, 3u << 16, 0, 0, 0));
        recorder.Capture(new(0, NativeMethods.WM_MOUSEWHEEL, 0, 0, 0, 0, 0, 0, 65536));
        recorder.Capture(new(0, NativeMethods.WM_MOUSEWHEEL, 0, 0, 0, 0, 0, 0, 0));
        recorder.RequestStop(MacroRecordingEndReason.Stopped);

        Assert.Empty(recorder.BuildSteps().Steps);
    }

    [Fact]
    public void Capture_DurationLimit_RetainsBalancedPrefixAndOriginalStopReason()
    {
        var recorder = new MacroRecorder(20, 1000);
        recorder.Begin(100, new bool[256], new bool[6]);
        recorder.Capture(new(599_099, NativeMethods.WM_KEYDOWN, 0x41, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(600_100, NativeMethods.WM_KEYDOWN, 0x42, 0, 0, 0, 0, 0, 0));
        recorder.RequestStop(MacroRecordingEndReason.Interrupted);

        var (steps, balanced) = recorder.BuildSteps();

        Assert.False(recorder.IsCapturing);
        Assert.Equal(MacroRecordingEndReason.DurationLimit, recorder.EndReason);
        Assert.True(balanced);
        Assert.Equal(2, steps.Length);
        Assert.All(steps, step => Assert.Equal(Key.A, step.Key));
    }

    [Fact]
    public void CheckDuration_NoFurtherInput_StopsAtTenMinutes()
    {
        var recorder = new MacroRecorder(20, 1000);
        recorder.Begin(100, new bool[256], new bool[6]);
        recorder.CheckDuration(600_099);
        Assert.True(recorder.IsCapturing);
        recorder.CheckDuration(600_100);
        Assert.False(recorder.IsCapturing);
        Assert.Equal(MacroRecordingEndReason.DurationLimit, recorder.EndReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Capture_InsufficientRowsForBalancedKeyPress_StopsWithEmptyTake(int budget)
    {
        var recorder = new MacroRecorder(budget, 1000);
        recorder.Begin(0, new bool[256], new bool[6]);
        if (budget == 0) Assert.False(recorder.IsCapturing);
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, 0x41, 0, 0, 0, 0, 0, 0));

        Assert.Equal(MacroRecordingEndReason.RowLimit, recorder.EndReason);
        Assert.Empty(recorder.BuildSteps().Steps);
    }

    [Fact]
    public void Capture_RepeatedKeyDown_ReservesOnlyOneBalancingRelease()
    {
        var recorder = new MacroRecorder(4, 1000);
        recorder.Begin(0, new bool[256], new bool[6]);
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, 0x41, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, 0x41, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, 0x41, 0, 0, 0, 0, 0, 0));
        recorder.RequestStop(MacroRecordingEndReason.Stopped);

        var steps = recorder.BuildSteps().Steps;

        Assert.Equal(new[] { MacroStepKind.KeyDown, MacroStepKind.KeyDown, MacroStepKind.KeyDown, MacroStepKind.KeyUp }, steps.Select(step => step.Kind));
    }

    [Theory]
    [InlineData(NativeMethods.WM_KEYDOWN, 0x0D)]
    [InlineData(NativeMethods.WM_SYSKEYDOWN, 0x20)]
    public void StopControl_KeyboardGesture_RemovesOnlyItsMatchedDown(int message, int virtualKey)
    {
        var recorder = new MacroRecorder(20, 1000);
        recorder.Begin(0, new bool[256], new bool[6]);
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, 0x41, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(10, message, virtualKey, 0, 0, 0, 0, 0, 0));
        recorder.StopFromControl(message, virtualKey);

        var steps = recorder.BuildSteps().Steps;

        Assert.Equal(2, steps.Length);
        Assert.All(steps, step => Assert.Equal(Key.A, step.Key));
    }

    [Fact]
    public void StopControl_UnmatchedOrAlreadyLimitedGesture_KeepsEarlierInput()
    {
        var recorder = new MacroRecorder(4, 1000);
        recorder.Begin(0, new bool[256], new bool[6]);
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, 0x20, 0, 0, 0, 0, 0, 0));
        recorder.StopFromControl(NativeMethods.WM_LBUTTONDOWN);
        var unmatched = recorder.BuildSteps().Steps;
        Assert.Equal(2, unmatched.Length);
        Assert.All(unmatched, step => Assert.Equal(Key.Space, step.Key));

        recorder.Begin(0, new bool[256], new bool[6]);
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, 0x20, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(10, NativeMethods.WM_LBUTTONDOWN, 0, 0, 0, 0, 0, 0, 0));
        recorder.StopFromControl(NativeMethods.WM_LBUTTONDOWN);
        Assert.Equal(MacroRecordingEndReason.RowLimit, recorder.EndReason);
        var limited = recorder.BuildSteps().Steps;
        Assert.Equal(2, limited.Length);
        Assert.All(limited, step => Assert.Equal(Key.Space, step.Key));
    }

    [Fact]
    public void Capture_ExactRowBudgetExhausted_FinalizesWithoutAnotherEvent()
    {
        var recorder = new MacroRecorder(2, 1000);
        recorder.Begin(0, new bool[256], new bool[6]);
        recorder.Capture(new(0, NativeMethods.WM_KEYDOWN, 0x41, 0, 0, 0, 0, 0, 0));
        recorder.Capture(new(0, NativeMethods.WM_KEYUP, 0x41, 0, 0, 0, 0, 0, 0));

        Assert.False(recorder.IsCapturing);
        Assert.Equal(MacroRecordingEndReason.RowLimit, recorder.EndReason);
        Assert.Equal(2, recorder.BuildSteps().Steps.Length);
    }

    [Fact]
    public void Capture_KeyboardRepeatHotPath_DoesNotAllocate()
    {
        var recorder = new MacroRecorder(1000, 1000);
        recorder.Begin(0, new bool[256], new bool[6]);
        var input = new RecordedMacroEvent(0, NativeMethods.WM_KEYDOWN, 0x41, 0, 0, 0, 0, 0, 0);
        recorder.Capture(input);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 256; index++) recorder.Capture(input);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        recorder.RequestStop(MacroRecordingEndReason.Stopped);
        Assert.Equal(0, allocated);
        Assert.Equal(258, recorder.BuildSteps().Steps.Length);
    }

    [Fact]
    public async Task RequestStop_RacingPhysicalCapture_PublishesReasonAndBalancedDetachedPrefix()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var recorder = new MacroRecorder(10000, 1000);
            recorder.Begin(0, new bool[256], new bool[6]);
            var input = new RecordedMacroEvent(0, NativeMethods.WM_KEYDOWN, 0x41, 0, 0, 0, 0, 0, 0);
            recorder.Capture(input);
            var finalization = Task.Run(() =>
            {
                recorder.RequestStop(MacroRecordingEndReason.Interrupted);
                Assert.False(recorder.IsCapturing);
                Assert.Equal(MacroRecordingEndReason.Interrupted, recorder.EndReason);
                return recorder.BuildSteps().Steps;
            });
            for (var index = 0; index < 1000; index++) recorder.Capture(input);

            var steps = await finalization;

            Assert.InRange(steps.Length, 2, 1002);
            Assert.Equal(MacroStepKind.KeyUp, steps[^1].Kind);
            Assert.All(steps[..^1], step => Assert.Equal(MacroStepKind.KeyDown, step.Kind));
            var count = recorder.Count;
            recorder.Capture(input);
            Assert.Equal(count, recorder.Count);
        }
    }
}
