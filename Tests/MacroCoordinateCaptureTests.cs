using sWinShortcuts.Models;
using sWinShortcuts.ViewModels;
using Xunit;

namespace Tests;

// Coordinate pick and Measure through the view-supplied countdown and ruler, with lambdas as fakes.
public sealed class MacroCoordinateCaptureTests
{
    private readonly List<string> _countdownCaptions = [];
    private readonly List<string> _captured = [];
    private TaskCompletionSource<(int X, int Y)?> _countdown = new();
    private TaskCompletionSource<(int X, int Y)?>? _ruler;
    private bool? _rulerRaw;

    private MacroViewModel Create(params MacroStep[] steps)
    {
        var macro = new MacroViewModel(new MacroDefinition { Steps = steps }, () => true);
        macro.ConfigureCoordinateCapture(
            (caption, token) =>
            {
                _countdownCaptions.Add(caption);
                token.Register(() => _countdown.TrySetCanceled(token));
                return _countdown.Task;
            },
            _captured.Add,
            (raw, token) =>
            {
                _rulerRaw = raw;
                _ruler = new TaskCompletionSource<(int X, int Y)?>();
                token.Register(() => _ruler.TrySetCanceled(token));
                return _ruler.Task;
            });
        return macro;
    }

    [Fact]
    public async Task PickCoordinates_Runs_ShowsThenClosesCountdown()
    {
        using var macro = Create(new MacroStep { Kind = MacroStepKind.MoveTo, X = 1, Y = 2 });

        var pick = macro.PickCoordinatesCommand.ExecuteAsync(null);
        Assert.True(macro.IsCapturingCoordinates);
        Assert.Equal(["Pick"], _countdownCaptions);
        _countdown.SetResult((1200, 800));
        await pick;

        Assert.False(macro.IsCapturingCoordinates);
        Assert.Equal((1200, 800), (macro.SelectedStep!.X, macro.SelectedStep.Y));
        Assert.Equal(["✓ 1200, 800"], _captured);
        Assert.Equal("Captured (1200, 800).", macro.CoordinatePickStatus);
    }

    [Fact]
    public async Task PickCoordinates_Cancelled_KeepsPosition()
    {
        using var macro = Create(new MacroStep { Kind = MacroStepKind.MoveTo, X = 1, Y = 2 });

        var pick = macro.PickCoordinatesCommand.ExecuteAsync(null);
        macro.CancelCoordinatePick();
        await pick;

        Assert.Equal((1, 2), (macro.SelectedStep!.X, macro.SelectedStep.Y));
        Assert.Empty(macro.CoordinatePickStatus);
        Assert.Empty(_captured);
        Assert.False(macro.IsCapturingCoordinates);
    }

    [Fact]
    public async Task CancelCoordinatePickCommand_WhileRunning_Cancels()
    {
        using var macro = Create(new MacroStep { Kind = MacroStepKind.MoveTo, X = 1, Y = 2 });
        Assert.False(macro.CancelCoordinatePickCommand.CanExecute(null));

        var pick = macro.PickCoordinatesCommand.ExecuteAsync(null);
        Assert.True(macro.CancelCoordinatePickCommand.CanExecute(null));
        macro.CancelCoordinatePickCommand.Execute(null);
        await pick;

        Assert.True(_countdown.Task.IsCanceled);
        Assert.False(macro.CancelCoordinatePickCommand.CanExecute(null));
        Assert.True(macro.PickCoordinatesCommand.CanExecute(null));
    }

    [Fact]
    public async Task PickCoordinates_StepChangesKind_ClearsStatus()
    {
        using var macro = Create(new MacroStep { Kind = MacroStepKind.MoveTo, X = 1, Y = 2 });
        var step = macro.SelectedStep!;

        var pick = macro.PickCoordinatesCommand.ExecuteAsync(null);
        Assert.NotEmpty(macro.CoordinatePickStatus);
        step.ActionChoice = MacroStepKind.KeyPress;
        _countdown.SetResult((1200, 800));
        await pick;

        Assert.Empty(macro.CoordinatePickStatus);
        Assert.Empty(_captured);
    }

    [Fact]
    public async Task MeasureOffset_AfterCountdown_OpensRuler()
    {
        using var macro = Create(new MacroStep { Kind = MacroStepKind.MoveMouseBy, X = 3, Y = 4 });

        var measure = macro.MeasureOffsetCommand.ExecuteAsync(null);
        Assert.Equal(["Start"], _countdownCaptions);
        Assert.Null(_rulerRaw);
        _countdown.SetResult((500, 400));
        await WaitForRulerAsync();

        Assert.True(_rulerRaw);
        _ruler!.SetResult(null);
        await measure;
    }

