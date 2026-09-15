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
    public void WaitEditor_ValidatesLocalText_AndAppliesExplicitWaitsOnly()
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
        Assert.Equal("Applies to 2 Wait steps, including 1 press hold.", macro.WaitEditMessage);

        foreach (var input in new[] { "", "letters", "-1", "3600001", "3,600,000" })
        {
            macro.AllWaitDurationText = input;
            Assert.False(macro.ApplyAllWaitTimesCommand.CanExecute(null));
            macro.ApplyAllWaitTimesCommand.Execute(null);
            Assert.Equal(input.Length > 0, macro.HasWaitEditError);
            Assert.False(macro.HasFormatError);
            Assert.Same(saved, profile.Macros.Definitions[0]);
            Assert.Equal(0, edits);
        }

        macro.AllWaitDurationText = "50";
        Assert.True(macro.ApplyAllWaitTimesCommand.CanExecute(null));
        macro.ApplyAllWaitTimesCommand.Execute(null);
        Assert.True(macro.IsWaitEditorOpen);
        Assert.True(macro.WaitEditApplied);
        Assert.Equal("Set 2 Wait steps to 50 ms.", macro.WaitEditMessage);
        Assert.Equal(new[] { 0, 50, 0, 50, 25 }, profile.Macros.Definitions[0].Steps.Select(step => step.DurationMs));
        Assert.Equal(1, edits);
        macro.CloseWaitEditorCommand.Execute(null);
        Assert.False(macro.IsWaitEditorOpen);
        Assert.Equal(string.Empty, macro.AllWaitDurationText);
        Assert.False(macro.WaitEditApplied);
        Assert.Equal(1, edits);
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
            macro.AllWaitDurationText = "123";
            close();
            Assert.False(macro.IsWaitEditorOpen);
            Assert.Equal(string.Empty, macro.AllWaitDurationText);
            Assert.False(macro.ApplyAllWaitTimesCommand.CanExecute(null));
            editor.SetRecordingDestination(null);
            profile.IsEnabled = true;
            profile.IsPersistenceSuspended = false;
            editor.SelectedMacro = macro;
            editor.RefreshAvailability();
        }

        macro.ShowWaitEditorCommand.Execute(null);
        macro.AllWaitDurationText = "100";
        macro.DeleteStepCommand.Execute(null);
        Assert.True(macro.IsWaitEditorOpen);
        Assert.Equal("This macro has no Wait steps.", macro.WaitEditMessage);
        Assert.False(macro.ApplyAllWaitTimesCommand.CanExecute(null));
    }

    [Fact]
    public void SetAllWaitTimes_UpdatesOnlyWaitRows_PublishesOnceAndKeepsSelection()
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

        macro.SetAllWaitTimes(100);

        Assert.Equal(new[] { 100, 25, 35, 100 }, profile.Macros.Definitions[0].Steps.Select(step => step.DurationMs));
        Assert.Equal(2, macro.WaitStepCount);
        Assert.Same(selected, macro.SelectedStep);
        Assert.Equal(1, edits);
        macro.SetAllWaitTimes(100);
        Assert.Equal(1, edits);
    }

    [Fact]
    public void SetAllWaitTimes_ThousandRows_IsOneEditAndAcceptsExistingBoundaries()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition
        {
            Steps = Enumerable.Repeat(new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 20 }, MacroValidation.MaxSteps).ToArray()
        }];
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        var macro = editor.SelectedMacro!;

        macro.SetAllWaitTimes(MacroValidation.MaxDurationMs);
        Assert.Equal(1, edits);
        Assert.All(profile.Macros.Definitions[0].Steps, step => Assert.Equal(MacroValidation.MaxDurationMs, step.DurationMs));
        macro.SetAllWaitTimes(0);
        Assert.Equal(2, edits);
        Assert.All(profile.Macros.Definitions[0].Steps, step => Assert.Equal(0, step.DurationMs));
        macro.SetAllWaitTimes(-1);
        macro.SetAllWaitTimes(MacroValidation.MaxDurationMs + 1);
        editor.SetRecordingDestination(macro);
        macro.SetAllWaitTimes(100);
        editor.SetRecordingDestination(null);
        profile.IsPersistenceSuspended = true;
        macro.SetAllWaitTimes(100);
        Assert.Equal(2, edits);
        Assert.All(profile.Macros.Definitions[0].Steps, step => Assert.Equal(0, step.DurationMs));
    }

    [Fact]
    public void SetAllWaitTimes_PreservesOtherMalformedText_AndSavesAfterCorrection()
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

        macro.SetAllWaitTimes(100);

        Assert.Equal("invalid", macro.Steps[0].DurationText);
        Assert.Equal("100", macro.Steps[1].DurationText);
        Assert.False(profile.Macros.Definitions[0].IsEnabled);
        Assert.Equal(10, profile.Macros.Definitions[0].Steps[1].DurationMs);
        macro.Steps[0].DurationText = "25";
        Assert.True(profile.Macros.Definitions[0].IsEnabled);
        Assert.Equal(100, profile.Macros.Definitions[0].Steps[1].DurationMs);
    }
}
