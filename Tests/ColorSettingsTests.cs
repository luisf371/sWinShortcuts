using System.Linq;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

public class ColorSettingsTests
{
    [Theory]
    [InlineData(double.NaN, DisplayColorProfile.DefaultGamma)]
    [InlineData(double.PositiveInfinity, DisplayColorProfile.DefaultGamma)]
    [InlineData(double.NegativeInfinity, DisplayColorProfile.DefaultGamma)]
    [InlineData(0.1, 0.5)]
    [InlineData(4.0, 3.0)]
    public void Gamma_InvalidEditorValue_NormalizesModelAndEditor(double gamma, double expected)
    {
        var display = new DisplayInfo { Id = "DISPLAY1", Name = "Monitor", DeviceName = "DISPLAY1" };
        var settings = new ColorSettings();
        var profile = settings.GetOrCreateProfile(display.Id);
        using var vm = new DisplayColorSettingsViewModel(
            display, profile, settings, new RecordingColorControlService(), () => true);

        vm.Gamma = 1.5;
        vm.Gamma = gamma;

        Assert.Equal(expected, vm.Gamma);
        Assert.Equal(expected, settings.SnapshotProfiles()[display.Id].Gamma);
    }

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
    public void ToggleVariant_AllPresetsAvailable_CyclesAndSnapshotsEachPreset()
    {
        var settings = new ColorSettings { HasSecondary = true, HasTertiary = true };
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1", IsEnabled = true, Brightness = 30 }, ColorVariant.Primary);
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1", IsEnabled = true, Brightness = 88 }, ColorVariant.Secondary);
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1", IsEnabled = true, Brightness = 65 }, ColorVariant.Tertiary);

        Assert.Equal(30, settings.SnapshotProfiles()["D1"].Brightness);
        (ColorVariant Variant, int Brightness)[] expected =
        [
            (ColorVariant.Secondary, 88), (ColorVariant.Tertiary, 65),
            (ColorVariant.Primary, 30), (ColorVariant.Secondary, 88)
        ];
        foreach (var (variant, brightness) in expected)
        {
            Assert.Equal(variant, settings.ToggleVariant());
            Assert.Equal(brightness, settings.SnapshotProfiles()["D1"].Brightness);
        }

        Assert.Equal(30, settings.SnapshotProfiles(ColorVariant.Primary)["D1"].Brightness);
    }

    [Fact]
    public void ToggleVariant_PrimaryOnly_StaysOnPrimary()
    {
        var settings = new ColorSettings();
        Assert.Equal(ColorVariant.Primary, settings.ToggleVariant());
        Assert.Equal(ColorVariant.Primary, settings.ActiveVariant);
    }

    [Fact]
    public void ToggleVariant_TertiaryEnabledButEmpty_SkipsTertiary()
    {
        var settings = new ColorSettings { HasSecondary = true, HasTertiary = true };
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1" }, ColorVariant.Secondary);

        Assert.Equal(ColorVariant.Secondary, settings.ToggleVariant());
        Assert.Equal(ColorVariant.Primary, settings.ToggleVariant());
    }

    [Fact]
    public void ToggleVariant_SecondaryDisabledButPopulated_SkipsSecondary()
    {
        var settings = new ColorSettings { HasTertiary = true };
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1" }, ColorVariant.Secondary);
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1" }, ColorVariant.Tertiary);

        Assert.Equal(ColorVariant.Tertiary, settings.ToggleVariant());
        Assert.Equal(ColorVariant.Primary, settings.ToggleVariant());
    }

    [Fact]
    public void ToggleVariant_TertiaryOriginDisabled_AdvancesFromPrimary()
    {
        var settings = new ColorSettings { HasSecondary = true };
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1" }, ColorVariant.Secondary);

        Assert.Equal(ColorVariant.Secondary, settings.ToggleVariant(ColorVariant.Tertiary));
    }

