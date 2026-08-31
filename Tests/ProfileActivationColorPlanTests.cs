using System.Collections.Immutable;
using System.Reflection;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public class ProfileActivationColorPlanTests
{
    [Fact]
    public async Task BuildColorPlan_ActiveProfileColorEnabled_OverridesGlobalColor()
    {
        var manager = await CreateManagerAsync();
        var activeProfile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        EnableDisplayColor(manager.WindowsProfile, "DISPLAY1", 60);
        EnableDisplayColor(activeProfile, "DISPLAY1", 75);

        var plan = ProfileActivationService.BuildColorPlan(activeProfile, [CreateDisplay("DISPLAY1")], manager);

        var display = Assert.Single(plan.Displays);
        Assert.True(display.IsEnabled);
        Assert.Equal(75, display.Brightness);
    }

    [Fact]
    public async Task BuildColorPlan_ActiveProfileMissingDisplay_FallsBackToGlobalColor()
    {
        var manager = await CreateManagerAsync();
        var activeProfile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        EnableDisplayColor(manager.WindowsProfile, "DISPLAY1", 60);
        EnableDisplayColor(manager.WindowsProfile, "DISPLAY2", 80);
        EnableDisplayColor(activeProfile, "DISPLAY1", 75);

        var plan = ProfileActivationService.BuildColorPlan(
            activeProfile,
            [CreateDisplay("DISPLAY1"), CreateDisplay("DISPLAY2")],
            manager);

        Assert.Collection(
            plan.Displays,
            primary => { Assert.Equal("DISPLAY1", primary.DisplayId); Assert.Equal(75, primary.Brightness); },
            secondary => { Assert.Equal("DISPLAY2", secondary.DisplayId); Assert.Equal(80, secondary.Brightness); });
    }

    [Fact]
    public async Task BuildColorPlan_NoActiveProfile_UsesGlobalColor()
    {
        var manager = await CreateManagerAsync();
        EnableDisplayColor(manager.WindowsProfile, "DISPLAY1", 60);

        var plan = ProfileActivationService.BuildColorPlan(null, [CreateDisplay("DISPLAY1")], manager);

        var display = Assert.Single(plan.Displays);
        Assert.True(display.IsEnabled);
        Assert.Equal(60, display.Brightness);
    }

    [Fact]
    public async Task BuildColorPlan_NoConfiguredGlobalColor_UsesDefaults()
    {
        // The merged default profile's color switch is on, but no per-display entries are
        // configured — every display falls through to the disabled/neutral defaults.
        var manager = await CreateManagerAsync();

        var plan = ProfileActivationService.BuildColorPlan(null, [CreateDisplay("DISPLAY1")], manager);

        var display = Assert.Single(plan.Displays);
        Assert.False(display.IsEnabled);
        Assert.Equal(DisplayColorProfile.DefaultBrightness, display.Brightness);
        Assert.Equal(DisplayColorProfile.DefaultContrast, display.Contrast);
        Assert.Equal(DisplayColorProfile.DefaultGamma, display.Gamma);
        Assert.Equal(DisplayColorProfile.DefaultDigitalVibrance, display.DigitalVibrance);
    }

    [Fact]
    public async Task BuildColorPlan_GlobalColorSwitchOff_OmitsFallbackEntirely()
    {
        // The default profile is ENABLED but its Color Settings checkbox is off: the fallback must
        // be omitted (null), so even configured per-display entries never apply.
        var manager = await CreateManagerAsync();
        EnableDisplayColor(manager.WindowsProfile, "DISPLAY1", 60);
        manager.WindowsProfile.ColorSettings.IsEnabled = false;

        var plan = ProfileActivationService.BuildColorPlan(null, [CreateDisplay("DISPLAY1")], manager);

        var display = Assert.Single(plan.Displays);
        Assert.False(display.IsEnabled);
        Assert.Equal(DisplayColorProfile.DefaultBrightness, display.Brightness);
    }

    [Fact]
    public async Task BuildColorPlan_TogglesBetweenPrimaryAndSecondary()
    {
        var manager = await CreateManagerAsync();
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.IsEnabled = true;
        profile.ColorSettings.IsEnabled = true;
        profile.ColorSettings.HasSecondary = true;
        profile.ColorSettings.SetProfile(new DisplayColorProfile { DisplayId = "DISPLAY1", IsEnabled = true, Brightness = 40, Contrast = 50, Gamma = 1.0, DigitalVibrance = 50 }, ColorVariant.Primary);
        profile.ColorSettings.SetProfile(new DisplayColorProfile { DisplayId = "DISPLAY1", IsEnabled = true, Brightness = 90, Contrast = 50, Gamma = 1.0, DigitalVibrance = 80 }, ColorVariant.Secondary);

        var displays = new[] { CreateDisplay("DISPLAY1") };

        // Starts on Primary ("as default")
        Assert.Equal(40, Assert.Single(ProfileActivationService.BuildColorPlan(profile, displays, manager).Displays).Brightness);

        profile.ColorSettings.ToggleVariant(); // -> Secondary
        var secondary = Assert.Single(ProfileActivationService.BuildColorPlan(profile, displays, manager).Displays);
        Assert.Equal(90, secondary.Brightness);
        Assert.Equal(80, secondary.DigitalVibrance);

        profile.ColorSettings.ToggleVariant(); // -> back to Primary
        Assert.Equal(40, Assert.Single(ProfileActivationService.BuildColorPlan(profile, displays, manager).Displays).Brightness);
    }

    [Fact]
    public async Task BuildColorPlan_ToggleNoOp_ForProfileWithoutSecondary()
    {
        var manager = await CreateManagerAsync();
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.IsEnabled = true;
        profile.ColorSettings.IsEnabled = true;
        profile.ColorSettings.HasSecondary = false; // an app the user never gave a secondary
        profile.ColorSettings.SetProfile(new DisplayColorProfile { DisplayId = "DISPLAY1", IsEnabled = true, Brightness = 40, Contrast = 50, Gamma = 1.0, DigitalVibrance = 50 }, ColorVariant.Primary);

        var displays = new[] { CreateDisplay("DISPLAY1") };

        profile.ColorSettings.ToggleVariant(); // no-op: HasSecondary is false
        Assert.Equal(ColorVariant.Primary, profile.ColorSettings.ActiveVariant);
        Assert.Equal(40, Assert.Single(ProfileActivationService.BuildColorPlan(profile, displays, manager).Displays).Brightness);
    }

    [Fact]
    public async Task ApplyColorPlan_DisplayAbsentFromHardware_LogsAndKeepsRetrying()
    {
        var manager = await CreateManagerAsync();
        var logger = new NullLoggerService { IsEnabled = true };
        var service = new ProfileActivationService(
            manager,
            new FakeForegroundWatcher(),
            new FakeInputHookService(),
            new FakeSystemTrayService(),
            new RecordingColorControlService(),
            new FakeDisplayService(),
            new FakeCrosshairService(),
            logger);

        // The display is in the plan but absent from hardware: the plan stays un-deduped (retry on
        // the next event) and the skip is now visible in the log.
        Assert.False(ApplyColorPlan(service, SingleDisplayPlan("DISPLAY1"), []));
        Assert.Contains(
            "[Color] Display 'DISPLAY1' is in the plan but absent from hardware; will retry on the next event.",
            logger.Messages);
    }

    [Fact]
    public async Task ApplyColorPlan_FailedOutcome_LogsAndKeepsRetrying()
    {
        var manager = await CreateManagerAsync();
        var logger = new NullLoggerService { IsEnabled = true };
        var color = new RecordingColorControlService { Outcome = ColorApplyOutcome.Failed };
        var service = new ProfileActivationService(
            manager,
            new FakeForegroundWatcher(),
            new FakeInputHookService(),
            new FakeSystemTrayService(),
            color,
            new FakeDisplayService { Displays = [CreateDisplay("DISPLAY1")] },
            new FakeCrosshairService(),
            logger);

        Assert.False(ApplyColorPlan(service, SingleDisplayPlan("DISPLAY1"), [CreateDisplay("DISPLAY1")]));
        Assert.Contains(
            "[Color] Apply failed for display 'DISPLAY1' (outcome=Failed); will retry on the next event.",
            logger.Messages);
    }

    private static ColorPlan SingleDisplayPlan(string displayId) => new(
        [new DisplayColorPlan(displayId, IsEnabled: true, Brightness: 60, Contrast: 50, Gamma: 1.0, DigitalVibrance: 50)]);

    private static bool ApplyColorPlan(
        ProfileActivationService service,
        ColorPlan plan,
        IReadOnlyList<DisplayInfo> displays) =>
        (bool)typeof(ProfileActivationService)
            .GetMethod("ApplyColorPlan", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [plan, ColorPlan.Empty, displays])!;

    private static async Task<ProfileManager> CreateManagerAsync()
    {
        var manager = new ProfileManager(new InMemoryProfileStore());
        await manager.InitializeAsync();
        return manager;
    }

    private static void EnableDisplayColor(Profile profile, string displayId, int brightness)
    {
        profile.ColorSettings.IsEnabled = true;
        profile.ColorSettings.SetProfile(new DisplayColorProfile
        {
            DisplayId = displayId,
            IsEnabled = true,
            Brightness = brightness,
            Contrast = 50,
            Gamma = 1.0,
            DigitalVibrance = 50
        });
    }

    private static DisplayInfo CreateDisplay(string id)
    {
        return new DisplayInfo
        {
            Id = id,
            Name = id,
            DeviceName = $@"\\.\{id}",
            IsPrimary = false
        };
    }
}
