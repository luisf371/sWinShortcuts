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

    [Fact]
    public void SnapshotProfiles_ReturnsActiveVariant_AndExplicitVariantIsIndependent()
    {
        var settings = new ColorSettings { HasSecondary = true };
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1", IsEnabled = true, Brightness = 30 }, ColorVariant.Primary);
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1", IsEnabled = true, Brightness = 88 }, ColorVariant.Secondary);

        Assert.Equal(30, settings.SnapshotProfiles()["D1"].Brightness);                  // active starts Primary
        Assert.Equal(ColorVariant.Secondary, settings.ToggleVariant());
        Assert.Equal(88, settings.SnapshotProfiles()["D1"].Brightness);                  // active now Secondary
        Assert.Equal(30, settings.SnapshotProfiles(ColorVariant.Primary)["D1"].Brightness); // explicit Primary intact
    }

    [Fact]
    public void ToggleVariant_IsNoOp_WhenNoSecondaryConfigured()
    {
        var settings = new ColorSettings { HasSecondary = false };
        Assert.Equal(ColorVariant.Primary, settings.ToggleVariant());
        Assert.Equal(ColorVariant.Primary, settings.ActiveVariant);
    }

    [Fact]
    public void SnapshotProfiles_FallsBackToPrimary_WhenSecondaryActiveButNotConfigured()
    {
        // Defensive: even if ActiveVariant is Secondary, an un-configured secondary must apply Primary — never
        // a blank secondary that would wipe the calibrated look.
        var settings = new ColorSettings { HasSecondary = false };
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1", IsEnabled = true, Brightness = 42 }, ColorVariant.Primary);
        settings.SetActiveVariant(ColorVariant.Secondary);

        Assert.Equal(42, settings.SnapshotProfiles()["D1"].Brightness);
    }

    [Fact]
    public void ToggleVariant_StaysPrimary_WhenSecondaryEnabledButEmpty()
    {
        // codex CRITICAL: HasSecondary=true but an EMPTY secondary store must NOT become active (it would
        // apply a blank/neutral plan and wipe the calibrated Primary look).
        var settings = new ColorSettings { HasSecondary = true };
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1", IsEnabled = true, Brightness = 55 }, ColorVariant.Primary);

        settings.ToggleVariant();
        Assert.Equal(ColorVariant.Primary, settings.ActiveVariant);
        Assert.Equal(55, settings.SnapshotProfiles()["D1"].Brightness); // still the Primary look
    }

    [Fact]
    public void EnsureSecondaryInitialized_SeedsFromPrimary_ThenTogglesCleanly()
    {
        var settings = new ColorSettings { HasSecondary = true };
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1", IsEnabled = true, Brightness = 33, DigitalVibrance = 70 }, ColorVariant.Primary);

        settings.EnsureSecondaryInitialized();

        var seeded = settings.SnapshotProfiles(ColorVariant.Secondary)["D1"];
        Assert.Equal(33, seeded.Brightness);
        Assert.Equal(70, seeded.DigitalVibrance);

        // Now the secondary is populated -> toggling actually switches (and applies the seeded copy).
        Assert.Equal(ColorVariant.Secondary, settings.ToggleVariant());
        Assert.Equal(33, settings.SnapshotProfiles()["D1"].Brightness);
    }

    [Fact]
    public void EnsureSecondaryInitialized_FillsMissingDisplays_PreservesExisting()
    {
        // codex CRITICAL: a PARTIAL secondary (Primary D1+D2, Secondary only D1) must not leave D2 blank —
        // the editor would materialize a disabled D2 that a toggle applies as a neutral plan, wiping D2.
        var settings = new ColorSettings { HasSecondary = true };
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1", IsEnabled = true, Brightness = 20 }, ColorVariant.Primary);
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D2", IsEnabled = true, Brightness = 40 }, ColorVariant.Primary);
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1", IsEnabled = true, Brightness = 99 }, ColorVariant.Secondary);

        settings.EnsureSecondaryInitialized();

        var sec = settings.SnapshotProfiles(ColorVariant.Secondary);
        Assert.Equal(99, sec["D1"].Brightness); // existing Secondary D1 preserved (not overwritten)
        Assert.Equal(40, sec["D2"].Brightness); // D2 filled from Primary (was missing) — never blank/disabled
        Assert.True(sec["D2"].IsEnabled);
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
