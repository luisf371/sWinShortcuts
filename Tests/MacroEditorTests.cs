using System.Windows.Input;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.ViewModels;
using Xunit;

namespace Tests;

public sealed class MacroEditorTests
{
    [Fact]
    public void MalformedNumericText_IsVisibleAndDisablesPlayback_UntilCorrected()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var editor = new MacrosViewModel(profile, () => { });
        editor.NewMacroCommand.Execute(null);
        var macro = editor.SelectedMacro!;
        macro.IsEnabled = true;
        macro.ShortcutKey = Key.F6;
        macro.InsertStepCommand.Execute(null);
        macro.SelectedStep!.DurationText = "letters";
        macro.SelectedStep.Key = Key.B;

        Assert.Equal("letters", macro.SelectedStep.DurationText);
        Assert.False(profile.Macros.Definitions[0].IsEnabled);
        Assert.Contains("Not saved", macro.ValidationMessage);

        macro.SelectedStep.DurationText = "25";
        Assert.True(profile.Macros.Definitions[0].IsEnabled);
        Assert.Equal(25, profile.Macros.Definitions[0].Steps[0].DurationMs);
    }

    [Fact]
    public void InvalidField_KeepsLastRepresentableValueDisabled_UntilCorrected()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var editor = new MacrosViewModel(profile, () => { });
        editor.NewMacroCommand.Execute(null);
        var macro = editor.SelectedMacro!;
        macro.IsEnabled = true;
        macro.ShortcutKey = Key.F6;
        macro.InsertStepCommand.Execute(null);
        macro.SelectedStep!.DurationMs = -1;

        Assert.True(macro.IsEnabled);
        Assert.False(profile.Macros.Definitions[0].IsEnabled);
        Assert.Equal(0, profile.Macros.Definitions[0].Steps[0].DurationMs);
        Assert.Contains("Not saved", macro.ValidationMessage);

        macro.SelectedStep.DurationMs = 25;

        Assert.True(profile.Macros.Definitions[0].IsEnabled);
        Assert.Equal(25, profile.Macros.Definitions[0].Steps[0].DurationMs);
        Assert.True(macro.IsPlayable);
    }

    [Fact]
    public void RowOperations_PreserveOrdering_AndPublishUpdatedDefinitions()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var editor = new MacrosViewModel(profile, () => { });
        editor.NewMacroCommand.Execute(null);
        var macro = editor.SelectedMacro!;
        macro.InsertStepCommand.Execute(null);
        macro.SelectedStep!.Key = Key.A;
        macro.DuplicateStepCommand.Execute(null);
        macro.SelectedStep!.Key = Key.B;
        macro.MoveStepUpCommand.Execute(null);
        Assert.Equal(new[] { Key.B, Key.A }, profile.Macros.Definitions[0].Steps.Select(step => step.Key));
        macro.MoveStepDownCommand.Execute(null);
        Assert.Equal(new[] { Key.A, Key.B }, profile.Macros.Definitions[0].Steps.Select(step => step.Key));
        Assert.Equal(new[] { 1, 2 }, macro.Steps.Select(step => step.Number));
        macro.DeleteStepCommand.Execute(null);
        Assert.Equal(Key.A, Assert.Single(profile.Macros.Definitions[0].Steps).Key);
    }

    [Fact]
    public void Duplicate_NewIdentityAndDetachedRows_IsDisabledAndUnassigned()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var editor = new MacrosViewModel(profile, () => { });
        editor.NewMacroCommand.Execute(null);
        var original = Assert.Single(editor.Definitions);
        original.IsEnabled = true;
        original.ShortcutKey = Key.F6;
        original.InsertStepCommand.Execute(null);
        original.SelectedStep!.Key = Key.B;

        editor.DuplicateMacroCommand.Execute(null);

        var copy = editor.SelectedMacro!;
        Assert.NotEqual(original.Id, copy.Id);
        Assert.False(copy.IsEnabled);
        Assert.Equal(Key.None, copy.ShortcutKey);
        Assert.NotSame(original.Steps[0], copy.Steps[0]);
        copy.Steps[0].Key = Key.C;
        Assert.Equal(Key.B, original.Steps[0].Key);
    }

    [Fact]
    public void InsertRecording_ThousandRows_PublishesOneDetachedEditAndEnforcesLimit()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        editor.NewMacroCommand.Execute(null);
        var macro = editor.SelectedMacro!;
        var rows = Enumerable.Range(0, 1000)
            .Select(_ => new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 2 }).ToArray();
        var before = edits;

        macro.InsertRecording(0, rows);

        Assert.Equal(before + 1, edits);
        Assert.Equal(1000, macro.Steps.Count);
        Assert.Equal(1000, profile.Macros.Definitions[0].Steps.Length);
        rows[0] = new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 99 };
        Assert.Equal(2, profile.Macros.Definitions[0].Steps[0].DurationMs);
        Assert.False(macro.InsertStepCommand.CanExecute(null));
        Assert.False(macro.DuplicateStepCommand.CanExecute(null));
        Assert.Throws<ArgumentOutOfRangeException>(() => macro.InsertRecording(1000, [new MacroStep { Kind = MacroStepKind.Wait }]));
    }

    [Fact]
    public void DeletedRowAndMacro_HandlersDetached_CannotPublishAnotherEdit()
    {
        var edits = 0;
        using var editor = new MacrosViewModel(ProfileFactory.CreateCustomProfile("Game", "game.exe"), () => edits++);
        editor.NewMacroCommand.Execute(null);
        var macro = editor.SelectedMacro!;
        macro.InsertStepCommand.Execute(null);
        var row = macro.SelectedStep!;
        macro.DeleteStepCommand.Execute(null);
        var before = edits;
        row.Key = Key.B;
        Assert.Equal(before, edits);

        editor.DeleteMacroCommand.Execute(null);
        before = edits;
        macro.Label = "Detached";
        Assert.Equal(before, edits);
    }

    [Fact]
    public void IncompleteSequence_PersistsDraft_ReportsOffendingRow()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var editor = new MacrosViewModel(profile, () => { });
        editor.NewMacroCommand.Execute(null);
        var macro = editor.SelectedMacro!;
        macro.ShortcutKey = Key.F6;
        macro.InsertStepCommand.Execute(null);
        macro.SelectedStep!.Kind = MacroStepKind.KeyUp;

        Assert.Single(profile.Macros.Definitions[0].Steps);
        Assert.Contains("1", macro.ValidationMessage);
        Assert.False(macro.IsPlayable);
    }

    [Fact]
    public void RecordingDestination_FrozenUntilTakeApplied_BlocksAllMutations()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var editor = new MacrosViewModel(profile, () => { });
        editor.NewMacroCommand.Execute(null);
        var macro = editor.SelectedMacro!;
        macro.InsertStepCommand.Execute(null);
        editor.SetRecordingDestination(macro);

        macro.Label = "Changed";
        macro.Steps[0].Key = Key.Z;
        editor.SelectedMacro = null;
        editor.DeleteMacroCommand.Execute(null);

        Assert.Equal("New macro", macro.Label);
        Assert.Equal(Key.A, macro.Steps[0].Key);
        Assert.Same(macro, editor.SelectedMacro);
        Assert.Single(editor.Definitions);
        Assert.False(macro.InsertStepCommand.CanExecute(null));
    }
}
