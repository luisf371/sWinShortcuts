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

    [Fact]
    public void NewMacro_StartsEnabled_ReportsWhatIsMissing_DuplicateStillStartsOff()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var editor = new MacrosViewModel(profile, () => { });
        editor.NewMacroCommand.Execute(null);
        var macro = editor.SelectedMacro!;

        Assert.True(macro.IsEnabled);
        Assert.True(profile.Macros.Definitions[0].IsEnabled);
        Assert.Equal("Assign a shortcut to play this macro.", macro.ValidationMessage);
        macro.ShortcutKey = Key.F6;
        Assert.Equal("Add at least one step to play this macro.", macro.ValidationMessage);
        macro.AddStepCommand.Execute(MacroStepKind.KeyPress);
        Assert.True(macro.IsPlayable);

        editor.DuplicateMacroCommand.Execute(null);
        Assert.False(editor.SelectedMacro!.IsEnabled);
    }

    [Fact]
    public void Insertion_DefaultsToEnd_AndContinuesAfterEachInsertedTake()
    {
        MacroStep Press(Key key) => new() { Kind = MacroStepKind.KeyPress, Key = key };
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [Press(Key.A), new() { Kind = MacroStepKind.KeyDown, Key = Key.B },
                new() { Kind = MacroStepKind.Wait, DurationMs = 5 }, new() { Kind = MacroStepKind.KeyUp, Key = Key.B }]
        }, () => true);

        // The last visible row is selected: the collapsed B press, whose raw end is the end of the macro.
        Assert.Same(macro.Steps[1], macro.SelectedStep);
        Assert.Equal("at the end", macro.InsertionHint);

        // A take lands at the end and leaves its last row selected, so the next take follows it.
        macro.InsertRecording(macro.RecordingInsertionIndex, [Press(Key.C), new() { Kind = MacroStepKind.Wait, DurationMs = 7 }, Press(Key.D)]);
        Assert.Same(macro.Steps[6], macro.SelectedStep);
        Assert.Equal("at the end", macro.InsertionHint);
        macro.InsertRecording(macro.RecordingInsertionIndex, [Press(Key.E)]);
        Assert.Equal(new[] { Key.A, Key.B, Key.None, Key.B, Key.C, Key.None, Key.D, Key.E }, macro.ToDefinition().Steps.Select(step => step.Key));

        // Selecting a row still inserts after it.
        macro.SelectedStep = macro.Steps[0];
        macro.AddStepCommand.Execute(MacroStepKind.KeyPress);
        Assert.Equal(1, macro.SelectedIndex);
        Assert.Equal("after step 2", macro.InsertionHint);
    }

    [Fact]
    public void AddStep_StartsWithAVisibleWaitAndTheNearestEarlierPosition()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [new() { Kind = MacroStepKind.MoveTo, X = -1280, Y = 300 }, new() { Kind = MacroStepKind.KeyPress, Key = Key.A }]
        }, () => true);

        macro.AddStepCommand.Execute(MacroStepKind.Wait);
        Assert.Equal(100, macro.SelectedStep!.DurationMs);
        macro.AddStepCommand.Execute(MacroStepKind.MouseClick);
        Assert.Equal((-1280, 300), (macro.SelectedStep!.X, macro.SelectedStep.Y));
        macro.SelectedStep.SetPosition(40, 50);
        macro.AddStepCommand.Execute(MacroStepKind.MoveTo);
        Assert.Equal((40, 50), (macro.SelectedStep!.X, macro.SelectedStep.Y));
        macro.AddStepCommand.Execute(MacroStepKind.KeyPress);
        Assert.Equal(0, macro.SelectedStep!.DurationMs);

        using var empty = new MacroViewModel(new MacroDefinition(), () => true);
        empty.AddStepCommand.Execute(MacroStepKind.MoveTo);
        Assert.Equal((0, 0), (empty.SelectedStep!.X, empty.SelectedStep.Y));
    }

    [Fact]
    public void ClearSteps_AsksFirst_AndKeepsNameShortcutAndOptions()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition
        {
            Label = "Combo", IsEnabled = true, ToggleMode = true, ShortcutKey = Key.F6,
            Steps = [new() { Kind = MacroStepKind.KeyPress, Key = Key.A }, new() { Kind = MacroStepKind.Wait, DurationMs = 5 }]
        }];
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        var requests = new List<MacroConfirmation>();
        var answer = false;
        editor.ConfigureConfirmation(request => { requests.Add(request); return answer; });
        Assert.True(editor.ClearStepsCommand.CanExecute(null));

        editor.ClearStepsCommand.Execute(null);

        Assert.Equal(2, profile.Macros.Definitions[0].Steps.Length);
        Assert.Equal(0, edits);
        var request = Assert.Single(requests);
        Assert.Equal("Clear all steps?", request.Title);
        Assert.Equal("Clear steps", request.ActionText);
        Assert.Equal("All 2 steps in \"Combo\" will be removed. Its name, shortcut, and options stay.", request.Message);

        answer = true;
        editor.ClearStepsCommand.Execute(null);

        var cleared = profile.Macros.Definitions[0];
        Assert.Empty(cleared.Steps);
        Assert.Equal(("Combo", Key.F6, true, true), (cleared.Label, cleared.ShortcutKey, cleared.ToggleMode, cleared.IsEnabled));
        Assert.Null(editor.SelectedMacro!.SelectedStep);
        Assert.Equal("at the end", editor.SelectedMacro.InsertionHint);
        Assert.Equal(1, edits);
        Assert.False(editor.ClearStepsCommand.CanExecute(null));
    }

    [Fact]
    public void DeleteMacro_WithSteps_AsksFirst_EmptyMacroDoesNot_AndChangedTargetIsKept()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition { Label = "Keep", Steps = [new() { Kind = MacroStepKind.KeyPress, Key = Key.A }] },
            new MacroDefinition { Label = "Empty" }];
        using var editor = new MacrosViewModel(profile, () => { });
        var requests = new List<MacroConfirmation>();
        Func<bool> answer = () => false;
        editor.ConfigureConfirmation(request => { requests.Add(request); return answer(); });

        editor.DeleteMacroCommand.Execute(null);

        Assert.Equal(2, editor.Definitions.Count);
        var request = Assert.Single(requests);
        Assert.Equal(("Delete macro?", "Delete"), (request.Title, request.ActionText));
        Assert.Equal("\"Keep\" and its 1 step will be permanently deleted.", request.Message);

        // The modal dialog keeps dispatching: a selection change while it is open keeps the original target.
        answer = () => { editor.SelectedMacro = editor.Definitions[1]; return true; };
        editor.SelectedMacro = editor.Definitions[0];
        editor.DeleteMacroCommand.Execute(null);
        Assert.Equal(2, editor.Definitions.Count);

        // Selected after the previous answer: an empty macro is deleted without asking.
        Assert.Equal("Empty", editor.SelectedMacro!.Label);
        editor.DeleteMacroCommand.Execute(null);
        Assert.Equal(2, requests.Count);
        Assert.Equal("Keep", Assert.Single(profile.Macros.Definitions).Label);

        answer = () => true;
        editor.DeleteMacroCommand.Execute(null);
        Assert.Empty(editor.Definitions);
        Assert.Empty(profile.Macros.Definitions);
    }
}
