using System.Collections.Concurrent;
using System.Windows.Input;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;
using AppMouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class ProfileRuntimeNotificationTests
{
    [Fact]
    public async Task MacroEdit_CancelsBeforePublication_ThenNotifiesRuntimeWithNewRows()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        var input = new FakeInputHookService();
        var runtime = new RecordingProfileRuntimeService(() => { });
        using var vm = new MainViewModel(manager, new FakeDialogService(), new FakeDisplayService(),
            new RecordingColorControlService(), runtime, inputHookService: input);
        await vm.InitializeAsync();
        var model = await manager.AddProfileAsync("Game", "game.exe");
        var owner = vm.Profiles.Single(profile => ReferenceEquals(profile.Model, model));
        owner.Macros.NewMacroCommand.Execute(null);
        var macro = owner.Macros.SelectedMacro!;
        runtime.Changes.Clear();
        while (input.CancelledMacroOwners.TryDequeue(out _)) { }
        var rowsAtCancellation = -1;
        input.MacroPlaybackCancellation = profile => rowsAtCancellation = profile.Macros.Definitions[0].Steps.Length;

        macro.InsertRecording(0, [new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 500 }]);

        Assert.Same(model, Assert.Single(input.CancelledMacroOwners));
        Assert.Equal(0, rowsAtCancellation);
        Assert.Equal((model, ProfileChangeKind.Macros), Assert.Single(runtime.Changes));
        Assert.Equal(500, Assert.Single(model.Macros.Definitions[0].Steps).DurationMs);
        await vm.FlushPendingSavesAsync();
    }

    [Fact]
    public async Task ShortcutConflictNotification_RefreshesEditor_WithoutChangingSavedAssignment()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        var input = new FakeInputHookService();
        using var vm = new MainViewModel(manager, new FakeDialogService(), new FakeDisplayService(),
            new RecordingColorControlService(), inputHookService: input);
        await vm.InitializeAsync();
        var model = await manager.AddProfileAsync("Game", "game.exe");
        var owner = vm.Profiles.Single(profile => ReferenceEquals(profile.Model, model));
        owner.Macros.NewMacroCommand.Execute(null);
        var macro = owner.Macros.SelectedMacro!;
        macro.ShortcutKey = Key.F6;
        macro.ControlModifier = true;
        input.MacroShortcutError = (_, _) => "This key is reserved by an app-level toggle.";

        input.RaiseMacroSessionChanged();

        Assert.Contains("app-level toggle", macro.ValidationMessage);
        Assert.Equal(Key.F6, model.Macros.Definitions[0].ShortcutKey);
        Assert.Equal(ModifierKeys.Control, model.Macros.Definitions[0].ShortcutModifiers);
        input.MacroShortcutError = null;
        input.RaiseMacroSessionChanged();
        Assert.DoesNotContain("app-level toggle", macro.ValidationMessage);
        await vm.FlushPendingSavesAsync();
    }

    [Fact]
    public void AltShortcutSources_MouseAndWheelFirst_ExhaustionDoesNotDuplicate_RowsRetainOwnSource()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var viewModel = new ProfileViewModel(profile, new FakeDisplayService(), new RecordingColorControlService(),
            keyOptions: [Key.B, Key.A, Key.LeftAlt, Key.RightAlt, Key.None, Key.B]);

        for (var i = 0; i < 10; i++) viewModel.AddAltShortcutBinding();

        Assert.Equal(
            Enum.GetValues<AppMouseButton>().Select(InputTrigger.FromMouseButton)
                .Concat([InputTrigger.FromWheel(MouseWheelDirection.Up), InputTrigger.FromWheel(MouseWheelDirection.Down),
                    InputTrigger.FromKey(Key.B), InputTrigger.FromKey(Key.A)]),
            viewModel.AltShortcutBindings.Select(row => row.Source));
        Assert.Empty(viewModel.AvailableAltShortcutSources);
        Assert.All(viewModel.AltShortcutBindings, row => Assert.Equal(row.Source, Assert.Single(row.SelectableSources)));
        var removed = viewModel.AltShortcutBindings[5];
        Assert.Equal(InputTrigger.FromWheel(MouseWheelDirection.Up), removed.Source);
        removed.TapKey = Key.E;
        Assert.Equal(Key.E, profile.AltMouse.WheelUpKey);
        viewModel.RemoveAltShortcutBinding(removed);
        Assert.Null(profile.AltMouse.WheelUpKey);
        Assert.Equal(removed.Source, Assert.Single(viewModel.AvailableAltShortcutSources));
        Assert.All(viewModel.AltShortcutBindings, row => Assert.Contains(removed.Source, row.SelectableSources));
    }

    [Fact]
    public void AltShortcutSources_DefaultCatalog_ListsMouseThenWheelThenKeysWithoutAlt()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var viewModel = new ProfileViewModel(profile, new FakeDisplayService(), new RecordingColorControlService());

        var sources = viewModel.AvailableAltShortcutSources;
        Assert.All(sources.Take(5), source => Assert.Equal(InputTriggerKind.MouseButton, source.Kind));
        Assert.All(sources.Skip(5).Take(2), source => Assert.Equal(InputTriggerKind.MouseWheel, source.Kind));
        Assert.All(sources.Skip(7), source => Assert.Equal(InputTriggerKind.KeyboardKey, source.Kind));
        Assert.DoesNotContain(InputTrigger.FromKey(Key.LeftAlt), sources);
        Assert.DoesNotContain(InputTrigger.FromKey(Key.RightAlt), sources);
        Assert.DoesNotContain(InputTrigger.None, sources);

        viewModel.AddAltShortcutBinding();
        Assert.Equal(InputTrigger.FromMouseButton(AppMouseButton.Left), Assert.Single(viewModel.AltShortcutBindings).Source);
    }

    [Fact]
    public void AltShortcut_SavedRows_LoadAsOneGridWithWheelsTapOnly()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.AltMouse.Bindings[AppMouseButton.Left] = new() { TapKey = Key.A, HoldKey = Key.B };
        profile.AltMouse.WheelUpKey = Key.E;
        profile.AltMouse.WheelDownKey = Key.Q;
        profile.AltKeyboard.Bindings[Key.F13] = new() { TapKey = Key.Z, HoldKey = Key.X };
        using var viewModel = new ProfileViewModel(profile, new FakeDisplayService(), new RecordingColorControlService());

        Assert.Equal(4, viewModel.AltShortcutBindings.Count);
        Assert.Equal(InputTrigger.FromWheel(MouseWheelDirection.Up), Assert.Single(viewModel.AltShortcutBindings, row => row.TapKey == Key.E).Source);
        Assert.Equal(InputTrigger.FromWheel(MouseWheelDirection.Down), Assert.Single(viewModel.AltShortcutBindings, row => row.TapKey == Key.Q).Source);
        Assert.Equal(Key.B, Assert.Single(viewModel.AltShortcutBindings, row => row.TapKey == Key.A).HoldKey);
        var keyRow = Assert.Single(viewModel.AltShortcutBindings, row => row.TapKey == Key.Z);
        Assert.True(keyRow.CanHold);
        Assert.Equal(Key.X, keyRow.HoldKey);
        // F13 is outside the editor's list, so only its own row offers it, first.
        Assert.Equal(InputTrigger.FromKey(Key.F13), keyRow.SelectableSources.First());
        Assert.DoesNotContain(InputTrigger.FromKey(Key.F13), viewModel.AvailableAltShortcutSources);
        Assert.All(viewModel.AltShortcutBindings, row => Assert.Contains(row.Source, row.SelectableSources));
        Assert.All(viewModel.AltShortcutBindings.Where(row => row.Source.Kind == InputTriggerKind.MouseWheel),
            row => { Assert.False(row.CanHold); Assert.Equal(Key.None, row.HoldKey); });
    }

    [Fact]
    public void AltShortcut_ToggleThresholdAndReset_WriteBothObjectsWithOneEvent()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var viewModel = new ProfileViewModel(profile, new FakeDisplayService(), new RecordingColorControlService());
        var changes = new List<ProfileChangeKind>();
        viewModel.ProfileChanged += (_, e) => changes.Add(e.Kind);
        const ProfileChangeKind both = ProfileChangeKind.AltMouse | ProfileChangeKind.AltKeyboard;

        viewModel.AltShortcut.IsEnabled = true;
        Assert.True(profile.AltMouse.IsEnabled && profile.AltKeyboard.IsEnabled);
        Assert.Equal(both, Assert.Single(changes));

        viewModel.AltShortcut.HoldThresholdMilliseconds = 5;
        Assert.Equal(10, viewModel.AltShortcut.HoldThresholdMilliseconds);
        Assert.Equal((10, 10), (profile.AltMouse.HoldThresholdMilliseconds, profile.AltKeyboard.HoldThresholdMilliseconds));

        viewModel.AltShortcut.ResetHoldThresholdCommand.Execute(null);
        Assert.Equal((200, 200), (profile.AltMouse.HoldThresholdMilliseconds, profile.AltKeyboard.HoldThresholdMilliseconds));
        Assert.Equal(new[] { both, both, both }, changes);

        viewModel.AltShortcut.IsEnabled = false;
        Assert.False(profile.AltMouse.IsEnabled || profile.AltKeyboard.IsEnabled);
        Assert.Equal(4, changes.Count);
    }

    [Fact]
    public void AltShortcut_TriggerEdits_ReportTheFamiliesTheyTouch()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.AltKeyboard.Bindings[Key.Q] = new() { TapKey = Key.E, HoldKey = Key.F };
        using var viewModel = new ProfileViewModel(profile, new FakeDisplayService(), new RecordingColorControlService());
        var row = Assert.Single(viewModel.AltShortcutBindings);
        var changes = new List<ProfileChangeKind>();
        viewModel.ProfileChanged += (_, e) => changes.Add(e.Kind);
        const ProfileChangeKind both = ProfileChangeKind.AltMouse | ProfileChangeKind.AltKeyboard;

        row.HoldKey = Key.G;
        row.Source = InputTrigger.FromKey(Key.W);
        Assert.Equal(new[] { ProfileChangeKind.AltKeyboard, ProfileChangeKind.AltKeyboard }, changes);
        Assert.Equal(Key.G, Assert.Single(profile.AltKeyboard.Bindings, pair => pair.Key == Key.W).Value.HoldKey);

        // Key -> mouse button: Tap/Hold kept, the old key entry is gone, both families are reported.
        row.Source = InputTrigger.FromMouseButton(AppMouseButton.Middle);
        Assert.Equal(both, changes[^1]);
        Assert.Empty(profile.AltKeyboard.Bindings);
        Assert.Equal((Key.E, Key.G), (profile.AltMouse.Bindings[AppMouseButton.Middle].TapKey!.Value, profile.AltMouse.Bindings[AppMouseButton.Middle].HoldKey!.Value));

        // Mouse -> key -> wheel: Hold is cleared before the wheel edit is reported.
        row.Source = InputTrigger.FromKey(Key.R);
        Assert.Equal(both, changes[^1]);
        Assert.Empty(profile.AltMouse.Bindings);
        row.Source = InputTrigger.FromWheel(MouseWheelDirection.Down);
        Assert.Equal(both, changes[^1]);
        Assert.Equal(Key.None, row.HoldKey);
        Assert.Empty(profile.AltKeyboard.Bindings);
        Assert.Equal(Key.E, profile.AltMouse.WheelDownKey);
        Assert.Equal(5, changes.Count);

        // A picker refresh changes no model and raises no profile event.
        row.SelectableSources = [];
        Assert.Equal(5, changes.Count);
    }

    [Theory]
    [InlineData(MouseWheelDirection.Up)]
    [InlineData(MouseWheelDirection.Down)]
    public void AltShortcut_ChangingBetweenButtonAndWheel_MovesTapAndClearsHold(MouseWheelDirection direction)
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.AltMouse.Bindings[AppMouseButton.Left] = new() { TapKey = Key.E, HoldKey = Key.F };
        using var viewModel = new ProfileViewModel(profile, new FakeDisplayService(), new RecordingColorControlService());
        var row = Assert.Single(viewModel.AltShortcutBindings);
        var changes = new List<ProfileChangeKind>();
        viewModel.ProfileChanged += (_, e) => changes.Add(e.Kind);
        var otherDirection = direction == MouseWheelDirection.Up ? MouseWheelDirection.Down : MouseWheelDirection.Up;

        row.Source = InputTrigger.FromWheel(direction);

        Assert.Equal(ProfileChangeKind.AltMouse, Assert.Single(changes));
        Assert.False(row.CanHold);
        Assert.Equal(Key.None, row.HoldKey);
        Assert.Empty(profile.AltMouse.Bindings);
        Assert.Equal(Key.E, direction == MouseWheelDirection.Up ? profile.AltMouse.WheelUpKey : profile.AltMouse.WheelDownKey);
        Assert.Contains(row.Source, row.SelectableSources);
        Assert.Contains(InputTrigger.FromMouseButton(AppMouseButton.Left), viewModel.AvailableAltShortcutSources);
        row.HoldKey = Key.F;
        Assert.Equal(Key.None, row.HoldKey);
        Assert.Single(changes);

        row.Source = InputTrigger.FromWheel(otherDirection);
        Assert.Null(direction == MouseWheelDirection.Up ? profile.AltMouse.WheelUpKey : profile.AltMouse.WheelDownKey);
        Assert.Equal(Key.E, otherDirection == MouseWheelDirection.Up ? profile.AltMouse.WheelUpKey : profile.AltMouse.WheelDownKey);
        row.TapKey = Key.None;
        Assert.Null(profile.AltMouse.WheelUpKey);
        Assert.Null(profile.AltMouse.WheelDownKey);
        Assert.Single(viewModel.AltShortcutBindings);
        row.TapKey = Key.Q;

        row.Source = InputTrigger.FromMouseButton(AppMouseButton.Right);
        Assert.True(row.CanHold);
        Assert.Equal(Key.None, row.HoldKey);
        Assert.Null(profile.AltMouse.WheelUpKey);
        Assert.Null(profile.AltMouse.WheelDownKey);
        Assert.Equal(Key.Q, profile.AltMouse.Bindings[AppMouseButton.Right].TapKey);
        Assert.Null(profile.AltMouse.Bindings[AppMouseButton.Right].HoldKey);
        row.HoldKey = Key.G;
        Assert.Equal(Key.G, profile.AltMouse.Bindings[AppMouseButton.Right].HoldKey);
    }

    [Fact]
    public void CombinedSources_ExhaustionDoesNotDuplicate_RowsRetainOwnSource()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var viewModel = new ProfileViewModel(profile, new FakeDisplayService(),
            new RecordingColorControlService(), keyOptions: [Key.B, Key.A]);
        Assert.Equal(new[]
        {
            InputTrigger.FromKey(Key.A), InputTrigger.FromKey(Key.B),
            InputTrigger.FromWheel(MouseWheelDirection.Up), InputTrigger.FromWheel(MouseWheelDirection.Down)
        }, viewModel.AvailableCombinedSources);

        for (var i = 0; i < 5; i++) viewModel.AddCombinedMapping();

        Assert.Equal(4, viewModel.CombinedMappings.Count);
        Assert.Empty(viewModel.AvailableCombinedSources);
        Assert.All(viewModel.CombinedMappings, row => Assert.Equal(row.Source, Assert.Single(row.SelectableSources)));
        var removed = viewModel.CombinedMappings[2];
        viewModel.RemoveCombinedMapping(removed);
        Assert.Equal(InputTrigger.FromWheel(MouseWheelDirection.Up), Assert.Single(viewModel.AvailableCombinedSources));
        Assert.All(viewModel.CombinedMappings, row => Assert.Contains(removed.Source, row.SelectableSources));
    }

    [Fact]
    public async Task AddCombinedCommand_ExhaustionAndRemoval_NotifyAvailability()
    {
        var store = new InMemoryProfileStore();
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        store.Profiles.Add(profile);
        var viewModel = new MainViewModel(new ProfileManager(store), new FakeDialogService(),
            new FakeDisplayService(), new RecordingColorControlService());
        await viewModel.InitializeAsync();
        var game = Assert.Single(viewModel.Profiles, vm => ReferenceEquals(vm.Model, profile));
        viewModel.SelectedProfile = game;
        var notifiedAvailability = new List<bool>();
        viewModel.AddCombinedMappingCommand.CanExecuteChanged += (_, _) =>
            notifiedAvailability.Add(viewModel.AddCombinedMappingCommand.CanExecute(null));

        var sourceCount = game.AvailableCombinedSources.Count;
        for (var i = 0; i < sourceCount; i++) viewModel.AddCombinedMappingCommand.Execute(null);

        Assert.False(viewModel.AddCombinedMappingCommand.CanExecute(null));
        Assert.False(notifiedAvailability[^1]);
        game.AddCombinedMapping();
        Assert.Equal(sourceCount, game.CombinedMappings.Count);
        game.RemoveCombinedMapping(game.CombinedMappings[^1]);
        Assert.True(viewModel.AddCombinedMappingCommand.CanExecute(null));
        Assert.True(notifiedAvailability[^1]);

        viewModel.SelectedProfile = Assert.Single(viewModel.Profiles, vm => vm.IsWindowsProfile);
        Assert.False(viewModel.AddCombinedMappingCommand.CanExecute(null));
        Assert.Equal(0, await viewModel.FlushPendingSavesAsync());
    }

    [Fact]
    public async Task WheelEdits_PublishSpecificRuntimeChangesAndDetachedAutosave()
    {
        var store = new InMemoryProfileStore();
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.AltMouse.WheelUpKey = Key.R;
        store.Profiles.Add(profile);
        var runtime = new RecordingProfileRuntimeService(() => { });
        var viewModel = new MainViewModel(new ProfileManager(store), new FakeDialogService(),
            new FakeDisplayService(), new RecordingColorControlService(), runtime);
        await viewModel.InitializeAsync();
        var game = Assert.Single(viewModel.Profiles, vm => ReferenceEquals(vm.Model, profile));
        var upRow = Assert.Single(game.AltShortcutBindings);
        Assert.Equal(InputTrigger.FromWheel(MouseWheelDirection.Up), upRow.Source);
        Assert.Equal(Key.R, upRow.TapKey);
        Assert.Empty(runtime.Changes);

        upRow.TapKey = Key.E;
        var downRow = new AltShortcutBindingEntryViewModel(InputTrigger.FromWheel(MouseWheelDirection.Down), Key.Q, null);
        game.AltShortcutBindings.Add(downRow);
        game.AddCombinedMapping();
        var row = Assert.Single(game.CombinedMappings);
        row.Source = InputTrigger.FromWheel(MouseWheelDirection.Down);
        Assert.Equal(new[] { ProfileChangeKind.AltMouse, ProfileChangeKind.AltMouse,
            ProfileChangeKind.CombinedMappings, ProfileChangeKind.CombinedMappings },
            runtime.Changes.Select(change => change.Kind));

        profile.AltMouse.WheelUpKey = Key.Z;
        Assert.Equal(0, await viewModel.FlushPendingSavesAsync());
        var saved = store.SavedProfiles.Last();
        Assert.Equal(Key.E, saved.AltMouse.WheelUpKey);
        Assert.Equal(Key.Q, saved.AltMouse.WheelDownKey);
        Assert.Equal(InputTrigger.FromWheel(MouseWheelDirection.Down), Assert.Single(saved.CombinedMappings.Mappings).Source);

        downRow.TapKey = Key.None;
        Assert.Null(profile.AltMouse.WheelDownKey);
        Assert.Equal(0, await viewModel.FlushPendingSavesAsync());
    }

    [Fact]
    public void HoldBreathPanicEditor_RejectsWheelAndKeepsKeyboardInput()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var viewModel = new ProfileViewModel(profile, new FakeDisplayService(), new RecordingColorControlService());
        viewModel.RightClickHoldBreathPanicTrigger = InputTrigger.FromKey(Key.E);
        Assert.Equal(InputTrigger.FromKey(Key.E), profile.RightClickHoldBreath.PanicTrigger);
        viewModel.RightClickHoldBreathPanicTrigger = InputTrigger.FromWheel(MouseWheelDirection.Up);
        Assert.Equal(InputTrigger.None, profile.RightClickHoldBreath.PanicTrigger);
    }

    [Fact]
    public void CapsLockRemapAvailability_FollowsModeAndToggle()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var viewModel = new ProfileViewModel(
            profile,
            new FakeDisplayService(),
            new RecordingColorControlService());

        viewModel.CapsLockMode = CapsLockMode.Normal;
        Assert.True(viewModel.CanRemapCapsLock);
        Assert.False(viewModel.CanSelectCapsLockRemapKey);

        viewModel.CapsLockRemapEnabled = true;
        Assert.True(viewModel.CanSelectCapsLockRemapKey);

        viewModel.CapsLockMode = CapsLockMode.Disabled;
        Assert.False(viewModel.CanRemapCapsLock);
        Assert.False(viewModel.CanSelectCapsLockRemapKey);

        viewModel.CapsLockRemapKey = Key.None;
        Assert.Null(profile.CapsLock.RemapTarget);
    }

    [Fact]
    public async Task ManualSave_WithoutNewEdit_DoesNotReconcileRuntimeState()
    {
        var store = new InMemoryProfileStore();
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        store.Profiles.Add(profile);
        var manager = new ProfileManager(store);
        var runtime = new RecordingProfileRuntimeService(() => { });
        var viewModel = new MainViewModel(
            manager,
            new FakeDialogService(),
            new FakeDisplayService(),
            new RecordingColorControlService(),
            runtime);

        await viewModel.InitializeAsync();
        viewModel.SelectedProfile = Assert.Single(
            viewModel.Profiles,
            x => ReferenceEquals(x.Model, profile));

        await viewModel.SaveProfileCommand.ExecuteAsync(null);

        Assert.Empty(runtime.Changes);
    }

    [Theory]
    [InlineData(ProfileChangeKind.Master)]
    [InlineData(ProfileChangeKind.Identity)]
    [InlineData(ProfileChangeKind.AltMouse)]
    [InlineData(ProfileChangeKind.AltKeyboard)]
    [InlineData(ProfileChangeKind.AltMouse | ProfileChangeKind.AltKeyboard)]
    [InlineData(ProfileChangeKind.CombinedMappings)]
    [InlineData(ProfileChangeKind.HoldBreath)]
    [InlineData(ProfileChangeKind.AutoRun)]
    [InlineData(ProfileChangeKind.RapidFire)]
    [InlineData(ProfileChangeKind.AntiAfk)]
    [InlineData(ProfileChangeKind.CapsLock)]
    [InlineData(ProfileChangeKind.WindowsLauncher)]
    [InlineData(ProfileChangeKind.Color)]
    [InlineData(ProfileChangeKind.Crosshair)]
    public async Task ProfileEdit_ForwardsSpecificRuntimeChangeBeforeAutosave(
        ProfileChangeKind expectedKind)
    {
        var store = new InMemoryProfileStore();
        var order = new ConcurrentQueue<string>();
        store.Saving = () => order.Enqueue("save");
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        store.Profiles.Add(profile);
        var manager = new ProfileManager(store);
        var runtime = new RecordingProfileRuntimeService(
            () => order.Enqueue("runtime"));
        var viewModel = new MainViewModel(
            manager,
            new FakeDialogService(),
            new FakeDisplayService(),
            new RecordingColorControlService(),
            runtime);

        await viewModel.InitializeAsync();
        var game = Assert.Single(viewModel.Profiles, x => ReferenceEquals(x.Model, profile));

        ApplyEdit(game, expectedKind);

        var change = Assert.Single(runtime.Changes);
        Assert.Same(profile, change.Profile);
        Assert.Equal(expectedKind, change.Kind);

        Assert.Equal(0, await viewModel.FlushPendingSavesAsync());
        Assert.Equal(new[] { "runtime", "save" }, order.ToArray());
    }

    [Fact]
    public async Task DefaultDisplayEdit_NotifiesColorOnce_WithoutDirectHardwareWrite()
    {
        var manager = new ProfileManager(new InMemoryProfileStore());
        await manager.InitializeAsync();
        var profile = manager.WindowsProfile;
        profile.ColorSettings.IsEnabled = true;
        profile.ColorSettings.SetProfile(new DisplayColorProfile
        {
            DisplayId = "DISPLAY1", IsEnabled = true, Brightness = 30
        });
        var runtime = new RecordingProfileRuntimeService(() => { });
        var color = new RecordingColorControlService();
        var viewModel = new MainViewModel(manager, new FakeDialogService(),
            new FakeDisplayService
            {
                Displays = [new DisplayInfo { Id = "DISPLAY1", Name = "Monitor", DeviceName = "DISPLAY1" }]
            }, color, runtime);
        await viewModel.InitializeAsync();
        var editor = Assert.Single(viewModel.Profiles, vm => ReferenceEquals(vm.Model, profile));

        editor.ColorSettings.DisplayViewModels.Single().Brightness = 80;

        var change = Assert.Single(runtime.Changes);
        Assert.Same(profile, change.Profile);
        Assert.Equal(ProfileChangeKind.Color, change.Kind);
        Assert.Empty(color.AppliedProfiles);
        Assert.Equal(0, await viewModel.FlushPendingSavesAsync());
    }

    private static void ApplyEdit(
        ProfileViewModel profile,
        ProfileChangeKind changeKind)
    {
        switch (changeKind)
        {
            case ProfileChangeKind.Master:
                profile.IsEnabled = false;
                break;
            case ProfileChangeKind.Identity:
                profile.Executable = "other.exe";
                break;
            case ProfileChangeKind.AltMouse:
                profile.AltShortcutBindings.Add(new AltShortcutBindingEntryViewModel(InputTrigger.FromMouseButton(AppMouseButton.Left), Key.E, null));
                break;
            case ProfileChangeKind.AltKeyboard:
                profile.AltShortcutBindings.Add(new AltShortcutBindingEntryViewModel(InputTrigger.FromKey(Key.Q), Key.E, null));
                break;
            case ProfileChangeKind.AltMouse | ProfileChangeKind.AltKeyboard:
                profile.AltShortcut.IsEnabled = true;
                break;
            case ProfileChangeKind.CombinedMappings:
                profile.CombinedKeyMappingsEnabled = true;
                break;
            case ProfileChangeKind.HoldBreath:
                profile.RightClickHoldBreathEnabled = true;
                break;
            case ProfileChangeKind.AutoRun:
                profile.AutoRunEnabled = true;
                break;
            case ProfileChangeKind.RapidFire:
                profile.RapidFireEnabled = true;
                break;
            case ProfileChangeKind.AntiAfk:
                profile.AntiAfkEnabled = true;
                break;
            case ProfileChangeKind.CapsLock:
                profile.CapsLockEnabled = true;
                break;
            case ProfileChangeKind.WindowsLauncher:
                profile.WindowsLauncherEnabled = false;
                break;
            case ProfileChangeKind.Color:
                profile.ColorSettings.IsEnabled = true;
                break;
            case ProfileChangeKind.Crosshair:
                profile.CrosshairEnabled = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changeKind));
        }
    }

    [Fact]
    public void RapidFireTiming_ClampsAndReportsInclusiveRange()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var viewModel = new ProfileViewModel(
            profile,
            new FakeDisplayService(),
            new RecordingColorControlService());
        var changes = new List<ProfileChangeKind>();
        var rangeNotifications = 0;
        viewModel.ProfileChanged += (_, e) => changes.Add(e.Kind);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileViewModel.RapidFireTimingRange))
            {
                rangeNotifications++;
            }
        };

        Assert.False(viewModel.RapidFireEnabled);
        Assert.Equal("90–100 ms", viewModel.RapidFireTimingRange);

        viewModel.RapidFireIntervalMilliseconds = 500;
        viewModel.RapidFireJitterMilliseconds = -1;

        Assert.Equal(250, profile.RapidFire.IntervalMilliseconds);
        Assert.Equal(0, profile.RapidFire.JitterMilliseconds);
        Assert.Equal("250–250 ms", viewModel.RapidFireTimingRange);
        Assert.Equal(2, rangeNotifications);
        Assert.Equal(
            [ProfileChangeKind.RapidFire, ProfileChangeKind.RapidFire],
            changes);
    }

    [Fact]
    public void CrosshairSizeAdjustment_ClampsNotifiesAndDedups()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var viewModel = new ProfileViewModel(
            profile,
            new FakeDisplayService(),
            new RecordingColorControlService());
        var changes = new List<ProfileChangeKind>();
        viewModel.ProfileChanged += (_, e) => changes.Add(e.Kind);
        var notifications = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileViewModel.CrosshairSizeAdjustment))
            {
                notifications++;
            }
        };

        Assert.Equal(0, viewModel.CrosshairSizeAdjustment);

        viewModel.CrosshairSizeAdjustment = 999;
        Assert.Equal(CrosshairSettings.MaxSizeAdjustment, profile.Crosshair.SizeAdjustment);

        viewModel.CrosshairSizeAdjustment = -999;
        Assert.Equal(CrosshairSettings.MinSizeAdjustment, profile.Crosshair.SizeAdjustment);

        viewModel.CrosshairSizeAdjustment = 12;
        Assert.Equal(12, profile.Crosshair.SizeAdjustment);
        // Each distinct clamped result (50, -50, 12) notifies once.
        Assert.Equal(3, notifications);
        Assert.Equal(
            [ProfileChangeKind.Crosshair, ProfileChangeKind.Crosshair, ProfileChangeKind.Crosshair],
            changes);

        // Same value: no notification, no change event.
        viewModel.CrosshairSizeAdjustment = 12;
        Assert.Equal(3, notifications);
        Assert.Equal(3, changes.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CrosshairOffset_ClampsNotifiesDedupsAndResets(bool horizontal)
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var viewModel = new ProfileViewModel(
            profile,
            new FakeDisplayService(),
            new RecordingColorControlService());
        Action<int> setOffset = horizontal
            ? value => viewModel.CrosshairOffsetX = value
            : value => viewModel.CrosshairOffsetY = value;
        Func<int> getOffset = horizontal
            ? () => profile.Crosshair.OffsetX
            : () => profile.Crosshair.OffsetY;
        var propertyName = horizontal
            ? nameof(ProfileViewModel.CrosshairOffsetX)
            : nameof(ProfileViewModel.CrosshairOffsetY);
        var resetCommand = horizontal
            ? viewModel.ResetCrosshairOffsetXCommand
            : viewModel.ResetCrosshairOffsetYCommand;
        var changes = new List<ProfileChangeKind>();
        viewModel.ProfileChanged += (_, e) => changes.Add(e.Kind);
        var notifications = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == propertyName)
            {
                notifications++;
            }
        };

        Assert.Equal(0, getOffset());
        setOffset(999);
        Assert.Equal(500, getOffset());
        setOffset(-999);
        Assert.Equal(-500, getOffset());
        setOffset(25);
        Assert.Equal(25, getOffset());
        setOffset(25);
        Assert.Equal(3, notifications);
        Assert.Equal(3, changes.Count);

        resetCommand.Execute(null);
        Assert.Equal(0, getOffset());
        Assert.Equal(4, notifications);
        Assert.Equal(4, changes.Count);
        Assert.All(changes, kind => Assert.Equal(ProfileChangeKind.Crosshair, kind));
    }

    [Fact]
    public async Task RemoveAllBindings_DetachesRemovedEntriesAndKeepsModelsEmpty()
    {
        var store = new InMemoryProfileStore();
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.CombinedMappings.Mappings =
        [
            new() { Source = InputTrigger.FromKey(Key.A), TargetKey = Key.B },
            new() { Source = InputTrigger.FromKey(Key.C), TargetKey = Key.D }
        ];
        profile.AltMouse.Bindings = new Dictionary<AppMouseButton, MouseButtonBinding>
        {
            [AppMouseButton.Left] = new() { TapKey = Key.E },
            [AppMouseButton.Right] = new() { HoldKey = Key.F }
        };
        profile.AltMouse.WheelUpKey = Key.K;
        profile.AltMouse.WheelDownKey = Key.L;
        profile.AltKeyboard.Bindings = new Dictionary<Key, AltKeyboardBinding>
        {
            [Key.G] = new() { TapKey = Key.H },
            [Key.I] = new() { HoldKey = Key.J }
        };
        store.Profiles.Add(profile);

        var viewModel = new MainViewModel(
            new ProfileManager(store),
            new FakeDialogService(),
            new FakeDisplayService(),
            new RecordingColorControlService());
        await viewModel.InitializeAsync();
        var game = Assert.Single(viewModel.Profiles, candidate => ReferenceEquals(candidate.Model, profile));
        viewModel.SelectedProfile = game;

        var removedCombined = game.CombinedMappings.ToArray();
        var removedAlt = game.AltShortcutBindings.ToArray();
        Assert.Equal(6, removedAlt.Length);
        var changes = new List<ProfileChangeKind>();
        game.ProfileChanged += (_, e) => changes.Add(e.Kind);

        viewModel.RemoveAllCombinedMappingsCommand.Execute(null);
        viewModel.RemoveAllAltShortcutBindingsCommand.Execute(null);

        Assert.Empty(profile.CombinedMappings.Mappings);
        Assert.Empty(profile.AltMouse.Bindings);
        Assert.Null(profile.AltMouse.WheelUpKey);
        Assert.Null(profile.AltMouse.WheelDownKey);
        Assert.Empty(profile.AltKeyboard.Bindings);
        // One event per removed row, each naming that row's family.
        Assert.Equal(
            new[] { ProfileChangeKind.AltMouse, ProfileChangeKind.AltMouse, ProfileChangeKind.AltMouse, ProfileChangeKind.AltMouse,
                ProfileChangeKind.AltKeyboard, ProfileChangeKind.AltKeyboard },
            changes.Where(kind => kind != ProfileChangeKind.CombinedMappings));
        viewModel.RemoveAllAltShortcutBindingsCommand.Execute(null);
        Assert.Equal(8, changes.Count);

        changes.Clear();
        removedCombined[0].TargetKey = Key.Z;
        removedAlt[0].TapKey = Key.Z;
        removedAlt[2].TapKey = Key.Z;
        removedAlt[^1].HoldKey = Key.Z;

        Assert.Empty(profile.CombinedMappings.Mappings);
        Assert.Empty(profile.AltMouse.Bindings);
        Assert.Null(profile.AltMouse.WheelUpKey);
        Assert.Null(profile.AltMouse.WheelDownKey);
        Assert.Empty(profile.AltKeyboard.Bindings);
        Assert.Empty(changes);
    }

    private sealed class RecordingProfileRuntimeService(
        Action onChange) : IProfileRuntimeService
    {
        public List<(Profile Profile, ProfileChangeKind Kind)> Changes { get; } = [];

        public List<(ColorSettings Settings, ColorVariant Variant)> ForcedPreviews { get; } = [];

        public int ClearedPreviews { get; private set; }

        public void NotifyProfileChanged(Profile profile, ProfileChangeKind changeKind)
        {
            onChange();
            Changes.Add((profile, changeKind));
        }

        public void SetForcedColorPreview(ColorSettings settings, ColorVariant variant)
        {
            ForcedPreviews.Add((settings, variant));
        }

        public void ClearForcedColorPreview()
        {
            ClearedPreviews++;
        }
    }
}
