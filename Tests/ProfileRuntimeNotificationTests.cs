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
    public void AltMouseSources_ExhaustionDoesNotDuplicate_RowsRetainOwnSource()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        using var viewModel = new ProfileViewModel(profile, new FakeDisplayService(), new RecordingColorControlService());

        for (var i = 0; i < 8; i++) viewModel.AddAltMouseBinding();

        Assert.Equal(7, viewModel.AltMouseBindings.Count);
        Assert.Empty(viewModel.AvailableAltMouseSources);
        Assert.All(viewModel.AltMouseBindings, row => Assert.Equal(row.Source, Assert.Single(row.SelectableSources)));
        var removed = viewModel.AltMouseBindings[5];
        Assert.Equal(InputTrigger.FromWheel(MouseWheelDirection.Up), removed.Source);
        removed.TapKey = Key.E;
        viewModel.RemoveAltMouseBinding(removed);
        Assert.Null(profile.AltMouse.WheelUpKey);
        Assert.Equal(removed.Source, Assert.Single(viewModel.AvailableAltMouseSources));
        Assert.All(viewModel.AltMouseBindings, row => Assert.Contains(removed.Source, row.SelectableSources));
    }

    [Fact]
    public void AltMouse_SavedWheelTargets_LoadAsTapOnlyRows()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.AltMouse.Bindings[AppMouseButton.Left] = new() { TapKey = Key.A, HoldKey = Key.B };
        profile.AltMouse.WheelUpKey = Key.E;
        profile.AltMouse.WheelDownKey = Key.Q;
        using var viewModel = new ProfileViewModel(profile, new FakeDisplayService(), new RecordingColorControlService());

        Assert.Equal(3, viewModel.AltMouseBindings.Count);
        Assert.Equal(InputTrigger.FromWheel(MouseWheelDirection.Up), Assert.Single(viewModel.AltMouseBindings, row => row.TapKey == Key.E).Source);
        Assert.Equal(InputTrigger.FromWheel(MouseWheelDirection.Down), Assert.Single(viewModel.AltMouseBindings, row => row.TapKey == Key.Q).Source);
        Assert.Equal(Key.B, Assert.Single(viewModel.AltMouseBindings, row => row.TapKey == Key.A).HoldKey);
        Assert.All(viewModel.AltMouseBindings, row => Assert.Contains(row.Source, row.SelectableSources));
        Assert.All(viewModel.AltMouseBindings.Where(row => row.Source.Kind == InputTriggerKind.MouseWheel),
            row => { Assert.False(row.CanHold); Assert.Equal(Key.None, row.HoldKey); });
    }

    [Theory]
    [InlineData(MouseWheelDirection.Up)]
    [InlineData(MouseWheelDirection.Down)]
    public void AltMouse_ChangingBetweenButtonAndWheel_MovesTapAndClearsHold(MouseWheelDirection direction)
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.AltMouse.Bindings[AppMouseButton.Left] = new() { TapKey = Key.E, HoldKey = Key.F };
        using var viewModel = new ProfileViewModel(profile, new FakeDisplayService(), new RecordingColorControlService());
        var row = Assert.Single(viewModel.AltMouseBindings);
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
        Assert.Contains(InputTrigger.FromMouseButton(AppMouseButton.Left), viewModel.AvailableAltMouseSources);
        row.HoldKey = Key.F;
        Assert.Equal(Key.None, row.HoldKey);
        Assert.Single(changes);

        row.Source = InputTrigger.FromWheel(otherDirection);
        Assert.Null(direction == MouseWheelDirection.Up ? profile.AltMouse.WheelUpKey : profile.AltMouse.WheelDownKey);
        Assert.Equal(Key.E, otherDirection == MouseWheelDirection.Up ? profile.AltMouse.WheelUpKey : profile.AltMouse.WheelDownKey);
        row.TapKey = Key.None;
        Assert.Null(profile.AltMouse.WheelUpKey);
        Assert.Null(profile.AltMouse.WheelDownKey);
        Assert.Single(viewModel.AltMouseBindings);
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
        var upRow = Assert.Single(game.AltMouseBindings);
        Assert.Equal(InputTrigger.FromWheel(MouseWheelDirection.Up), upRow.Source);
        Assert.Equal(Key.R, upRow.TapKey);
        Assert.Empty(runtime.Changes);

        upRow.TapKey = Key.E;
        var downRow = new AltMouseBindingEntryViewModel(InputTrigger.FromWheel(MouseWheelDirection.Down), Key.Q, null);
        game.AltMouseBindings.Add(downRow);
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
                profile.AltMouse.IsEnabled = true;
                break;
            case ProfileChangeKind.AltKeyboard:
                profile.AltKeyboard.IsEnabled = true;
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
        var removedAltMouse = game.AltMouseBindings.ToArray();
        var removedAltKeyboard = game.AltKeyboardBindings.ToArray();
        var changes = new List<ProfileChangeKind>();
        game.ProfileChanged += (_, e) => changes.Add(e.Kind);

        viewModel.RemoveAllCombinedMappingsCommand.Execute(null);
        viewModel.RemoveAllAltMouseBindingsCommand.Execute(null);
        viewModel.RemoveAllAltKeyboardBindingsCommand.Execute(null);

        Assert.Empty(profile.CombinedMappings.Mappings);
        Assert.Empty(profile.AltMouse.Bindings);
        Assert.Null(profile.AltMouse.WheelUpKey);
        Assert.Null(profile.AltMouse.WheelDownKey);
        Assert.Empty(profile.AltKeyboard.Bindings);

        changes.Clear();
        removedCombined[0].TargetKey = Key.Z;
        removedAltMouse[0].TapKey = Key.Z;
        removedAltMouse[^1].TapKey = Key.Z;
        removedAltKeyboard[0].HoldKey = Key.Z;

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
