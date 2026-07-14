using System.Windows.Input;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using Xunit;
using AppMouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class ProfilePersistenceSnapshotTests
{
    [Fact]
    public void Create_LiveModelMutatesAfterCapture_SnapshotRemainsDeepAndCoherent()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.IsEnabled = true;
        profile.AltMouse.IsEnabled = true;
        profile.AltMouse.Bindings[AppMouseButton.Left] = new MouseButtonBinding
        {
            TapKey = Key.A,
            HoldKey = Key.B
        };
        profile.CombinedMappings.Mappings.Add(new CombinedMappingEntry
        {
            SourceKey = Key.C,
            TargetKey = Key.D,
            SuppressOriginalKey = true
        });
        profile.WindowsLauncher.Launchers[Key.E] = new LauncherBinding
        {
            Path = "old.exe",
            Arguments = "--old",
            RunAsAdmin = true
        };
        var display = profile.ColorSettings.GetOrCreateProfile("DISPLAY1");
        display.IsEnabled = true;
        display.Brightness = 61;

        var snapshot = ProfilePersistenceSnapshot.Create(profile);

        profile.Executable = "other.exe";
        profile.IsEnabled = false;
        profile.AltMouse.Bindings[AppMouseButton.Left].TapKey = Key.F;
        profile.CombinedMappings.Mappings[0].TargetKey = Key.G;
        profile.WindowsLauncher.Launchers[Key.E].Path = "new.exe";
        profile.ColorSettings.UpdateProfile(
            "DISPLAY1",
            color => color.Brightness = 99);

        Assert.Equal("game.exe", snapshot.Executable);
        Assert.True(snapshot.IsEnabled);
        Assert.Equal(Key.A, snapshot.AltMouse.Bindings[AppMouseButton.Left].TapKey);
        Assert.Equal(Key.D, snapshot.CombinedMappings.Mappings[0].TargetKey);
        Assert.Equal("old.exe", snapshot.WindowsLauncher.Launchers[Key.E].Path);
        Assert.Equal(
            61,
            snapshot.ColorSettings
                .SnapshotProfiles(ColorVariant.Primary)["DISPLAY1"]
                .Brightness);
    }
}