    [Fact]
    public async Task MeasureOffset_Captured_FillsTheOffset()
    {
        using var macro = Create(new MacroStep { Kind = MacroStepKind.MoveCursorBy, X = 3, Y = 4 });

        var measure = macro.MeasureOffsetCommand.ExecuteAsync(null);
        _countdown.SetResult((500, 400));
        await WaitForRulerAsync();
        Assert.False(_rulerRaw);
        _ruler!.SetResult((240, -35));
        await measure;

        Assert.Equal((240, -35), (macro.SelectedStep!.X, macro.SelectedStep.Y));
        Assert.Equal(("240", "-35"), (macro.SelectedStep.XText, macro.SelectedStep.YText));
        Assert.Equal("Measured (+240, -35).", macro.CoordinatePickStatus);
        Assert.False(macro.IsCapturingCoordinates);
    }

    [Fact]
    public async Task MeasureOffset_CancelledDuringCountdown_NeverOpensRuler()
    {
        using var macro = Create(new MacroStep { Kind = MacroStepKind.MoveCursorBy, X = 3, Y = 4 });

        var measure = macro.MeasureOffsetCommand.ExecuteAsync(null);
        macro.CancelCoordinatePickCommand.Execute(null);
        await measure;

        Assert.Null(_rulerRaw);
        Assert.Equal((3, 4), (macro.SelectedStep!.X, macro.SelectedStep.Y));
    }

    [Fact]
    public async Task MeasureOffset_CancelledInRuler_LeavesTheOffset()
    {
        using var macro = Create(new MacroStep { Kind = MacroStepKind.MoveCursorBy, X = 3, Y = 4 });

        var measure = macro.MeasureOffsetCommand.ExecuteAsync(null);
        _countdown.SetResult((500, 400));
        await WaitForRulerAsync();
        _ruler!.SetResult(null);
        await measure;

        Assert.Equal((3, 4), (macro.SelectedStep!.X, macro.SelectedStep.Y));
        Assert.Empty(macro.CoordinatePickStatus);
    }

    [Fact]
    public async Task MeasureOffset_SelectionChanged_DoesNotWriteToTheOldStep()
    {
        using var macro = Create(
            new MacroStep { Kind = MacroStepKind.MoveCursorBy, X = 3, Y = 4 },
            new MacroStep { Kind = MacroStepKind.MoveCursorBy, X = 5, Y = 6 });
        var first = macro.Steps[0];
        macro.SelectedStep = first;

        var measure = macro.MeasureOffsetCommand.ExecuteAsync(null);
        _countdown.SetResult((500, 400));
        await WaitForRulerAsync();
        // A ruler that ignores the cancellation still finishes; its result must not land anywhere.
        var ruler = _ruler!;
        macro.SelectedStep = macro.Steps[1];
        ruler.TrySetResult((240, -35));
        await measure;

        Assert.Equal((3, 4), (first.X, first.Y));
        Assert.Equal((5, 6), (macro.Steps[1].X, macro.Steps[1].Y));
    }

    [Fact]
    public async Task MeasureOffset_WhilePicking_IsDisabled()
    {
        using var macro = Create(
            new MacroStep { Kind = MacroStepKind.MoveTo, X = 1, Y = 2 },
            new MacroStep { Kind = MacroStepKind.MoveCursorBy, X = 3, Y = 4 });
        macro.SelectedStep = macro.Steps[0];

        var pick = macro.PickCoordinatesCommand.ExecuteAsync(null);
        macro.Steps[0].ActionChoice = MacroStepKind.MoveTo;
        macro.Steps[0].MoveIsOffset = true;
        Assert.True(macro.SelectedStep!.HasOffset);
        Assert.False(macro.MeasureOffsetCommand.CanExecute(null));
        macro.CancelCoordinatePick();
        await pick;

        Assert.True(macro.MeasureOffsetCommand.CanExecute(null));
    }

    [Fact]
    public async Task PickCoordinates_WhileMeasuring_IsDisabled()
    {
        using var macro = Create(new MacroStep { Kind = MacroStepKind.MoveCursorBy, X = 3, Y = 4 });

        var measure = macro.MeasureOffsetCommand.ExecuteAsync(null);
        macro.SelectedStep!.MoveIsPosition = true;
        Assert.True(macro.SelectedStep.HasCoordinates);
        Assert.False(macro.PickCoordinatesCommand.CanExecute(null));
        macro.CancelCoordinatePick();
        await measure;

        Assert.True(macro.PickCoordinatesCommand.CanExecute(null));
    }

    [Fact]
    public void MeasureOffset_WithoutAView_IsDisabled()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [new MacroStep { Kind = MacroStepKind.MoveCursorBy }]
        }, () => true);

        Assert.False(macro.MeasureOffsetCommand.CanExecute(null));
    }

    // The ruler opens after the countdown's continuation runs.
    private async Task WaitForRulerAsync()
    {
        for (var i = 0; i < 200 && _ruler is null; i++) await Task.Delay(5);
        Assert.NotNull(_ruler);
    }
}
