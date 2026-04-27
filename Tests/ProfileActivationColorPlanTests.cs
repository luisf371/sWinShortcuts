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
