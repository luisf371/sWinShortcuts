using System.Windows.Input;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.ViewModels;
using Xunit;

namespace Tests;

public sealed class MacroEditorTests
{
    [Fact]
    public void ShortcutTrigger_KeyboardMouseAndClear_PublishesEachTargetAtomically()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new() { ShortcutKey = Key.F6, ShortcutModifiers = ModifierKeys.Control | ModifierKeys.Alt }];
        var beforePublish = new List<MacroDefinition>();
        var published = new List<MacroDefinition>();
        using var editor = new MacrosViewModel(profile, () => published.Add(profile.Macros.Definitions[0]));
        editor.ConfigureRuntime(_ => Task.CompletedTask, () => { }, _ => { },
            () => beforePublish.Add(profile.Macros.Definitions[0]), _ => null, () => { });
        var macro = editor.SelectedMacro!;
        var notifications = new List<string?>();
        macro.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        var mouse = InputTrigger.FromMouseButton(sWinShortcuts.Models.MouseButton.Middle);

        macro.ShortcutTrigger = mouse;
        macro.ShortcutTrigger = mouse;

        Assert.Equal(Key.None, macro.ShortcutKey);
        Assert.Equal("Ctrl+Alt+Middle Mouse Button", macro.ShortcutText);
        Assert.Single(beforePublish);
        Assert.Equal(Key.F6, beforePublish[0].ShortcutKey);
        Assert.Single(published);
        Assert.Equal(Key.None, published[0].ShortcutKey);
        Assert.Equal(sWinShortcuts.Models.MouseButton.Middle, published[0].ShortcutMouseButton);
        Assert.Contains(nameof(MacroViewModel.ShortcutKey), notifications);
        Assert.Contains(nameof(MacroViewModel.ShortcutTrigger), notifications);
        Assert.Contains(nameof(MacroViewModel.ShortcutText), notifications);

        macro.ShortcutTrigger = InputTrigger.FromKey(Key.F7);
        Assert.Equal(Key.F7, macro.ShortcutKey);
        Assert.Null(published[^1].ShortcutMouseButton);
        Assert.Equal("Ctrl+Alt+F7", macro.ShortcutText);
        macro.ShortcutTrigger = mouse;
        macro.ShortcutKey = Key.F8;
        Assert.Equal(InputTrigger.FromKey(Key.F8), macro.ShortcutTrigger);
        Assert.Null(published[^1].ShortcutMouseButton);
        macro.ShortcutTrigger = InputTrigger.None;
        Assert.Equal("No shortcut", macro.ShortcutText);
        Assert.Equal(5, published.Count);
        Assert.Equal(5, beforePublish.Count);
        Assert.All(published, current => Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, current.ShortcutModifiers));

        macro.ShortcutTrigger = InputTrigger.FromWheel(MouseWheelDirection.Up);
        macro.ShortcutTrigger = new(InputTriggerKind.MouseButton, Key.None, (sWinShortcuts.Models.MouseButton)99);
        profile.IsEnabled = false;
        macro.ShortcutTrigger = mouse;
        Assert.Equal(InputTrigger.None, macro.ShortcutTrigger);
        Assert.Equal(5, published.Count);
    }

    [Fact]
    public void ShortcutOptions_IncludesExistingKeyboardChoicesAndAllFiveMouseButtons()
    {
        Assert.Equal(InputTrigger.None, MacroViewModel.ShortcutOptions[0]);
        Assert.Equal(MacroViewModel.KeyOptions.Select(InputTrigger.FromKey),
            MacroViewModel.ShortcutOptions.Where(trigger => trigger.Kind == InputTriggerKind.KeyboardKey));
        Assert.Equal(Enum.GetValues<sWinShortcuts.Models.MouseButton>().Select(InputTrigger.FromMouseButton),
            MacroViewModel.ShortcutOptions.Where(trigger => trigger.Kind == InputTriggerKind.MouseButton));
        Assert.DoesNotContain(MacroViewModel.ShortcutOptions, trigger => trigger.Kind == InputTriggerKind.MouseWheel);
    }

    [Fact]
    public void ToggleMode_DefaultOff_CancelsBeforePublishingEachChangedValue()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new()];
        var beforePublish = new List<MacroDefinition>();
        var published = new List<MacroDefinition>();
        using var editor = new MacrosViewModel(profile, () => published.Add(profile.Macros.Definitions[0]));
        editor.ConfigureRuntime(_ => Task.CompletedTask, () => { }, _ => { },
            () => beforePublish.Add(profile.Macros.Definitions[0]), _ => null, () => { });
        var macro = editor.SelectedMacro!;
        Assert.False(macro.ToggleMode);

        macro.ToggleMode = true;
        macro.ToggleMode = true;
        macro.ToggleMode = false;

        Assert.Collection(beforePublish, previous => Assert.False(previous.ToggleMode), previous => Assert.True(previous.ToggleMode));
        Assert.Collection(published, current => Assert.True(current.ToggleMode), current => Assert.False(current.ToggleMode));
        Assert.False(macro.ToDefinition().ToggleMode);
        profile.IsEnabled = false;
        macro.ToggleMode = true;
        profile.IsEnabled = true;
        profile.IsPersistenceSuspended = true;
        macro.ToggleMode = true;
        Assert.False(macro.ToggleMode);
        Assert.False(profile.Macros.Definitions[0].ToggleMode);
        Assert.Equal(2, beforePublish.Count);
        Assert.Equal(2, published.Count);
    }

    [Fact]
    public void CancelOnMouseMovement_DefaultOff_PublishesEachChangedValue()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        editor.NewMacroCommand.Execute(null);
        var macro = editor.SelectedMacro!;
        var before = edits;
        Assert.False(macro.CancelOnMouseMovement);

        macro.CancelOnMouseMovement = true;
        macro.CancelOnMouseMovement = true;

        Assert.True(profile.Macros.Definitions[0].CancelOnMouseMovement);
        Assert.Equal(before + 1, edits);
        macro.CancelOnMouseMovement = false;
        Assert.False(profile.Macros.Definitions[0].CancelOnMouseMovement);
        Assert.Equal(before + 2, edits);
    }

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
        Assert.False(macro.HasFormatError);
        macro.SelectedStep!.DurationText = "letters";
        macro.SelectedStep.Key = Key.B;

        Assert.Equal("letters", macro.SelectedStep.DurationText);
        Assert.False(profile.Macros.Definitions[0].IsEnabled);
        Assert.Contains("Not saved", macro.ValidationMessage);
        Assert.True(macro.HasFormatError);
        Assert.Equal(1, macro.ProblemStepNumber);

        macro.SelectedStep.DurationText = "25";
        Assert.True(profile.Macros.Definitions[0].IsEnabled);
        Assert.Equal(25, profile.Macros.Definitions[0].Steps[0].DurationMs);
        Assert.False(macro.HasFormatError);
        Assert.False(macro.HasProblemStep);
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
        macro.AddStepCommand.Execute(MacroStepKind.KeyPress);
        macro.SelectedStep!.Key = Key.A;
        sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.D, ModifierKeys.Control)!.Execute(null);
        macro.SelectedStep!.Key = Key.B;
        sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.Up, ModifierKeys.Alt)!.Execute(null);
        Assert.Equal(new[] { Key.B, Key.A }, profile.Macros.Definitions[0].Steps.Select(step => step.Key));
        Assert.Equal("after step 1", macro.InsertionHint);
        sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.Down, ModifierKeys.Alt)!.Execute(null);
        Assert.Equal(new[] { Key.A, Key.B }, profile.Macros.Definitions[0].Steps.Select(step => step.Key));
        Assert.Equal("at the end", macro.InsertionHint);
        Assert.Equal(new[] { 1, 2 }, macro.Steps.Select(step => step.Number));
        sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.Delete, ModifierKeys.None)!.Execute(null);
        Assert.Equal(Key.A, Assert.Single(profile.Macros.Definitions[0].Steps).Key);
        Assert.Null(sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.D, ModifierKeys.None));
        Assert.Null(sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.Delete, ModifierKeys.Control));
    }

    [Fact]
    public void Duplicate_NewIdentityAndDetachedRows_IsDisabledAndUnassigned()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var editor = new MacrosViewModel(profile, () => { });
        editor.NewMacroCommand.Execute(null);
        var original = Assert.Single(editor.Definitions);
        original.IsEnabled = true;
        original.ToggleMode = true;
        original.CancelOnMouseMovement = true;
        original.ShortcutKey = Key.F6;
        original.ShortcutModifiers = ModifierKeys.Control;
        original.InsertStepCommand.Execute(null);
        original.SelectedStep!.Key = Key.B;

        editor.DuplicateMacroCommand.Execute(null);

        var copy = editor.SelectedMacro!;
        Assert.NotEqual(original.Id, copy.Id);
        Assert.False(copy.IsEnabled);
        Assert.True(copy.ToggleMode);
        Assert.True(copy.CancelOnMouseMovement);
        Assert.Equal(Key.None, copy.ShortcutKey);
        Assert.Equal(ModifierKeys.None, copy.ShortcutModifiers);
        Assert.True(profile.Macros.Definitions[1].ToggleMode);
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
        Assert.False(macro.CanAddStep);
        Assert.False(macro.AddStepCommand.CanExecute(MacroStepKind.Wait));
        macro.AddStepCommand.Execute(MacroStepKind.Wait);
        Assert.Equal(1000, macro.Steps.Count);
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
        Assert.False(macro.HasFormatError);
        Assert.Equal(1, macro.ProblemStepNumber);
        macro.AddStepCommand.Execute(MacroStepKind.KeyPress);
        Assert.Equal(1, macro.SelectedIndex);
        macro.ShowProblemStepCommand.Execute(null);
        Assert.Equal(0, macro.SelectedIndex);
        macro.SelectedStep!.Kind = MacroStepKind.KeyPress;
        Assert.False(macro.HasProblemStep);
        Assert.False(macro.ShowProblemStepCommand.CanExecute(null));
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
        macro.ToggleMode = true;
        macro.CancelOnMouseMovement = true;
        macro.Steps[0].Key = Key.Z;
        editor.SelectedMacro = null;
        editor.DeleteMacroCommand.Execute(null);

        Assert.Equal("New macro", macro.Label);
        Assert.False(macro.ToggleMode);
        Assert.False(macro.CancelOnMouseMovement);
        Assert.Equal(Key.A, macro.Steps[0].Key);
        Assert.Same(macro, editor.SelectedMacro);
        Assert.Single(editor.Definitions);
        Assert.False(macro.InsertStepCommand.CanExecute(null));
        Assert.False(macro.AddStepCommand.CanExecute(MacroStepKind.Wait));
        macro.AddStepCommand.Execute(MacroStepKind.Wait);
        Assert.Single(macro.Steps);
    }
}
