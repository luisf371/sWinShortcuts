using System.Collections.Concurrent;
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
            viewModel.Profiles.Where(x => ReferenceEquals(x.Model, profile)));

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
    [InlineData(ProfileChangeKind.AntiAfk)]
    [InlineData(ProfileChangeKind.CapsLock)]
    [InlineData(ProfileChangeKind.WindowsLauncher)]
    [InlineData(ProfileChangeKind.Color)]
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
        var game = Assert.Single(viewModel.Profiles.Where(x => ReferenceEquals(x.Model, profile)));

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
            default:
                throw new ArgumentOutOfRangeException(nameof(changeKind));
        }
    }

    private sealed class RecordingProfileRuntimeService(
        Action onChange) : IProfileRuntimeService
    {
        public List<(Profile Profile, ProfileChangeKind Kind)> Changes { get; } = [];

        public void NotifyProfileChanged(Profile profile, ProfileChangeKind changeKind)
        {
            onChange();
            Changes.Add((profile, changeKind));
        }
    }
}
