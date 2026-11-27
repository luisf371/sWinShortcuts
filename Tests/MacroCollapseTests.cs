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
        Assert.Equal(MacroPressGroupPart.ClosedHead, representative.PressGroupPart);
        Assert.Same(macro.Steps[0], representative);
        Assert.True(representative.IsCollapsedPress);
        Assert.Equal(3, representative.SourceStepCount);
        Assert.Same(macro.Steps[1], representative.CollapsedWait);
        Assert.Equal("0 ms hold", representative.DisplayTimingText);
        Assert.Equal(mouse ? "Mouse click" : "Key press", representative.DisplayActionLabel);
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
            [KeyRow(MacroStepKind.KeyDown), new() { Kind = MacroStepKind.MouseWheel, WheelDelta = 120 }, KeyRow(MacroStepKind.KeyUp)],
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
        macro.TogglePressGroupCommand.Execute(original[0]);
        macro.SelectedStep = original[1];

        macro.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MacroViewModel.VisibleSteps)) macro.SelectedStep = null;
        };
        macro.TogglePressGroupCommand.Execute(original[0]);

        Assert.Same(original, macro.Steps);
        Assert.Same(original[0], macro.SelectedStep);
        Assert.Equal(models, macro.ToDefinition().Steps);
        Assert.Equal(0, changes);
        Assert.True(macro.ExpandSelectedStepCommand.CanExecute(null));
        macro.ExpandSelectedStepCommand.Execute(null);
        Assert.Equal(MacroPressGroupPart.OpenHead, original[0].PressGroupPart);
        Assert.Same(original[0], macro.SelectedStep);
        Assert.Equal(3, macro.VisibleSteps.Count);
        Assert.Equal("Step 1", macro.SelectedStep!.AutomationStepLabel);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void InvalidWaitText_CorrectionKeepsGroupedEditorAndSelection()
    {
        using var macro = Create([.. Press(Key.A, 25), Wait(77)]);
        macro.SelectedStep = macro.Steps[0];
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
        Assert.Equal("at the end", macro.InsertionHint);
        macro.SelectedStep = macro.Steps[0];
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
        macro.SelectedStep = macro.Steps[0];
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
        macro.SelectedStep = macro.Steps[0];
        var before = macro.Steps.Count;
        Assert.Equal(canDuplicate, macro.DuplicateStepCommand.CanExecute(null));

        macro.DuplicateStepCommand.Execute(null);

        Assert.Equal(canDuplicate ? before + 3 : before, macro.Steps.Count);
        Assert.True(macro.Steps.Count <= MacroValidation.MaxSteps);
    }

    [Fact]
    public void SetWaitTimes_RefreshesGroupedTimingOnce_WithoutReplacingVisibleRows()
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

        macro.SetWaitTimes(MacroWaitScope.AllWaits, 0);

        Assert.Same(visible, macro.VisibleSteps);
        Assert.Equal(1, changes);
        Assert.Equal(2, timingChanges);
        Assert.Equal("0 ms hold", first.DisplayTimingText);
        Assert.Equal("0 ms hold", second.DisplayTimingText);
        Assert.All(macro.Steps.Where(row => row.Kind == MacroStepKind.Wait), row => Assert.Equal("0", row.DurationText));
        macro.SetWaitTimes(MacroWaitScope.AllWaits, 0);
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
        macro.TogglePressGroupCommand.Execute(macro.Steps[0]);
        macro.SelectedPressKey = Key.C;
        macro.SelectedPressMouseButton = MouseButton.Middle;
        Assert.Equal(1, changes);
    }

    [Fact]
    public void SelectedPressTarget_SelectionNotifiesBindings_AndReadOnlyEditsAreIgnored()
    {
        var canEdit = true;
        using var macro = new MacroViewModel(new MacroDefinition { ShortcutKey = Key.F6, Steps = [.. Press(Key.A), .. Press(Key.B)] }, () => canEdit);
        macro.SelectedStep = macro.Steps[0];
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
        Assert.Same(macro.Steps[2], macro.SelectedStep);
    }

    [Theory]
    [InlineData(MacroStepKind.KeyPress, MacroStepKind.KeyDown, MacroStepKind.KeyUp, "Key press")]
    [InlineData(MacroStepKind.MouseClick, MacroStepKind.MouseDown, MacroStepKind.MouseUp, "Mouse click")]
    public void AddStep_Press_InsertsClosedGroupWith50MsHold(MacroStepKind kind, MacroStepKind down, MacroStepKind up, string label)
    {
        using var macro = Create([new() { Kind = MacroStepKind.MoveTo, X = 300, Y = 400 }]);

        macro.AddStepCommand.Execute(kind);

        Assert.Equal(new[] { MacroStepKind.MoveTo, down, MacroStepKind.Wait, up }, macro.Steps.Select(row => row.Kind));
        Assert.Equal(50, macro.Steps[2].DurationMs);
        var head = macro.SelectedStep!;
        Assert.Same(macro.Steps[1], head);
        Assert.Equal(MacroPressGroupPart.ClosedHead, head.PressGroupPart);
        Assert.Equal(label, head.DisplayActionLabel);
        Assert.Equal("50 ms hold", head.DisplayTimingText);
        // A Mouse click clicks where the cursor is: none of its rows takes the earlier position.
        Assert.All(macro.Steps.Skip(1), row => Assert.Equal((0, 0), (row.X, row.Y)));
        Assert.Equal(2, macro.VisibleSteps.Count);
    }

    [Fact]
    public void InsertStep_InsertsKeyPressGroup()
    {
        using var macro = Create([]);

        macro.InsertStepCommand.Execute(null);

        Assert.Equal(new[] { MacroStepKind.KeyDown, MacroStepKind.Wait, MacroStepKind.KeyUp }, macro.Steps.Select(row => row.Kind));
        Assert.Equal((Key.A, 50, Key.A), (macro.Steps[0].Key, macro.Steps[1].DurationMs, macro.Steps[2].Key));
        Assert.True(macro.SelectedStep!.IsCollapsedPress);
    }

    [Fact]
    public void AddStep_TwoFreeSteps_GroupItemsDisabled()
    {
        using var macro = Create([.. Enumerable.Repeat(Wait(1), MacroValidation.MaxSteps - 2)]);

        Assert.False(macro.AddStepCommand.CanExecute(MacroStepKind.KeyPress));
        Assert.False(macro.AddStepCommand.CanExecute(MacroStepKind.MouseClick));
        Assert.False(macro.InsertStepCommand.CanExecute(null));
        Assert.True(macro.AddStepCommand.CanExecute(MacroStepKind.Wait));
        Assert.True(macro.CanAddStep);
        // A direct call does nothing instead of overflowing the step limit.
        macro.AddStepCommand.Execute(MacroStepKind.KeyPress);
        macro.InsertStepCommand.Execute(null);
        Assert.Equal(MacroValidation.MaxSteps - 2, macro.Steps.Count);
        macro.AddStepCommand.Execute(MacroStepKind.Wait);
        Assert.Equal(MacroValidation.MaxSteps - 1, macro.Steps.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void CollapseSelected_FromWaitOrUp_ClosesGroupAndSelectsHead(int offset)
    {
        using var macro = Create([.. Press(Key.A), Wait(77)]);
        macro.TogglePressGroupCommand.Execute(macro.Steps[0]);
        macro.SelectedStep = macro.Steps[offset];
        Assert.Same(macro.Steps[offset], macro.SelectedStep);

        Assert.Same(macro.CollapseSelectedStepCommand, sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.Left, ModifierKeys.None));
        Assert.Null(sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.Right, ModifierKeys.None));
        macro.CollapseSelectedStepCommand.Execute(null);

        Assert.Same(macro.Steps[0], macro.SelectedStep);
        Assert.Equal(MacroPressGroupPart.ClosedHead, macro.Steps[0].PressGroupPart);
        Assert.Same(macro.ExpandSelectedStepCommand, sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.Right, ModifierKeys.None));
        Assert.Null(sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.Left, ModifierKeys.None));
        // Plain rows keep the list's own Left and Right behavior.
        macro.SelectedStep = macro.Steps[3];
        Assert.Null(sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.Right, ModifierKeys.None));
        Assert.Null(sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.Left, ModifierKeys.None));
    }

    [Fact]
    public void AddStep_KeyPressInsideHeldA_ErrorNamesEarlierRelease()
    {
        using var macro = Create(Press(Key.A, 500));
        macro.TogglePressGroupCommand.Execute(macro.Steps[0]);
        macro.SelectedStep = macro.Steps[1];

        macro.AddStepCommand.Execute(MacroStepKind.KeyPress);

        Assert.Equal("Step 6: a Key up must follow a matching Key down. Step 5 already released A.", macro.ValidationMessage);
        Assert.Equal(6, macro.ProblemStepNumber);
    }

    [Fact]
    public void AddStep_MouseClickInsideHeldLeft_ErrorsAtNewDown()
    {
        using var macro = Create([Mouse(MacroStepKind.MouseDown), Wait(500), Mouse(MacroStepKind.MouseUp)]);
        macro.TogglePressGroupCommand.Execute(macro.Steps[0]);
        macro.SelectedStep = macro.Steps[1];

        macro.AddStepCommand.Execute(MacroStepKind.MouseClick);

        Assert.Equal("Step 3: a mouse button already held by this macro cannot be pressed again.", macro.ValidationMessage);
    }

    [Fact]
    public void TogglePressGroup_OpensOnlyThatGroup()
    {
        using var macro = Create([.. Press(Key.A), .. Press(Key.B)]);

        Assert.False(macro.TogglePressGroupCommand.CanExecute(macro.Steps[1]));
        macro.TogglePressGroupCommand.Execute(macro.Steps[3]);

        Assert.Equal(new[] { MacroPressGroupPart.ClosedHead, MacroPressGroupPart.None, MacroPressGroupPart.None,
            MacroPressGroupPart.OpenHead, MacroPressGroupPart.OpenMiddle, MacroPressGroupPart.OpenLast }, macro.Steps.Select(row => row.PressGroupPart));
        Assert.Equal(4, macro.VisibleSteps.Count);
        Assert.Equal(Press(Key.A).Concat(Press(Key.B)), macro.ToDefinition().Steps);
        macro.TogglePressGroupCommand.Execute(macro.Steps[3]);
        Assert.Equal(2, macro.VisibleSteps.Count);
    }

    [Fact]
    public void ExpandAllAndCollapseAll_OpenAndCloseEveryGroup()
    {
        using var macro = Create([.. Press(Key.A), Wait(9), .. Press(Key.B)]);
        Assert.False(macro.CollapseAllCommand.CanExecute(null));

        macro.ExpandAllCommand.Execute(null);

        Assert.Equal(7, macro.VisibleSteps.Count);
        Assert.False(macro.ExpandAllCommand.CanExecute(null));
        Assert.True(macro.CollapseAllCommand.CanExecute(null));
        macro.CollapseAllCommand.Execute(null);
        Assert.Equal(3, macro.VisibleSteps.Count);
        Assert.All(new[] { macro.Steps[0], macro.Steps[4] }, row => Assert.Equal(MacroPressGroupPart.ClosedHead, row.PressGroupPart));
    }

    [Fact]
    public void OpenGroup_InsertBetweenDownAndWait_ShowsPlainSteps()
    {
        using var macro = Create(Press(Key.A));
        macro.TogglePressGroupCommand.Execute(macro.Steps[0]);
        macro.SelectedStep = macro.Steps[0];

        macro.AddStepCommand.Execute(MacroStepKind.Wait);

        Assert.Equal(4, macro.VisibleSteps.Count);
        Assert.All(macro.Steps, row => Assert.Equal(MacroPressGroupPart.None, row.PressGroupPart));
    }

    [Fact]
    public void OpenGroup_MoveWaitOutAndBack_ReformsOpen()
    {
        using var macro = Create([.. Press(Key.A), Wait(77)]);
        var down = macro.Steps[0];
        macro.TogglePressGroupCommand.Execute(down);
        macro.SelectedStep = macro.Steps[1];

        macro.MoveStepDownCommand.Execute(null);
        Assert.All(macro.Steps, row => Assert.Equal(MacroPressGroupPart.None, row.PressGroupPart));
        macro.MoveStepUpCommand.Execute(null);

        Assert.Equal(MacroPressGroupPart.OpenHead, down.PressGroupPart);
        Assert.Equal(4, macro.VisibleSteps.Count);
    }

    [Fact]
    public void CollapseAll_ForgetsBrokenGroup_ReformsClosed()
    {
        using var macro = Create([.. Press(Key.A), Wait(77)]);
        var down = macro.Steps[0];
        macro.TogglePressGroupCommand.Execute(down);
        macro.SelectedStep = macro.Steps[1];
        macro.MoveStepDownCommand.Execute(null);

        macro.CollapseAllCommand.Execute(null);
        macro.MoveStepUpCommand.Execute(null);

        Assert.Equal(MacroPressGroupPart.ClosedHead, down.PressGroupPart);
        Assert.Equal(2, macro.VisibleSteps.Count);
    }

    [Fact]
    public void OpenGroup_WithTwoFreeSteps_RefreshesDuplicateAvailability()
    {
        using var macro = Create([.. Press(Key.A), .. Enumerable.Repeat(Wait(1), MacroValidation.MaxSteps - 5)]);
        macro.SelectedStep = macro.Steps[0];
        Assert.False(macro.DuplicateStepCommand.CanExecute(null));
        var raised = 0;
        macro.DuplicateStepCommand.CanExecuteChanged += (_, _) => raised++;

        macro.ExpandSelectedStepCommand.Execute(null);

        Assert.True(raised > 0);
        Assert.True(macro.DuplicateStepCommand.CanExecute(null));
    }

    [Fact]
    public void ShiftAroundPress_ShowsPlainShiftRowsAndClosedInnerGroup()
    {
        using var macro = Create([KeyRow(MacroStepKind.KeyDown, Key.LeftShift), .. Press(Key.A), KeyRow(MacroStepKind.KeyUp, Key.LeftShift)]);

        Assert.Equal(new[] { macro.Steps[0], macro.Steps[1], macro.Steps[4] }, macro.VisibleSteps);
        Assert.Equal(new[] { MacroPressGroupPart.None, MacroPressGroupPart.ClosedHead, MacroPressGroupPart.None, MacroPressGroupPart.None,
            MacroPressGroupPart.None }, macro.Steps.Select(row => row.PressGroupPart));
    }

    [Fact]
    public void RecordedDrag_IsPlainButCountsAsPressHold()
    {
        // A recorded drag moves the cursor before its release, so it never groups.
        using var macro = Create([Mouse(MacroStepKind.MouseDown), Wait(40), new() { Kind = MacroStepKind.MoveTo, X = 5, Y = 5 }, Mouse(MacroStepKind.MouseUp)]);

        Assert.Equal(4, macro.VisibleSteps.Count);
        Assert.True(macro.Steps[1].IsPressHold);
        Assert.Equal(1, macro.PressHoldCount);
    }

    [Fact]
    public void ActionOptions_ExcludesKeyPressAndMouseClick()
    {
        Assert.Equal(new[] { MacroStepKind.KeyDown, MacroStepKind.KeyUp, MacroStepKind.MouseDown, MacroStepKind.MouseUp, MacroStepKind.MoveTo,
            MacroStepKind.SavePosition, MacroStepKind.MouseWheel, MacroStepKind.FreezeMouse, MacroStepKind.UnfreezeMouse, MacroStepKind.Wait },
            MacroViewModel.ActionOptions.Select(option => option.Key));
    }

    [Fact]
    public void OldRow_ShowsOldLabelAndReplaceHelp()
    {
        using var macro = Create([KeyRow(MacroStepKind.KeyPress), new() { Kind = MacroStepKind.MouseClick, MouseButton = MouseButton.Left, X = 1, Y = 2 }]);

        Assert.Equal("Old key press", macro.Steps[0].ActionLabel);
        Assert.Equal("Old mouse click", macro.Steps[1].ActionLabel);
        Assert.StartsWith("This old step no longer plays.", macro.Steps[0].KindHelp);
        Assert.EndsWith("Add a Move to its position first.", macro.Steps[1].KindHelp);
        Assert.Equal("Step 1: replace this old Key press step.", macro.ValidationMessage);
        Assert.Equal(macro.ValidationMessage, macro.Steps[0].DisplayError);
    }

    [Fact]
    public void NewlyLoadedMacro_AllGroupsClosed()
    {
        var profile = sWinShortcuts.Factories.ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new() { ShortcutKey = Key.F6, Steps = [.. Press(Key.A), .. Press(Key.B)] }];
        using var editor = new MacrosViewModel(profile, () => { });

        var macro = editor.SelectedMacro!;

        Assert.Equal(2, macro.VisibleSteps.Count);
        Assert.All(macro.VisibleSteps, row => Assert.Equal(MacroPressGroupPart.ClosedHead, row.PressGroupPart));
    }

    private static MacroViewModel Create(MacroStep[] rows) => new(new MacroDefinition { ShortcutKey = Key.F6, Steps = rows }, () => true);
    private static MacroStep[] Press(Key key, int durationMs = 5) => [KeyRow(MacroStepKind.KeyDown, key), Wait(durationMs), KeyRow(MacroStepKind.KeyUp, key)];
    private static MacroStep KeyRow(MacroStepKind kind, Key key = Key.A) => new() { Kind = kind, Key = key };
    private static MacroStep Mouse(MacroStepKind kind, MouseButton button = MouseButton.Left) => new() { Kind = kind, MouseButton = button };
    private static MacroStep Wait(int durationMs) => new() { Kind = MacroStepKind.Wait, DurationMs = durationMs };
}