    [Fact]
    public void ToggleVariant_RaisesVariantToggled_OnlyWhenThePresetMoves()
    {
        var settings = new ColorSettings { HasSecondary = true };
        var raised = 0;
        settings.VariantToggled += (_, _) => raised++;

        Assert.Equal(ColorVariant.Primary, settings.ToggleVariant()); // Secondary has no rows yet
        Assert.Equal(0, raised);

        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1" }, ColorVariant.Secondary);
        Assert.Equal(ColorVariant.Secondary, settings.ToggleVariant());
        Assert.Equal(ColorVariant.Primary, settings.ToggleVariant());
        Assert.Equal(2, raised);

        // A previewed origin counts like the toast: Secondary -> Primary moves even though Primary was active.
        Assert.Equal(ColorVariant.Primary, settings.ToggleVariant(ColorVariant.Secondary));
        Assert.Equal(3, raised);
    }

    [Fact]
    public void ActiveVariant_DisablingOptionalPresets_ResetsOnlyTheActiveSlot()
    {
        var settings = new ColorSettings { HasSecondary = true, HasTertiary = true };
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1" }, ColorVariant.Tertiary);
        Assert.Equal(ColorVariant.Tertiary, settings.ToggleVariant());

        settings.HasSecondary = false;
        Assert.Equal(ColorVariant.Tertiary, settings.ActiveVariant);

        settings.HasTertiary = false;
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
    public void EnsureVariantInitialized_SeedsFromPrimary_ThenTogglesCleanly()
    {
        var settings = new ColorSettings { HasSecondary = true };
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1", IsEnabled = true, Brightness = 33, DigitalVibrance = 70 }, ColorVariant.Primary);

        settings.EnsureVariantInitialized(ColorVariant.Secondary);

        var seeded = settings.SnapshotProfiles(ColorVariant.Secondary)["D1"];
        Assert.Equal(33, seeded.Brightness);
        Assert.Equal(70, seeded.DigitalVibrance);

        // Now the secondary is populated -> toggling actually switches (and applies the seeded copy).
        Assert.Equal(ColorVariant.Secondary, settings.ToggleVariant());
        Assert.Equal(33, settings.SnapshotProfiles()["D1"].Brightness);
    }

    [Theory]
    [InlineData(ColorVariant.Secondary)]
    [InlineData(ColorVariant.Tertiary)]
    public void EnsureVariantInitialized_PartiallyPopulated_CopiesMissingRowsAndPreservesEdits(ColorVariant variant)
    {
        var settings = new ColorSettings();
        settings.SetProfile(new DisplayColorProfile { DisplayId = "D1", IsEnabled = true, Brightness = 20 }, ColorVariant.Primary);
        settings.SetProfile(new DisplayColorProfile
        {
            DisplayId = "D2", IsEnabled = false, Brightness = 40, Contrast = 60, Gamma = 1.4, DigitalVibrance = 70
        }, ColorVariant.Primary);
        settings.SetProfile(new DisplayColorProfile { DisplayId = "d1", IsEnabled = true, Brightness = 99 }, variant);

        settings.EnsureVariantInitialized(variant);
        settings.UpdateProfile("D2", p => p.Brightness = 10);

        var rows = settings.SnapshotProfiles(variant);
        Assert.Equal(99, rows["D1"].Brightness);
        Assert.Equal("D2", rows["D2"].DisplayId);
        Assert.False(rows["D2"].IsEnabled);
        Assert.Equal(40, rows["D2"].Brightness);
        Assert.Equal(60, rows["D2"].Contrast);
        Assert.Equal(1.4, rows["D2"].Gamma);
        Assert.Equal(70, rows["D2"].DigitalVibrance);
    }

    [Fact]
    public void EditingInactiveSecondary_DoesNotWriteDirectlyToHardware()
    {
        var display = new DisplayInfo { Id = "DISPLAY1", Name = "Monitor", DeviceName = "DISPLAY1" };
        var settings = new ColorSettings { IsEnabled = true, HasSecondary = true };
        settings.SetProfile(new DisplayColorProfile
        {
            DisplayId = display.Id, IsEnabled = true, Brightness = 30
        }, ColorVariant.Primary);
        settings.SetProfile(new DisplayColorProfile
        {
            DisplayId = display.Id, IsEnabled = true, Brightness = 70
        }, ColorVariant.Secondary);
        var color = new RecordingColorControlService();
        using var vm = new ColorSettingsViewModel(
            settings, new FakeDisplayService { Displays = [display] }, color, allowLiveUpdates: true);

        vm.EditingVariant = ColorVariant.Secondary;
        color.AppliedProfiles.Clear();
        vm.DisplayViewModels.Single().Brightness = 80;

        Assert.Equal(ColorVariant.Primary, settings.ActiveVariant);
        Assert.Empty(color.AppliedProfiles);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InactivePreset_EnableToggle_DoesNotApplyOrRevertHardware(bool enable)
    {
        var display = new DisplayInfo { Id = "DISPLAY1", Name = "Monitor", DeviceName = "DISPLAY1" };
        var settings = new ColorSettings { IsEnabled = true, HasSecondary = true };
        var profile = new DisplayColorProfile { DisplayId = display.Id, IsEnabled = !enable };
        settings.SetProfile(profile, ColorVariant.Secondary);
        var color = new RecordingColorControlService();
        using var vm = new DisplayColorSettingsViewModel(
            display, profile, settings, color, () => true, ColorVariant.Secondary, allowLiveUpdates: true);

        vm.IsEnabled = enable;

        Assert.Empty(color.AppliedProfiles);
    }

    [Fact]
    public void PendingHardwareApply_AfterVariantChange_RechecksActiveVariant()
    {
        var display = new DisplayInfo { Id = "DISPLAY1", Name = "Monitor", DeviceName = "DISPLAY1" };
        var settings = new ColorSettings { IsEnabled = true, HasSecondary = true };
        var profile = new DisplayColorProfile { DisplayId = display.Id, IsEnabled = true, Brightness = 30 };
        settings.SetProfile(profile);
        settings.EnsureVariantInitialized(ColorVariant.Secondary);
        var color = new RecordingColorControlService();
        using var vm = new DisplayColorSettingsViewModel(
            display, profile, settings, color, () => true, allowLiveUpdates: true);
        vm.Brightness = 40;
        color.AppliedProfiles.Clear();
        settings.SetActiveVariant(ColorVariant.Secondary);

        // Execute the existing timer target after its scheduling-time state has changed.
        typeof(DisplayColorSettingsViewModel).GetMethod("ApplyToHardwareNow",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);

        Assert.Empty(color.AppliedProfiles);
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

    [Fact]
    public void MasterToggles_MakeNoHardwareCall()
    {
        // codex P1: a MASTER switch flip (the Color Settings checkbox on the default profile) must be
        // UI-only on the editor side — the old NotifyMasterEnabledChanged wrote a NEUTRAL gamma/DVC to
        // every display, including ones the user never opted into. The hardware transition belongs to
        // ProfileActivationService's plan-diff (force-queued via the Color change kind), which restores
        // only previously-owned displays.
        var (vm, color, _) = BuildLiveVm();

        vm.IsEnabled = false; // uncheck "Color Settings" on the live (default) profile
        Assert.Empty(color.AppliedProfiles);

        vm.IsEnabled = true; // re-check
        Assert.Empty(color.AppliedProfiles);

        vm.Dispose();
    }

    [Fact]
    public void SidebarMasterToggle_OnDefaultProfile_MakesNoHardwareCall()
    {
        // codex P1: the merged profile's sidebar enable is a master for the WHOLE profile — a user
        // toggling it (e.g. just to disable the Windows Launcher) must not get hardware writes from
        // the editor's per-display VMs.
        var display = new DisplayInfo { Id = @"\\.\DISPLAY1", Name = "Mon", DeviceName = @"\\.\DISPLAY1" };
        var displays = new FakeDisplayService { Displays = [display] };
        var color = new RecordingColorControlService();
        using var viewModel = new ProfileViewModel(
            ProfileFactory.CreateWindowsProfile(),
            displays,
            color);

        color.AppliedProfiles.Clear(); // ignore construction-time activity
        viewModel.IsEnabled = false;

        Assert.Empty(color.AppliedProfiles);
    }
}
