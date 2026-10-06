using System.Windows.Input;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using sWinShortcuts.ViewModels;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class MacroBulkWaitTests
{
    [Fact]
    public void WaitEditor_OpensOnPressHolds_ValidatesLocalTextAndAppliesEachScope()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition
        {
            ShortcutKey = Key.F6,
            Steps = [new() { Kind = MacroStepKind.KeyDown, Key = Key.A },
                new() { Kind = MacroStepKind.Wait, DurationMs = 10 },
                new() { Kind = MacroStepKind.KeyUp, Key = Key.A },
                new() { Kind = MacroStepKind.Wait, DurationMs = 40 },
                new() { Kind = MacroStepKind.KeyPress, Key = Key.B, DurationMs = 25 }]
        }];
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        var macro = editor.SelectedMacro!;
        var saved = profile.Macros.Definitions[0];
        macro.ShowWaitEditorCommand.Execute(null);
        Assert.True(macro.IsWaitEditorOpen);
        // Press holds come first with the suggested 50 ms: the Down/Wait/Up hold and the Key press hold.
        Assert.True(macro.IsPressHoldScope);
        Assert.Equal("50", macro.WaitDurationText);
        Assert.Equal("Applies to 2 press holds.", macro.WaitEditMessage);

        foreach (var input in new[] { "", "letters", "-1", "3600001", "3,600,000" })
        {
            macro.WaitDurationText = input;
            Assert.False(macro.ApplyWaitTimesCommand.CanExecute(null));
            macro.ApplyWaitTimesCommand.Execute(null);
            Assert.Equal(input.Length > 0, macro.HasWaitEditError);
            Assert.False(macro.HasFormatError);
            Assert.Same(saved, profile.Macros.Definitions[0]);
            Assert.Equal(0, edits);
        }

        macro.WaitDurationText = "50";
        Assert.True(macro.ApplyWaitTimesCommand.CanExecute(null));
        macro.ApplyWaitTimesCommand.Execute(null);
        Assert.True(macro.IsWaitEditorOpen);
        Assert.True(macro.WaitEditApplied);
        Assert.Equal("Set 2 press holds to 50 ms.", macro.WaitEditMessage);
        Assert.Equal(new[] { 0, 50, 0, 40, 50 }, profile.Macros.Definitions[0].Steps.Select(step => step.DurationMs));
        Assert.Equal(1, edits);

        // A new scope clears the last result and changes only its own rows.
        macro.IsBetweenStepsScope = true;
        Assert.Equal(MacroWaitScope.BetweenSteps, macro.WaitScope);
        Assert.False(macro.IsPressHoldScope);
        Assert.False(macro.WaitEditApplied);
        Assert.Equal("Applies to 1 Wait step between actions.", macro.WaitEditMessage);
        macro.WaitDurationText = "30";
        macro.ApplyWaitTimesCommand.Execute(null);
        Assert.Equal("Set 1 Wait step between actions to 30 ms.", macro.WaitEditMessage);
        Assert.Equal(new[] { 0, 50, 0, 30, 50 }, profile.Macros.Definitions[0].Steps.Select(step => step.DurationMs));
        macro.IsAllWaitsScope = true;
        Assert.Equal("Applies to 2 press holds and 1 Wait step between actions.", macro.WaitEditMessage);
        macro.WaitDurationText = "20";
        macro.ApplyWaitTimesCommand.Execute(null);
        Assert.Equal(new[] { 0, 20, 0, 20, 20 }, profile.Macros.Definitions[0].Steps.Select(step => step.DurationMs));
        Assert.Equal(3, edits);
        // A radio button clearing its own projection does not choose a scope.
        macro.IsAllWaitsScope = false;
        Assert.Equal(MacroWaitScope.AllWaits, macro.WaitScope);

        macro.CloseWaitEditorCommand.Execute(null);
        Assert.False(macro.IsWaitEditorOpen);
        Assert.Equal(string.Empty, macro.WaitDurationText);
        Assert.False(macro.WaitEditApplied);
        Assert.Equal(3, edits);
    }

    [Fact]
    public void WaitEditor_ClosesWhenContextCannotEdit_AndHandlesNoRemainingWaits()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition { Steps = [new() { Kind = MacroStepKind.Wait, DurationMs = 10 }] }, new()];
        using var editor = new MacrosViewModel(profile, () => { });
        var macro = editor.SelectedMacro!;
        var empty = editor.Definitions[1];
        Assert.False(empty.ShowWaitEditorCommand.CanExecute(null));
        empty.ShowWaitEditorCommand.Execute(null);
        Assert.False(empty.IsWaitEditorOpen);

        foreach (var close in new Action[]
        {
            () => editor.SelectedMacro = empty,
            () => editor.SetRecordingDestination(macro),
            () => { profile.IsEnabled = false; editor.RefreshAvailability(); },
            () => { profile.IsPersistenceSuspended = true; editor.RefreshAvailability(); },
            editor.LeaveEditor
        })
        {
            macro.ShowWaitEditorCommand.Execute(null);
            // Without press holds the editor opens on the pauses between actions, with no suggested value.
            Assert.True(macro.IsBetweenStepsScope);
            Assert.Equal(string.Empty, macro.WaitDurationText);
            macro.WaitDurationText = "123";
            close();
            Assert.False(macro.IsWaitEditorOpen);
            Assert.Equal(string.Empty, macro.WaitDurationText);
            Assert.False(macro.ApplyWaitTimesCommand.CanExecute(null));
            editor.SetRecordingDestination(null);
            profile.IsEnabled = true;
            profile.IsPersistenceSuspended = false;
            editor.SelectedMacro = macro;
            editor.RefreshAvailability();
        }

        macro.ShowWaitEditorCommand.Execute(null);
        macro.WaitDurationText = "100";
        macro.DeleteStepCommand.Execute(null);
        Assert.True(macro.IsWaitEditorOpen);
        Assert.Equal("This macro has no Wait steps between actions.", macro.WaitEditMessage);
        Assert.False(macro.ApplyWaitTimesCommand.CanExecute(null));
        macro.IsPressHoldScope = true;
        Assert.Equal("This macro has no press holds.", macro.WaitEditMessage);
        macro.IsAllWaitsScope = true;
        Assert.Equal("This macro has no waits or holds.", macro.WaitEditMessage);
    }

    [Fact]
    public void WaitEditor_CompactPressesOnly_OpensOnTheirHolds()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [new() { Kind = MacroStepKind.KeyPress, Key = Key.A },
                new() { Kind = MacroStepKind.MouseClick, MouseButton = MouseButton.Left, DurationMs = 35 }]
        }, () => true);

        Assert.Equal(0, macro.WaitStepCount);
        Assert.True(macro.ShowWaitEditorCommand.CanExecute(null));
        macro.ShowWaitEditorCommand.Execute(null);

        Assert.True(macro.IsPressHoldScope);
        Assert.Equal("Applies to 2 press holds.", macro.WaitEditMessage);
        macro.ApplyWaitTimesCommand.Execute(null);
        Assert.Equal(new[] { 50, 50 }, macro.ToDefinition().Steps.Select(step => step.DurationMs));
    }

    [Fact]
    public void SetWaitTimes_PressHolds_ChangesRecordedHoldsAndCompactHoldsOnly()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition
        {
            Steps =
            [
                Edge(MacroStepKind.KeyDown, Key.A), Wait(80), Edge(MacroStepKind.KeyUp, Key.A), Wait(200),
                // A recorded click re-anchors the cursor before its release.
                Move(100, 200), Click(MacroStepKind.MouseDown), Wait(90), Move(100, 200), Click(MacroStepKind.MouseUp), Wait(300),
                new() { Kind = MacroStepKind.KeyPress, Key = Key.B },
                // A typematic repeat is not a fresh press, so neither of its waits holds one.
                Edge(MacroStepKind.KeyDown, Key.C), Wait(500), Edge(MacroStepKind.KeyDown, Key.C), Wait(33), Edge(MacroStepKind.KeyUp, Key.C),
                // A press inside another hold keeps its own hold; the outer waits pause between actions.
                Edge(MacroStepKind.KeyDown, Key.D), Wait(70), Edge(MacroStepKind.KeyDown, Key.E), Wait(60), Edge(MacroStepKind.KeyUp, Key.E),
                Wait(40), Edge(MacroStepKind.KeyUp, Key.D)
            ]
        }];
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        var macro = editor.SelectedMacro!;
        var holds = new[] { 1, 6, 19 };

        Assert.Equal(holds, macro.Steps.Select((step, index) => (step, index)).Where(row => row.step.IsPressHold).Select(row => row.index));
        Assert.Equal(4, macro.PressHoldCount);
        Assert.Equal(6, macro.BetweenWaitCount);
        Assert.Equal("80 ms hold", macro.Steps[1].TimingText);
        Assert.Equal("200 ms", macro.Steps[3].TimingText);

        macro.SetWaitTimes(MacroWaitScope.PressHolds, 50);

        Assert.Equal(new[] { 0, 50, 0, 200, 0, 0, 50, 0, 0, 300, 50, 0, 500, 0, 33, 0, 0, 70, 0, 50, 0, 40, 0 },
            profile.Macros.Definitions[0].Steps.Select(step => step.DurationMs));
        Assert.Equal(1, edits);
        Assert.Equal("50 ms hold", macro.Steps[19].TimingText);
    }

    [Fact]
    public void SetWaitTimes_BetweenSteps_UpdatesOnlyPauses_PublishesOnceAndKeepsSelection()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition
        {
            Steps = [new() { Kind = MacroStepKind.Wait, DurationMs = 10 },
                new() { Kind = MacroStepKind.KeyPress, Key = Key.A, DurationMs = 25 },
                new() { Kind = MacroStepKind.MouseClick, MouseButton = MouseButton.Left, DurationMs = 35 },
                new() { Kind = MacroStepKind.Wait, DurationMs = 40 }]
        }];
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        var macro = editor.SelectedMacro!;
        var selected = macro.Steps[1];
        macro.SelectedStep = selected;

        macro.SetWaitTimes(MacroWaitScope.BetweenSteps, 100);

        Assert.Equal(new[] { 100, 25, 35, 100 }, profile.Macros.Definitions[0].Steps.Select(step => step.DurationMs));
        Assert.Equal(2, macro.WaitStepCount);
        Assert.Same(selected, macro.SelectedStep);
        Assert.Equal(1, edits);
        macro.SetWaitTimes(MacroWaitScope.BetweenSteps, 100);
        Assert.Equal(1, edits);
    }

    [Fact]
    public void SetWaitTimes_ThousandRows_IsOneEditAndAcceptsExistingBoundaries()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition
        {
            Steps = Enumerable.Repeat(new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 20 }, MacroValidation.MaxSteps).ToArray()
        }];
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        var macro = editor.SelectedMacro!;

        macro.SetWaitTimes(MacroWaitScope.AllWaits, MacroValidation.MaxDurationMs);
        Assert.Equal(1, edits);
        Assert.All(profile.Macros.Definitions[0].Steps, step => Assert.Equal(MacroValidation.MaxDurationMs, step.DurationMs));
        macro.SetWaitTimes(MacroWaitScope.AllWaits, 0);
        Assert.Equal(2, edits);
        Assert.All(profile.Macros.Definitions[0].Steps, step => Assert.Equal(0, step.DurationMs));
        macro.SetWaitTimes(MacroWaitScope.AllWaits, -1);
        macro.SetWaitTimes(MacroWaitScope.AllWaits, MacroValidation.MaxDurationMs + 1);
        macro.SetWaitTimes((MacroWaitScope)99, 100);
        editor.SetRecordingDestination(macro);
        macro.SetWaitTimes(MacroWaitScope.AllWaits, 100);
        editor.SetRecordingDestination(null);
        profile.IsPersistenceSuspended = true;
        macro.SetWaitTimes(MacroWaitScope.AllWaits, 100);
        Assert.Equal(2, edits);
        Assert.All(profile.Macros.Definitions[0].Steps, step => Assert.Equal(0, step.DurationMs));
    }

    [Fact]
    public void SetWaitTimes_PreservesOtherMalformedText_AndSavesAfterCorrection()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition
        {
            IsEnabled = true, ShortcutKey = Key.F6,
            Steps = [new() { Kind = MacroStepKind.KeyPress, Key = Key.A, DurationMs = 25 },
                new() { Kind = MacroStepKind.Wait, DurationMs = 10 }]
        }];
        using var editor = new MacrosViewModel(profile, () => { });
        var macro = editor.SelectedMacro!;
        macro.Steps[0].DurationText = "invalid";

        macro.SetWaitTimes(MacroWaitScope.BetweenSteps, 100);

        Assert.Equal("invalid", macro.Steps[0].DurationText);
        Assert.Equal("100", macro.Steps[1].DurationText);
        Assert.False(profile.Macros.Definitions[0].IsEnabled);
        Assert.Equal(10, profile.Macros.Definitions[0].Steps[1].DurationMs);
        macro.Steps[0].DurationText = "25";
        Assert.True(profile.Macros.Definitions[0].IsEnabled);
        Assert.Equal(100, profile.Macros.Definitions[0].Steps[1].DurationMs);
    }

    private static MacroStep Edge(MacroStepKind kind, Key key) => new() { Kind = kind, Key = key };
    private static MacroStep Click(MacroStepKind kind) => new() { Kind = kind, MouseButton = MouseButton.Left };
    private static MacroStep Move(int x, int y) => new() { Kind = MacroStepKind.MoveTo, X = x, Y = y };
    private static MacroStep Wait(int durationMs) => new() { Kind = MacroStepKind.Wait, DurationMs = durationMs };
}
