using System.Windows.Input;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using sWinShortcuts.ViewModels;
using Xunit;

namespace Tests;

public sealed class MacroEditorTests
{
    [Fact]
    public void Shortcut_KeyboardMouseAndClear_PublishesEachTargetAtomically()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        const ModifierKeys modifiers = ModifierKeys.Control | ModifierKeys.Alt;
        profile.Macros.Definitions = [new() { ShortcutKey = Key.F6, ShortcutModifiers = modifiers }];
        var beforePublish = new List<MacroDefinition>();
        var published = new List<MacroDefinition>();
        using var editor = new MacrosViewModel(profile, () => published.Add(profile.Macros.Definitions[0]));
        editor.ConfigureRuntime(_ => Task.CompletedTask, () => { }, _ => { },
            () => beforePublish.Add(profile.Macros.Definitions[0]), _ => null, () => { });
        var macro = editor.SelectedMacro!;
        var notifications = new List<string?>();
        macro.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        var mouse = KeyChord.FromTrigger(InputTrigger.FromMouseButton(sWinShortcuts.Models.MouseButton.Middle), modifiers);

        macro.Shortcut = mouse;
        macro.Shortcut = mouse;

        Assert.Equal(mouse, macro.Shortcut);
        Assert.Equal("LCtrl+LAlt+Middle Mouse Button", macro.ShortcutText);
        Assert.Single(beforePublish);
        Assert.Equal(Key.F6, beforePublish[0].ShortcutKey);
        Assert.Single(published);
        Assert.Equal(Key.None, published[0].ShortcutKey);
        Assert.Equal(sWinShortcuts.Models.MouseButton.Middle, published[0].ShortcutMouseButton);
        Assert.Contains(nameof(MacroViewModel.Shortcut), notifications);
        Assert.Contains(nameof(MacroViewModel.ShortcutText), notifications);

        macro.Shortcut = KeyChord.FromKey(Key.F7, modifiers);
        Assert.Equal(Key.F7, published[^1].ShortcutKey);
        Assert.Null(published[^1].ShortcutMouseButton);
        Assert.Equal("LCtrl+LAlt+F7", macro.ShortcutText);
        macro.Shortcut = mouse;
        macro.Shortcut = KeyChord.FromKey(Key.F8, modifiers);
        Assert.Null(published[^1].ShortcutMouseButton);

        // Wheels and undefined buttons are not macro shortcuts: both edits are ignored.
        macro.Shortcut = KeyChord.FromTrigger(InputTrigger.FromWheel(MouseWheelDirection.Up));
        macro.Shortcut = KeyChord.FromTrigger(new(InputTriggerKind.MouseButton, Key.None, (sWinShortcuts.Models.MouseButton)99));
        Assert.Equal(KeyChord.FromKey(Key.F8, modifiers), macro.Shortcut);
        Assert.Equal(4, published.Count);
        Assert.All(published, current => Assert.Equal(modifiers, current.ShortcutModifiers));

        macro.Shortcut = KeyChord.None;
        Assert.Equal("No shortcut", macro.ShortcutText);
        Assert.Equal(5, published.Count);
        Assert.Equal(5, beforePublish.Count);
        Assert.Equal(ModifierKeys.None, published[^1].ShortcutModifiers);

