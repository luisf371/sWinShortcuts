using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using sWinShortcuts.ViewModels;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class MacroCollapseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingTriplet_DefaultCollapsed_PreservesRawZeroDuration(bool mouse)
    {
        var rows = mouse
            ? new[] { Mouse(MacroStepKind.MouseDown), Wait(0), Mouse(MacroStepKind.MouseUp) }
            : Press(Key.A, 0);
        using var macro = Create(rows);

        var representative = Assert.Single(macro.VisibleSteps);
        Assert.True(macro.CollapseSteps);
        Assert.Same(macro.Steps[0], representative);
        Assert.True(representative.IsCollapsedPress);
        Assert.Equal(3, representative.SourceStepCount);
        Assert.Same(macro.Steps[1], representative.CollapsedWait);
        Assert.Equal("0 ms hold", representative.DisplayTimingText);
        Assert.Equal(mouse ? "Mouse press" : "Key press", representative.DisplayActionLabel);
        Assert.Equal("Steps 1 to 3", representative.AutomationStepLabel);
        Assert.Equal(rows, macro.ToDefinition().Steps);
        Assert.Equal(3, macro.RecordingInsertionIndex);
    }

    [Fact]
    public void NonmatchingOrMalformedSequences_KeepEverySourceVisible()
    {
        MacroStep[][] sequences =
        [
            [KeyRow(MacroStepKind.KeyDown), Wait(5), KeyRow(MacroStepKind.KeyUp, Key.B)],
            [Mouse(MacroStepKind.MouseDown), Wait(5), Mouse(MacroStepKind.MouseUp, MouseButton.Right)],
            [KeyRow(MacroStepKind.KeyDown), KeyRow(MacroStepKind.KeyUp)],
            [KeyRow(MacroStepKind.KeyDown), Wait(5)],
            [KeyRow(MacroStepKind.KeyDown), Wait(5), Wait(5), KeyRow(MacroStepKind.KeyUp)],
            [Mouse(MacroStepKind.MouseDown), Wait(5), new() { Kind = MacroStepKind.MoveTo }, Mouse(MacroStepKind.MouseUp)],
            [KeyRow(MacroStepKind.KeyDown), Wait(5), KeyRow(MacroStepKind.KeyDown, Key.B), Wait(5),
                KeyRow(MacroStepKind.KeyUp), Wait(5), KeyRow(MacroStepKind.KeyUp, Key.B)],
            [KeyRow(MacroStepKind.KeyDown, Key.None), Wait(5), KeyRow(MacroStepKind.KeyUp, Key.None)],
            [new() { Kind = MacroStepKind.MouseDown }, Wait(5), new() { Kind = MacroStepKind.MouseUp }],
            [Mouse(MacroStepKind.MouseDown), Mouse(MacroStepKind.MouseDown), Wait(5), Mouse(MacroStepKind.MouseUp)]
        ];
        foreach (var rows in sequences)
        {
            using var macro = Create(rows);
            Assert.Equal(rows.Length, macro.VisibleSteps.Count);
            Assert.All(macro.VisibleSteps, row => Assert.False(row.IsCollapsedPress));
            Assert.Equal(rows, macro.ToDefinition().Steps);
        }
    }

    [Fact]
    public void CollapseToggle_PreservesSourceAndNormalizesSelection_WithoutPublishing()
    {
        using var macro = Create(Press(Key.A));
        var original = macro.Steps;
        var models = macro.ToDefinition().Steps;
        var changes = 0;
        macro.Changed += (_, _) => changes++;
        macro.CollapseSteps = false;
        macro.SelectedStep = original[1];

        macro.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MacroViewModel.VisibleSteps)) macro.SelectedStep = null;
        };
        macro.CollapseSteps = true;

        Assert.Same(original, macro.Steps);
        Assert.Same(original[0], macro.SelectedStep);
        Assert.Equal(models, macro.ToDefinition().Steps);
        Assert.Equal(0, changes);
        Assert.True(macro.ExpandSelectedStepCommand.CanExecute(null));
        macro.ExpandSelectedStepCommand.Execute(null);
        Assert.False(macro.CollapseSteps);
        Assert.Same(original[0], macro.SelectedStep);
        Assert.Equal(3, macro.VisibleSteps.Count);
        Assert.Equal("Step 1", macro.SelectedStep!.AutomationStepLabel);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void InvalidWaitText_CorrectionKeepsGroupedEditorAndSelection()
    {
        using var macro = Create([.. Press(Key.A, 25), Wait(77)]);
        var visible = macro.VisibleSteps;
        var representative = macro.SelectedStep!;
        var wait = representative.CollapsedWait!;
        var notifications = new List<string?>();
        representative.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        wait.DurationText = "letters";

        Assert.True(macro.HasFormatError);
        Assert.Equal(25, macro.ToDefinition().Steps[1].DurationMs);
        Assert.Equal(2, macro.ProblemStepNumber);
        Assert.Equal(wait.Error, representative.DisplayError);
        Assert.NotEmpty(representative.DisplayError);
        Assert.Contains(nameof(MacroStepViewModel.DisplayError), notifications);
        macro.SelectedStep = macro.Steps[3];
        macro.ShowProblemStepCommand.Execute(null);
        Assert.True(macro.CollapseSteps);
        Assert.Same(representative, macro.SelectedStep);
        Assert.Equal(0, macro.SelectedIndex);

        foreach (var text in new[] { "", "1", "12", "120", "-1", "0" })
        {
            wait.DurationText = text;
            Assert.Same(visible, macro.VisibleSteps);
            Assert.Same(representative, macro.SelectedStep);
            Assert.Same(wait, representative.CollapsedWait);
            Assert.Equal(text, wait.DurationText);
            Assert.Equal(wait.Error, representative.DisplayError);
        }
        Assert.False(macro.HasFormatError);
        Assert.Empty(representative.DisplayError);
        Assert.Equal("0 ms hold", representative.DisplayTimingText);
    }

    [Fact]
    public void AddAndRecord_AfterCollapsedPress_UseItsRawEndIndex()
    {
        using var macro = Create([.. Press(Key.A), Wait(77)]);
        Assert.Equal("after step 3", macro.InsertionHint);
        var insertionIndex = macro.RecordingInsertionIndex;
        macro.InsertRecording(insertionIndex, [KeyRow(MacroStepKind.KeyPress, Key.B)]);

        Assert.Equal(new[] { MacroStepKind.KeyDown, MacroStepKind.Wait, MacroStepKind.KeyUp,
            MacroStepKind.KeyPress, MacroStepKind.Wait }, macro.Steps.Select(row => row.Kind));
        Assert.Equal(3, macro.SelectedIndex);
        macro.SelectedStep = macro.Steps[0];
        macro.AddStepCommand.Execute(MacroStepKind.Wait);
        Assert.Equal(3, macro.SelectedIndex);
        Assert.Equal(MacroStepKind.KeyUp, macro.Steps[2].Kind);
        Assert.Equal(MacroStepKind.Wait, macro.Steps[3].Kind);
        Assert.Equal(Key.B, macro.Steps[4].Key);
    }

    [Fact]
    public void DuplicateAndDelete_GroupedPress_OperateOnAllSourcesOnce()
    {
        using var macro = Create([.. Press(Key.A), Wait(77)]);
        var original = macro.Steps.ToArray();
        var changes = 0;
        macro.Changed += (_, _) => changes++;
        macro.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MacroViewModel.VisibleSteps)) macro.SelectedStep = null;
        };

        macro.DuplicateStepCommand.Execute(null);

        Assert.Equal(7, macro.Steps.Count);
        Assert.Equal(3, macro.VisibleSteps.Count);
        Assert.Equal(3, macro.SelectedIndex);
        Assert.True(macro.SelectedStep!.IsCollapsedPress);
        Assert.Equal(1, changes);
        for (var i = 0; i < 3; i++)
        {
            Assert.Same(original[i], macro.Steps[i]);
            Assert.NotSame(original[i], macro.Steps[i + 3]);
            Assert.Equal(original[i].ToModel(), macro.Steps[i + 3].ToModel());
        }

        macro.DeleteStepCommand.Execute(null);

        Assert.Equal(4, macro.Steps.Count);
        Assert.Equal(original, macro.Steps);
        Assert.Same(original[3], macro.SelectedStep);
        Assert.Equal(2, changes);
        macro.SelectedStep = original[0];
        macro.DeleteStepCommand.Execute(null);
        var afterDelete = changes;
        original[1].DurationText = "99";
        Assert.Equal(afterDelete, changes);
    }

    [Fact]
    public void MoveStep_GroupOrSingleton_CrossesWholeVisibleNeighbor()
    {
        using var macro = Create([.. Press(Key.A), Wait(77), .. Press(Key.B), KeyRow(MacroStepKind.KeyPress, Key.C)]);
        var original = macro.Steps.ToArray();
        macro.SelectedStep = original[4];
        macro.MoveStepUpCommand.Execute(null);
        Assert.Equal(new[] { original[0], original[1], original[2], original[4], original[5], original[6], original[3], original[7] }, macro.Steps);
        macro.MoveStepUpCommand.Execute(null);
        Assert.Equal(new[] { original[4], original[5], original[6], original[0], original[1], original[2], original[3], original[7] }, macro.Steps);
        Assert.False(macro.MoveStepUpCommand.CanExecute(null));
        macro.MoveStepDownCommand.Execute(null);
        macro.SelectedStep = original[3];
        macro.MoveStepUpCommand.Execute(null);
        Assert.Equal(original, macro.Steps);

        macro.SelectedStep = original[4];
        macro.MoveStepDownCommand.Execute(null);
        Assert.Same(original[4], macro.SelectedStep);
        Assert.False(macro.MoveStepDownCommand.CanExecute(null));
        Assert.Equal(new[] { original[0], original[1], original[2], original[3], original[7], original[4], original[5], original[6] }, macro.Steps);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void DuplicateStep_CollapsedPress_RequiresThreeRawSlots(int freeSlots, bool canDuplicate)
    {
        using var macro = Create([.. Press(Key.A), .. Enumerable.Repeat(Wait(1), MacroValidation.MaxSteps - freeSlots - 3)]);
        var before = macro.Steps.Count;
        Assert.Equal(canDuplicate, macro.DuplicateStepCommand.CanExecute(null));

        macro.DuplicateStepCommand.Execute(null);

        Assert.Equal(canDuplicate ? before + 3 : before, macro.Steps.Count);
        Assert.True(macro.Steps.Count <= MacroValidation.MaxSteps);
    }

    [Fact]
    public void SetAllWaitTimes_RefreshesGroupedTimingOnce_WithoutReplacingVisibleRows()
    {
        using var macro = Create([.. Press(Key.A, 5), Wait(77), .. Press(Key.B, 10)]);
        var visible = macro.VisibleSteps;
        var first = macro.Steps[0];
        var second = macro.Steps[4];
        var changes = 0;
        var timingChanges = 0;
        macro.Changed += (_, _) => changes++;
        first.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MacroStepViewModel.DisplayTimingText)) timingChanges++; };
        second.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MacroStepViewModel.DisplayTimingText)) timingChanges++; };

        macro.SetAllWaitTimes(0);

        Assert.Same(visible, macro.VisibleSteps);
        Assert.Equal(1, changes);
        Assert.Equal(2, timingChanges);
        Assert.Equal("0 ms hold", first.DisplayTimingText);
        Assert.Equal("0 ms hold", second.DisplayTimingText);
        Assert.All(macro.Steps.Where(row => row.Kind == MacroStepKind.Wait), row => Assert.Equal("0", row.DurationText));
        macro.SetAllWaitTimes(0);
        Assert.Equal(1, changes);
        Assert.Equal(2, timingChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedPressTarget_UpdatesBothEndpointsOnce_AndPreservesInvalidHold(bool mouse)
    {
        var rows = mouse
            ? new[] { Mouse(MacroStepKind.MouseDown), Wait(25), Mouse(MacroStepKind.MouseUp) }
            : Press(Key.A, 25);
        using var macro = Create(rows);
        var selected = macro.SelectedStep!;
        var wait = selected.CollapsedWait!;
        wait.DurationText = "letters";
        var visible = macro.VisibleSteps;
        var changes = 0;
        var notifications = new List<string?>();
        macro.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        macro.Changed += (_, _) =>
        {
            changes++;
            Assert.Equal(macro.Steps[0].Key, macro.Steps[2].Key);
            Assert.Equal(macro.Steps[0].MouseButton, macro.Steps[2].MouseButton);
        };

        if (mouse) macro.SelectedPressMouseButton = MouseButton.Right;
        else macro.SelectedPressKey = Key.B;

        Assert.Equal(1, changes);
        Assert.Same(visible, macro.VisibleSteps);
        Assert.Same(selected, macro.SelectedStep);
        Assert.Equal("letters", wait.DurationText);
        Assert.Equal(wait.Error, selected.DisplayError);
        if (mouse)
        {
            Assert.Equal(MouseButton.Right, macro.Steps[0].MouseButton);
            Assert.Equal(MouseButton.Right, macro.SelectedPressMouseButton);
            Assert.Contains(nameof(MacroViewModel.SelectedPressMouseButton), notifications);
            macro.SelectedPressMouseButton = MouseButton.Right;
            macro.SelectedPressMouseButton = null;
            macro.SelectedPressMouseButton = (MouseButton)999;
            macro.SelectedPressKey = Key.B;
        }
        else
        {
            Assert.Equal(Key.B, macro.Steps[0].Key);
            Assert.Equal(Key.B, macro.SelectedPressKey);
            Assert.Contains(nameof(MacroViewModel.SelectedPressKey), notifications);
            macro.SelectedPressKey = Key.B;
            macro.SelectedPressKey = Key.None;
            macro.SelectedPressMouseButton = MouseButton.Right;
        }
        Assert.Equal(1, changes);
        macro.CollapseSteps = false;
        macro.SelectedPressKey = Key.C;
        macro.SelectedPressMouseButton = MouseButton.Middle;
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SelectedPressTarget_SelectionNotifiesBindings_AndReadOnlyEditsAreIgnored()
    {
        var canEdit = true;
        using var macro = new MacroViewModel(new MacroDefinition { ShortcutKey = Key.F6, Steps = [.. Press(Key.A), .. Press(Key.B)] }, () => canEdit);
        var notifications = new List<string?>();
        macro.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        macro.SelectedStep = macro.Steps[3];

        Assert.Equal(Key.B, macro.SelectedPressKey);
        Assert.Contains(nameof(MacroViewModel.SelectedPressKey), notifications);
        Assert.Contains(nameof(MacroViewModel.SelectedPressMouseButton), notifications);
        Assert.Equal("Steps 4 to 6", macro.SelectedStep!.AutomationStepLabel);
        canEdit = false;
        macro.SelectedPressKey = Key.C;
        Assert.Equal(Key.B, macro.Steps[3].Key);
        Assert.Equal(Key.B, macro.Steps[5].Key);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverlappingHold_WithInvalidWait_KeepsEverySourceVisible(bool mouse)
    {
        var down = mouse ? Mouse(MacroStepKind.MouseDown) : KeyRow(MacroStepKind.KeyDown);
        var up = mouse ? Mouse(MacroStepKind.MouseUp) : KeyRow(MacroStepKind.KeyUp);
        using var macro = Create([down, down, Wait(25), up, up]);

        Assert.Equal(5, macro.VisibleSteps.Count);
        macro.Steps[2].DurationText = "letters";

        Assert.Equal(5, macro.VisibleSteps.Count);
        Assert.All(macro.VisibleSteps, row => Assert.False(row.IsCollapsedPress));
        Assert.Equal(3, macro.ProblemStepNumber);
        macro.ShowProblemStepCommand.Execute(null);
        Assert.True(macro.CollapseSteps);
        Assert.Same(macro.Steps[2], macro.SelectedStep);
    }

    private static MacroViewModel Create(MacroStep[] rows) => new(new MacroDefinition { ShortcutKey = Key.F6, Steps = rows }, () => true);
    private static MacroStep[] Press(Key key, int durationMs = 5) => [KeyRow(MacroStepKind.KeyDown, key), Wait(durationMs), KeyRow(MacroStepKind.KeyUp, key)];
    private static MacroStep KeyRow(MacroStepKind kind, Key key = Key.A) => new() { Kind = kind, Key = key };
    private static MacroStep Mouse(MacroStepKind kind, MouseButton button = MouseButton.Left) => new() { Kind = kind, MouseButton = button };
    private static MacroStep Wait(int durationMs) => new() { Kind = MacroStepKind.Wait, DurationMs = durationMs };
}
