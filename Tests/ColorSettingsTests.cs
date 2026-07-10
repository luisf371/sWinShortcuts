using System.Linq;
using sWinShortcuts.Models;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

public class ColorSettingsTests
{
    [Fact]
    public void UpdateProfile_MutatesUnderSync_AndSnapshotReflectsIt()
    {
        // F-018: writes route through UpdateProfile (under _sync); a snapshot then deep-copies a coherent value.
        var settings = new ColorSettings();
        settings.SetProfile(new DisplayColorProfile { DisplayId = @"\\.\DISPLAY1", IsEnabled = true, Brightness = 50 });

        settings.UpdateProfile(@"\\.\DISPLAY1", p => { p.Brightness = 70; p.Gamma = 1.5; });

        var snap = settings.SnapshotProfiles()[@"\\.\DISPLAY1"];
        Assert.Equal(70, snap.Brightness);
        Assert.Equal(1.5, snap.Gamma);

        settings.UpdateProfile("unknown", p => p.Brightness = 1); // no-op for an unknown display
        Assert.False(settings.SnapshotProfiles().ContainsKey("unknown"));
    }

    private static (ColorSettingsViewModel vm, RecordingColorControlService color, FakeDisplayService displays) BuildLiveVm()
    {
        var display = new DisplayInfo { Id = @"\\.\DISPLAY1", Name = "Mon", DeviceName = @"\\.\DISPLAY1" };
        var displays = new FakeDisplayService { Displays = [display] };
        var color = new RecordingColorControlService();
        var settings = new ColorSettings { IsEnabled = true };
        var vm = new ColorSettingsViewModel(settings, displays, color, allowLiveUpdates: true);
        color.AppliedProfiles.Clear(); // ignore any construction-time activity
        return (vm, color, displays);
    }

    [Fact]
    public void HotPlugRebuild_DisabledDisplay_MakesNoHardwareCall()
    {
        // F-006: a display-list rebuild (hot-plug) is UI-only — a disabled/never-owned display must get ZERO
        // hardware writes (the old NotifyMasterEnabledChanged pushed neutral gamma/DVC to it).
        var (vm, color, displays) = BuildLiveVm();

        displays.RaiseDisplaysChanged();

        Assert.Empty(color.AppliedProfiles);
        vm.Dispose();
    }

    [Fact]
    public void ExplicitEnableToggle_StillMakesHardwareCall()
    {
        // F-006 did NOT break the intended behavior: an explicit user enable toggle still applies to hardware.
        var (vm, color, _) = BuildLiveVm();
        var displayVm = vm.DisplayViewModels.Single();

        displayVm.IsEnabled = true;

        Assert.NotEmpty(color.AppliedProfiles);
        vm.Dispose();
    }
}