        profile.IsEnabled = false;
        macro.Shortcut = mouse;
        Assert.Equal(KeyChord.None, macro.Shortcut);
        Assert.Equal(5, published.Count);
    }

    public static TheoryData<int> ModifierSubsets()
    {
        var data = new TheoryData<int>();
        for (var mask = 1; mask < 16; mask++) data.Add(mask);
        return data;
    }

    // Each of the 15 Ctrl/Alt/Shift/Win combinations reaches the model in one publish and displays modifiers first.
    [Theory]
    [MemberData(nameof(ModifierSubsets))]
    public void Shortcut_EveryModifierSubsetWithKey_PublishesOnceAndDisplaysModifiersFirst(int mask)
    {
        var modifiers = ((mask & 1) != 0 ? ModifierKeys.Control : ModifierKeys.None) |
            ((mask & 2) != 0 ? ModifierKeys.Alt : ModifierKeys.None) |
            ((mask & 4) != 0 ? ModifierKeys.Shift : ModifierKeys.None) |
            ((mask & 8) != 0 ? ModifierKeys.Windows : ModifierKeys.None);
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new()];
        var published = new List<MacroDefinition>();
        using var editor = new MacrosViewModel(profile, () => published.Add(profile.Macros.Definitions[0]));
        var macro = editor.SelectedMacro!;
        var notifications = new List<string?>();
        macro.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        macro.Shortcut = KeyChord.FromKey(Key.E, modifiers);

        var definition = Assert.Single(published);
        Assert.Equal(Key.E, definition.ShortcutKey);
        Assert.Null(definition.ShortcutMouseButton);
        Assert.Equal(modifiers, definition.ShortcutModifiers);
        Assert.Equal(KeyChord.FromKey(Key.E, modifiers), macro.Shortcut);
        Assert.Equal(KeyChord.FromKey(Key.E, modifiers).ToDisplayText(), macro.ShortcutText);
        // Mask bits 1, 2, 4, 8 are Ctrl, Alt, Shift, Win: the display order.
        Assert.Equal(string.Concat(new[] { "LCtrl+", "LAlt+", "LShift+", "LWin+" }.Where((_, bit) => (mask & (1 << bit)) != 0)) + "E",
            macro.ShortcutText);
        Assert.Contains(nameof(MacroViewModel.Shortcut), notifications);
        Assert.Contains(nameof(MacroViewModel.ShortcutText), notifications);
    }

    [Fact]
    public void Shortcut_MouseWithModifiersThenClear_ReplacesKeyAndClearsPrefixes()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new() { ShortcutKey = Key.F6 }];
        var published = new List<MacroDefinition>();
        using var editor = new MacrosViewModel(profile, () => published.Add(profile.Macros.Definitions[0]));
        var macro = editor.SelectedMacro!;

        macro.Shortcut = KeyChord.FromTrigger(InputTrigger.FromMouseButton(sWinShortcuts.Models.MouseButton.XButton1),
            ModifierKeys.Control | ModifierKeys.Shift);
        Assert.Equal(Key.None, published[^1].ShortcutKey);
        Assert.Equal(sWinShortcuts.Models.MouseButton.XButton1, published[^1].ShortcutMouseButton);
        Assert.Equal("LCtrl+LShift+Mouse Button 4", macro.ShortcutText);

        // A wheel is not a macro shortcut; the edit is ignored.
        macro.Shortcut = KeyChord.FromTrigger(InputTrigger.FromWheel(MouseWheelDirection.Up));
        Assert.Single(published);

        macro.Shortcut = KeyChord.None;
        Assert.Equal(2, published.Count);
        Assert.Equal(ModifierKeys.None, published[^1].ShortcutModifiers);
        Assert.Equal(KeyChord.None, macro.Shortcut);
        Assert.Equal("No shortcut", macro.ShortcutText);
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
    public void ShowPlayingSteps_DefaultOff_PublishesEachChangedValue()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        editor.NewMacroCommand.Execute(null);
        var macro = editor.SelectedMacro!;
        var before = edits;
        Assert.False(macro.ShowPlayingSteps);

        macro.ShowPlayingSteps = true;
        macro.ShowPlayingSteps = true;

        Assert.True(profile.Macros.Definitions[0].ShowPlayingSteps);
        Assert.Equal(before + 1, edits);
        Assert.Equal("Once", macro.PlaybackSummary);
        macro.ShowPlayingSteps = false;
        Assert.False(profile.Macros.Definitions[0].ShowPlayingSteps);
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
        macro.Shortcut = KeyChord.FromKey(Key.F6);
        macro.InsertStepCommand.Execute(null);
        Assert.False(macro.HasFormatError);
        var hold = macro.SelectedStep!.CollapsedWait!;
        hold.DurationText = "letters";
        macro.SelectedPressKey = Key.B;

        Assert.Equal("letters", hold.DurationText);
        Assert.False(profile.Macros.Definitions[0].IsEnabled);
        Assert.Contains("Not saved", macro.ValidationMessage);
        Assert.True(macro.HasFormatError);
        Assert.Equal(2, macro.ProblemStepNumber);

        hold.DurationText = "25";
        Assert.True(profile.Macros.Definitions[0].IsEnabled);
        Assert.Equal(25, profile.Macros.Definitions[0].Steps[1].DurationMs);
        Assert.Equal(Key.B, profile.Macros.Definitions[0].Steps[2].Key);
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
        macro.Shortcut = KeyChord.FromKey(Key.F6);
        macro.InsertStepCommand.Execute(null);
        var hold = macro.SelectedStep!.CollapsedWait!;
        hold.DurationMs = -1;

        Assert.True(macro.IsEnabled);
        Assert.False(profile.Macros.Definitions[0].IsEnabled);
        Assert.Equal(MacroValidation.DefaultPressHoldMs, profile.Macros.Definitions[0].Steps[1].DurationMs);
        Assert.Contains("Not saved", macro.ValidationMessage);

        hold.DurationMs = 25;

        Assert.True(profile.Macros.Definitions[0].IsEnabled);
        Assert.Equal(25, profile.Macros.Definitions[0].Steps[1].DurationMs);
        Assert.True(macro.IsPlayable);
    }

    [Fact]
    public void RowOperations_PreserveOrdering_AndPublishUpdatedDefinitions()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var editor = new MacrosViewModel(profile, () => { });
        editor.NewMacroCommand.Execute(null);
        var macro = editor.SelectedMacro!;
        Key[] PressedKeys() => profile.Macros.Definitions[0].Steps.Where(step => step.Kind == MacroStepKind.KeyDown).Select(step => step.Key).ToArray();
        macro.AddStepCommand.Execute(MacroStepKind.KeyPress);
        macro.SelectedPressKey = Key.A;
        sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.D, ModifierKeys.Control)!.Execute(null);
        macro.SelectedPressKey = Key.B;
        sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.Up, ModifierKeys.Alt)!.Execute(null);
        Assert.Equal(new[] { Key.B, Key.A }, PressedKeys());
        Assert.Equal("after step 3", macro.InsertionHint);
        sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.Down, ModifierKeys.Alt)!.Execute(null);
        Assert.Equal(new[] { Key.A, Key.B }, PressedKeys());
        Assert.Equal("at the end", macro.InsertionHint);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, macro.Steps.Select(step => step.Number));
        sWinShortcuts.Views.MacrosView.GetStepKeyCommand(macro, Key.Delete, ModifierKeys.None)!.Execute(null);
        Assert.Equal(new[] { Key.A }, PressedKeys());
        Assert.Equal(3, profile.Macros.Definitions[0].Steps.Length);
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
        original.Shortcut = KeyChord.FromKey(Key.F6, ModifierKeys.Control);
        original.InsertStepCommand.Execute(null);
        original.SelectedStep!.Key = Key.B;

        editor.DuplicateMacroCommand.Execute(null);

        var copy = editor.SelectedMacro!;
        Assert.NotEqual(original.Id, copy.Id);
        Assert.False(copy.IsEnabled);
        Assert.True(copy.ToggleMode);
        Assert.True(copy.CancelOnMouseMovement);
        Assert.Equal(KeyChord.None, copy.Shortcut);
        Assert.Equal(ModifierKeys.None, profile.Macros.Definitions[1].ShortcutModifiers);
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
        macro.Shortcut = KeyChord.FromKey(Key.F6);
        macro.AddStepCommand.Execute(MacroStepKind.Wait);
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
        macro.SelectedStep!.Kind = MacroStepKind.Wait;
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
        Assert.Equal(3, macro.Steps.Count);
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
        Assert.Equal("Assign a shortcut to play it.", macro.ValidationMessage);
        macro.Shortcut = KeyChord.FromKey(Key.F6);
        Assert.Equal("Add a step to play it.", macro.ValidationMessage);
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
        Assert.Equal("after step 4", macro.InsertionHint);
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
        macro.AddStepCommand.Execute(MacroStepKind.MoveTo);
        Assert.Equal((-1280, 300), (macro.SelectedStep!.X, macro.SelectedStep.Y));
        macro.SelectedStep.SetPosition(40, 50);
        macro.AddStepCommand.Execute(MacroStepKind.MoveTo);
        Assert.Equal((40, 50), (macro.SelectedStep!.X, macro.SelectedStep.Y));
        macro.AddStepCommand.Execute(MacroStepKind.KeyPress);
        Assert.Equal(MacroValidation.DefaultPressHoldMs, macro.SelectedStep!.CollapsedWait!.DurationMs);

        using var empty = new MacroViewModel(new MacroDefinition(), () => true);
        empty.AddStepCommand.Execute(MacroStepKind.MoveTo);
        Assert.Equal((0, 0), (empty.SelectedStep!.X, empty.SelectedStep.Y));
    }

    [Fact]
    public void AddStep_MoveCursorBy_EditsAnOffsetThatIsNeverUsedAsAPosition()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [new() { Kind = MacroStepKind.MoveTo, X = -1280, Y = 300 }]
        }, () => true);

        macro.AddStepCommand.Execute(MacroStepKind.MoveCursorBy);
        var offset = macro.SelectedStep!;
        Assert.Equal((0, 0), (offset.X, offset.Y));
        Assert.Equal("Move", offset.ActionLabel);
        Assert.True(offset.HasOffset);
        Assert.False(offset.HasCoordinates);
        Assert.False(macro.PickCoordinatesCommand.CanExecute(null));
        Assert.DoesNotContain(MacroViewModel.ActionOptions, option => option.Key == MacroStepKind.MoveCursorBy);

        offset.XText = "-25";
        offset.YText = "12";
        Assert.Equal("-25, +12 px", offset.Detail);
        Assert.Empty(offset.Error);
        offset.XText = "left";
        Assert.Equal("Offsets must be signed whole numbers.", offset.Error);
        offset.XText = "-25";

        macro.AddStepCommand.Execute(MacroStepKind.MoveTo);
        Assert.Equal((-1280, 300), (macro.SelectedStep!.X, macro.SelectedStep.Y));
        Assert.Equal(new MacroStep { Kind = MacroStepKind.MoveCursorBy, Key = Key.A, MouseButton = sWinShortcuts.Models.MouseButton.Left, WheelDelta = 120, X = -25, Y = 12 },
            macro.ToDefinition().Steps[1]);
    }

    [Theory]
    [InlineData(MacroStepKind.MoveTo)]
    [InlineData(MacroStepKind.MoveCursorBy)]
    [InlineData(MacroStepKind.MoveMouseBy)]
    public void ActionChoice_AnyMoveKind_ShowsMove(MacroStepKind kind)
    {
        using var macro = new MacroViewModel(new MacroDefinition { Steps = [new() { Kind = kind }] }, () => true);
        var step = Assert.Single(macro.Steps);

        Assert.True(step.IsMove);
        Assert.Equal(MacroStepKind.MoveTo, step.ActionChoice);
        Assert.Equal("Move", step.ActionLabel);
        Assert.Contains(new KeyValuePair<MacroStepKind, string>(MacroStepKind.MoveTo, "Move"), MacroViewModel.ActionOptions);
    }

    [Theory]
    [InlineData(MacroStepKind.MoveCursorBy)]
    [InlineData(MacroStepKind.MoveMouseBy)]
    public void ActionChoice_SelectMoveOnAMoveStep_KeepsItsMode(MacroStepKind kind)
    {
        using var macro = new MacroViewModel(new MacroDefinition { Steps = [new() { Kind = kind, X = 40, Y = -5 }] }, () => true);
        var step = Assert.Single(macro.Steps);

        step.ActionChoice = MacroStepKind.MoveTo;

        Assert.Equal(kind, step.Kind);
        Assert.Equal((40, -5), (step.X, step.Y));
    }

    [Fact]
    public void MoveIsOffset_FromPosition_StartsAtZeroAndUsesPixels()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [new() { Kind = MacroStepKind.MoveTo, X = 1200, Y = 800 }]
        }, () => true);
        var step = Assert.Single(macro.Steps);

        step.MoveIsPosition = false;
        Assert.True(step.MoveIsPosition);
        step.MoveIsOffset = true;

        Assert.Equal((0, 0), (step.X, step.Y));
        Assert.Equal(MacroStepKind.MoveCursorBy, step.Kind);
        Assert.True(step.MoveIsOffset);
        Assert.False(step.MoveIsPosition);
    }

    [Theory]
    [InlineData(true, 300, 400)]
    [InlineData(false, 0, 0)]
    public void MoveIsPosition_FromOffset_StartsAtLastPointedPosition(bool hasPriorPosition, int x, int y)
    {
        MacroStep[] steps =
        [
            new() { Kind = MacroStepKind.MoveTo, X = 100, Y = 200 },
            new() { Kind = MacroStepKind.MouseClick, X = 300, Y = 400 },
            new() { Kind = MacroStepKind.MoveCursorBy, X = 60, Y = 80 },
            new() { Kind = MacroStepKind.MoveMouseBy, X = 40, Y = -5 },
            new() { Kind = MacroStepKind.MoveTo, X = 500, Y = 600 }
        ];
        using var macro = new MacroViewModel(new MacroDefinition { Steps = hasPriorPosition ? steps : steps[2..] }, () => true);
        var step = macro.Steps[^2];

        step.MoveIsPosition = true;

        Assert.Equal(MacroStepKind.MoveTo, step.Kind);
        Assert.Equal((x, y), (step.X, step.Y));
    }

    [Fact]
    public void MoveTarget_FlipBackAndForth_RestoresEachModesValues()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [new() { Kind = MacroStepKind.MoveTo, X = 1200, Y = 800 }]
        }, () => true);
        var step = Assert.Single(macro.Steps);
        step.MoveIsOffset = true;
        step.SetPosition(40, -5);

        step.MoveIsPosition = true;
        Assert.Equal((1200, 800), (step.X, step.Y));
        step.MoveIsOffset = true;
        Assert.Equal((40, -5), (step.X, step.Y));
    }

    [Fact]
    public void MoveAs_SwitchPixelsAndRaw_KeepsOffsetAndHeldButtons()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [new() { Kind = MacroStepKind.MoveCursorBy, X = 40, Y = -5, HeldButtons = MacroHeldButtonPolicy.Wait }]
        }, () => true);
        var step = Assert.Single(macro.Steps);

        step.MoveAsRaw = true;
        step.MoveAsPixels = false;
        Assert.Equal((false, true), (step.MoveAsPixels, step.MoveAsRaw));
        Assert.Equal((40, -5, MacroHeldButtonPolicy.Wait), (step.X, step.Y, step.HeldButtons));

        step.MoveAsPixels = true;
        step.MoveAsRaw = false;
        Assert.Equal((true, false), (step.MoveAsPixels, step.MoveAsRaw));
        Assert.Equal((40, -5, MacroHeldButtonPolicy.Wait), (step.X, step.Y, step.HeldButtons));
    }

    [Fact]
    public void MoveIsOffset_AfterRaw_ReturnsToRaw()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [new() { Kind = MacroStepKind.MoveMouseBy, X = 40, Y = -5, HeldButtons = MacroHeldButtonPolicy.Allow }]
        }, () => true);
        var step = Assert.Single(macro.Steps);

        step.MoveIsOffset = true;
        Assert.True(step.MoveAsRaw);
        step.MoveIsPosition = true;
        Assert.True(step.HeldButtonsAllow);
        step.MoveIsOffset = true;

        Assert.Equal(MacroStepKind.MoveMouseBy, step.Kind);
        Assert.Equal((40, -5), (step.X, step.Y));
        Assert.True(step.HeldButtonsAllow);
    }

    [Fact]
    public void ActionChoice_OffsetToMouseClick_DoesNotUseOffsetAsPosition()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [new() { Kind = MacroStepKind.MoveTo, X = 1200, Y = 800 }, new() { Kind = MacroStepKind.MoveCursorBy, X = 40, Y = -5 }]
        }, () => true);
        var step = macro.Steps[1];

        step.ActionChoice = MacroStepKind.MouseClick;
        Assert.Equal(MacroStepKind.MouseClick, step.Kind);
        Assert.Equal((1200, 800), (step.X, step.Y));

        step.SetPosition(10, 20);
        step.ActionChoice = MacroStepKind.MoveTo;
        Assert.Equal((10, 20), (step.X, step.Y));
    }

    [Theory]
    [InlineData(MacroStepKind.MoveTo, 1200, 800, "1200, 800")]
    [InlineData(MacroStepKind.MoveCursorBy, 40, -5, "+40, -5 px")]
    [InlineData(MacroStepKind.MoveMouseBy, 40, -5, "+40, -5 raw")]
    public void Detail_MoveKinds_ShowPositionPxOrRaw(MacroStepKind kind, int x, int y, string detail)
    {
        using var macro = new MacroViewModel(new MacroDefinition { Steps = [new() { Kind = kind, X = x, Y = y }] }, () => true);

        Assert.Equal(detail, Assert.Single(macro.Steps).Detail);
    }

    [Fact]
    public void ActionChoice_OffsetToKeyPressAndBack_DoesNotUseOffsetAsPosition()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [new() { Kind = MacroStepKind.MoveTo, X = 1200, Y = 800 }, new() { Kind = MacroStepKind.MoveMouseBy, X = 40, Y = -5 }]
        }, () => true);
        var step = macro.Steps[1];

        step.ActionChoice = MacroStepKind.KeyPress;
        Assert.Equal(MacroStepKind.KeyPress, step.ActionChoice);
        Assert.False(step.IsMove);
        step.ActionChoice = MacroStepKind.MoveTo;
        Assert.Equal((1200, 800), (step.X, step.Y));

        step.MoveIsOffset = true;
        Assert.Equal((40, -5), (step.X, step.Y));
    }

    [Fact]
    public void MoveIsOffset_WithInvalidXText_ReplacesTheText()
    {
        using var macro = new MacroViewModel(new MacroDefinition { Steps = [new() { Kind = MacroStepKind.MoveTo }] }, () => true);
        var step = Assert.Single(macro.Steps);
        step.XText = "left";
        step.YText = "up";
        Assert.NotEmpty(step.Error);

        step.MoveIsOffset = true;

        Assert.Equal(("0", "0"), (step.XText, step.YText));
    }

    [Theory]
    [InlineData(MacroStepKind.MoveTo, true)]
    [InlineData(MacroStepKind.MoveCursorBy, true)]
    [InlineData(MacroStepKind.MoveMouseBy, true)]
    [InlineData(MacroStepKind.MouseClick, false)]
    [InlineData(MacroStepKind.MouseWheel, false)]
    public void HeldButtonChoice_ShowsOnlyOnMoveSteps_AndDefaultsToStop(MacroStepKind kind, bool shown)
    {
        using var macro = new MacroViewModel(new MacroDefinition(), () => true);
        macro.AddStepCommand.Execute(kind);
        var step = macro.SelectedStep!;

        Assert.Equal(shown, step.HasHeldButtons);
        Assert.True(step.HeldButtonsStop);
        Assert.Equal(MacroHeldButtonPolicy.Stop, macro.ToDefinition().Steps[0].HeldButtons);
    }

    [Fact]
    public void HeldButtonChoice_RadioButtonsSetOneChoice_AndShowItInTheRow()
    {
        using var macro = new MacroViewModel(new MacroDefinition(), () => true);
        macro.AddStepCommand.Execute(MacroStepKind.MoveMouseBy);
        var step = macro.SelectedStep!;
        Assert.Equal("Move", step.ActionLabel);
        Assert.True(step.HasOffset);
        Assert.False(step.HasCoordinates);
        Assert.DoesNotContain(MacroViewModel.ActionOptions, option => option.Key == MacroStepKind.MoveMouseBy);

        step.HeldButtonsWait = true;
        Assert.Equal((false, true, false), (step.HeldButtonsStop, step.HeldButtonsWait, step.HeldButtonsAllow));
        Assert.Equal("waits for buttons", step.TimingText);
        step.HeldButtonsWait = false; // WPF unchecking the old radio button must not change the choice
        Assert.Equal(MacroHeldButtonPolicy.Wait, macro.ToDefinition().Steps[0].HeldButtons);

        step.HeldButtonsAllow = true;
        Assert.Equal("moves while held", step.TimingText);
        Assert.Equal(MacroHeldButtonPolicy.Allow, macro.ToDefinition().Steps[0].HeldButtons);

        step.HeldButtonsStop = true;
        Assert.Empty(step.TimingText);
        Assert.Equal(MacroHeldButtonPolicy.Stop, macro.ToDefinition().Steps[0].HeldButtons);
    }

    [Fact]
    public void MouseDownWaitMoveCursorByMouseUp_MarksTheWaitAsAHold()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps =
            [
                new() { Kind = MacroStepKind.MouseDown, MouseButton = sWinShortcuts.Models.MouseButton.Left },
                new() { Kind = MacroStepKind.Wait, DurationMs = 50 },
                new() { Kind = MacroStepKind.MoveCursorBy, X = 40 },
                new() { Kind = MacroStepKind.MouseUp, MouseButton = sWinShortcuts.Models.MouseButton.Left }
            ]
        }, () => true);

        Assert.True(macro.Steps[1].IsPressHold);
    }

    [Fact]
    public void AddStep_SavePosition_UsesLowestFreeSlotAboveTheInsertionPoint()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps =
            [
                new() { Kind = MacroStepKind.SavePosition, Slot = 1 },
                new() { Kind = MacroStepKind.SavePosition, Slot = 3 },
                new() { Kind = MacroStepKind.Wait, DurationMs = 50 },
                new() { Kind = MacroStepKind.SavePosition, Slot = 2 }
            ]
        }, () => true);
        macro.SelectedStep = macro.Steps[2];

        macro.AddStepCommand.Execute(MacroStepKind.SavePosition);

        Assert.Same(macro.Steps[3], macro.SelectedStep);
        Assert.Equal(2, macro.SelectedStep!.Slot);
        Assert.Equal("Save position", macro.SelectedStep.ActionLabel);
        Assert.Equal("#2", macro.SelectedStep.Detail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void AddStep_SavePositionWithNoEarlierSlotsOrAllSlotsUsed_DefaultsToFirstSlot(int slotsUsed)
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = Enumerable.Range(1, slotsUsed)
                .Select(slot => new MacroStep { Kind = MacroStepKind.SavePosition, Slot = slot }).ToArray()
        }, () => true);
        macro.SelectedStep = null;

        macro.AddStepCommand.Execute(MacroStepKind.SavePosition);

        Assert.Equal(1, macro.SelectedStep!.Slot);
    }

    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 1)]
    public void MoveIsSaved_WithOrWithoutEarlierSaves_UsesClosestSlotOrFirstSlot(bool hasEarlierSaves, int expectedSlot)
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps =
            [
                new() { Kind = MacroStepKind.SavePosition, Slot = 6 },
                new() { Kind = MacroStepKind.SavePosition, Slot = 3 },
                new() { Kind = MacroStepKind.MoveTo },
                new() { Kind = MacroStepKind.SavePosition, Slot = 7 }
            ]
        }, () => true);
        var step = macro.Steps[hasEarlierSaves ? 2 : 0];
        step.Kind = MacroStepKind.MoveTo;
        step.Slot = 0;

        step.MoveIsSaved = true;
        step.MoveIsSaved = false;

        Assert.True(step.MoveIsSaved);
        Assert.Equal(expectedSlot, step.Slot);
        Assert.Equal(MacroStepKind.MoveTo, step.ActionChoice);
        Assert.Equal("Move", step.ActionLabel);
        Assert.Equal($"to saved #{expectedSlot}", step.Detail);
        Assert.True(step.HasSlot);
        Assert.False(step.HasCoordinates || step.HasOffset);
    }

    [Theory]
    [InlineData(MacroStepKind.MoveCursorBy)]
    [InlineData(MacroStepKind.MoveMouseBy)]
    public void MoveIsSaved_VisitedFromPositionAndOffset_RestoresValuesAndHeldButtons(MacroStepKind offsetKind)
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [new() { Kind = MacroStepKind.MoveTo, X = -120, Y = 300, HeldButtons = MacroHeldButtonPolicy.Allow }]
        }, () => true);
        var step = Assert.Single(macro.Steps);

        step.MoveIsSaved = true;
        Assert.True(step.HasHeldButtons);
        Assert.Equal(MacroHeldButtonPolicy.Allow, step.HeldButtons);
        step.HeldButtonsWait = true;
        step.Slot = 8;
        step.MoveIsPosition = true;
        Assert.Equal((-120, 300), (step.X, step.Y));
        Assert.Equal(MacroHeldButtonPolicy.Wait, step.HeldButtons);

        step.Kind = offsetKind;
        step.X = 40;
        step.Y = -5;
        step.MoveIsSaved = true;
        Assert.Equal(8, step.Slot);
        Assert.Equal(MacroHeldButtonPolicy.Wait, step.HeldButtons);
        step.MoveIsOffset = true;

        Assert.Equal(offsetKind, step.Kind);
        Assert.Equal((40, -5), (step.X, step.Y));
        Assert.Equal(MacroHeldButtonPolicy.Wait, step.HeldButtons);
    }

    [Theory]
    [InlineData(MacroStepKind.SavePosition)]
    [InlineData(MacroStepKind.MoveToSaved)]
    public void RefreshPressHolds_SavedPositionStepBetweenWaitAndMouseUp_MarksTheWaitAsAHold(MacroStepKind kind)
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps =
            [
                new() { Kind = MacroStepKind.SavePosition, Slot = 1 },
                new() { Kind = MacroStepKind.MouseDown, MouseButton = sWinShortcuts.Models.MouseButton.Left },
                new() { Kind = MacroStepKind.Wait, DurationMs = 50 },
                new() { Kind = kind, Slot = 1 },
                new() { Kind = MacroStepKind.MouseUp, MouseButton = sWinShortcuts.Models.MouseButton.Left }
            ]
        }, () => true);

        Assert.True(macro.Steps[2].IsPressHold);
        Assert.Equal("50 ms hold", macro.Steps[2].TimingText);
    }

    [Theory]
    [InlineData(MacroStepKind.FreezeMouse, "Freeze mouse")]
    [InlineData(MacroStepKind.UnfreezeMouse, "Unfreeze mouse")]
    public void AddStep_MouseFreezeKinds_InsertLabelledRowsWithoutFieldEditors(MacroStepKind kind, string label)
    {
        using var macro = new MacroViewModel(new MacroDefinition(), () => true);

        macro.AddStepCommand.Execute(kind);

        var step = Assert.Single(macro.Steps);
        Assert.Same(step, macro.SelectedStep);
        Assert.Equal(kind, step.Kind);
        Assert.Equal(label, step.ActionLabel);
        Assert.False(step.HasKey || step.HasMouseButton || step.HasCoordinates || step.HasOffset || step.HasDuration || step.IsWheel);
        Assert.Empty(step.Error);
        Assert.Empty(step.Summary);
        Assert.NotEqual("Pauses before the next step.", step.KindHelp);
        Assert.Contains(new KeyValuePair<MacroStepKind, string>(kind, label), MacroViewModel.ActionOptions);
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

    [Fact]
    public void PlaybackProblem_NamedStep_ShowsTheMessageOnItsRowUntilFixed()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            ShortcutKey = Key.F6,
            Steps = [new() { Kind = MacroStepKind.KeyDown, Key = Key.LeftShift }, .. Tests.Fakes.MacroPresses.Press(Key.Tab)]
        }, () => true);
        var down = macro.Steps[0];

        Assert.Equal("Step 1: add a matching Key up before the macro ends.", macro.ValidationMessage);
        Assert.Equal(macro.ValidationMessage, down.DisplayError);
        Assert.True(down.IsProblemOnly);
        Assert.Empty(down.Error);
        Assert.Empty(macro.Steps[1].DisplayError);

        // A new Key up starts on A, so the problem moves to that row; choosing the held key fixes the macro.
        macro.AddStepCommand.Execute(MacroStepKind.KeyUp);
        var up = macro.SelectedStep!;
        Assert.Equal("Step 5: a Key up must follow a matching Key down.", up.DisplayError);
        Assert.Empty(down.DisplayError);
        up.Key = Key.LeftShift;

        Assert.True(macro.IsPlayable);
        Assert.All(macro.Steps, step => Assert.False(step.IsProblemOnly));
    }

    [Fact]
    public void MouseButtons_SideButtons_ReadAsButton4And5()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [new() { Kind = MacroStepKind.MouseClick, MouseButton = sWinShortcuts.Models.MouseButton.XButton1, X = 1200, Y = 700 }]
        }, () => true);

        Assert.Equal("Button 4 at 1200, 700", macro.Steps[0].Detail);
        Assert.Equal(new[] { "Left", "Right", "Middle", "Button 4", "Button 5" }, MacroViewModel.MouseButtonOptions.Select(option => option.Value));
    }

    [Fact]
    public void Kind_AutoHoldPressChangedToWait_StartsWithTheNewWaitPause()
    {
        using var macro = new MacroViewModel(new MacroDefinition { Steps = [new() { Kind = MacroStepKind.KeyPress, Key = Key.E }] }, () => true);
        var step = macro.Steps[0];

        step.Kind = MacroStepKind.Wait;

        Assert.Equal(100, step.DurationMs);
        Assert.Equal("100", step.DurationText);
        Assert.Equal(MacroStepKind.Wait, MacroViewModel.ActionOptions[^1].Key);
    }

    [Fact]
    public void PlaybackMenu_RadioPairsAndSummary_FollowTheMacroOptions()
    {
        using var macro = new MacroViewModel(new MacroDefinition(), () => true);
        Assert.True(macro.PlaysOnce && macro.StartsWhenReleased);
        Assert.Equal("Once", macro.PlaybackSummary);

        macro.PlaysInLoop = true;
        macro.StartsWhenPressed = true;
        macro.CancelOnMouseMovement = true;
        // Left out of the label so it still fits the row at the minimum width.
        macro.ShowPlayingSteps = true;
        Assert.Equal((true, true), (macro.ToggleMode, macro.StartOnPress));
        Assert.Equal("Loop · on press · stops on move", macro.PlaybackSummary);

        // Clearing a checked item is ignored; only choosing its partner changes the mode.
        macro.PlaysInLoop = false;
        macro.StartsWhenPressed = false;
        Assert.Equal((true, true), (macro.ToggleMode, macro.StartOnPress));
        macro.PlaysOnce = true;
        macro.StartsWhenReleased = true;
        Assert.Equal((false, false), (macro.ToggleMode, macro.StartOnPress));
        Assert.Equal("Once · stops on move", macro.PlaybackSummary);
    }

    [Fact]
    public void WheelDirection_RadioProjections_IgnoreClearing()
    {
        using var macro = new MacroViewModel(new MacroDefinition
        {
            Steps = [new() { Kind = MacroStepKind.MouseWheel, WheelDelta = 120 }]
        }, () => true);

        var wheel = macro.Steps[0];
        Assert.True(wheel.IsVerticalWheel);
        wheel.IsHorizontalWheel = true;
        Assert.True(wheel.HorizontalWheel && !wheel.IsVerticalWheel);
        wheel.IsHorizontalWheel = false;
        Assert.True(wheel.HorizontalWheel);
        wheel.IsVerticalWheel = true;
        Assert.False(wheel.HorizontalWheel);
    }
}
