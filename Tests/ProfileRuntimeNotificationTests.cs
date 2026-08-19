using System.Collections.Concurrent;
using System.Windows.Input;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class ProfileRuntimeNotificationTests
{
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
