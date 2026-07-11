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
        EnableDisplayColor(manager.ColorProfile, "DISPLAY1", 60);
        EnableDisplayColor(activeProfile, "DISPLAY1", 75);

        var plan = ProfileActivationService.BuildColorPlan(activeProfile, [CreateDisplay("DISPLAY1")], manager);

        var display = Assert.Single(plan.Displays);
        Assert.True(display.IsEnabled);
        Assert.Equal(75, display.Brightness);
    }

    [Fact]
    public async Task BuildColorPlan_NoActiveProfile_UsesGlobalColor()
    {
        var manager = await CreateManagerAsync();
        EnableDisplayColor(manager.ColorProfile, "DISPLAY1", 60);

        var plan = ProfileActivationService.BuildColorPlan(null, [CreateDisplay("DISPLAY1")], manager);

        var display = Assert.Single(plan.Displays);
        Assert.True(display.IsEnabled);
        Assert.Equal(60, display.Brightness);
    }

    [Fact]
    public async Task BuildColorPlan_NoEnabledColorProfile_UsesDefaults()
    {
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
