using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Xunit;

namespace Tests;

public sealed class CrosshairDecisionTests
{
    [Fact]
    public void ShouldShow_NullProfile_IsFalse()
    {
        Assert.False(CrosshairDecision.ShouldShow(null));
        Assert.False(CrosshairDecision.ReportsRightButton(null));
    }

    [Fact]
    public void ShouldShow_DisabledProfile_IsFalse()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Crosshair.IsEnabled = true;
        profile.IsEnabled = false;

        Assert.False(CrosshairDecision.ShouldShow(profile));
        Assert.False(CrosshairDecision.ReportsRightButton(profile));
    }

    [Fact]
    public void ShouldShow_ProfileEnabled_CrosshairDisabled_IsFalse()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Crosshair.IsEnabled = false;

        Assert.True(profile.IsEnabled);
        Assert.False(CrosshairDecision.ShouldShow(profile));
        Assert.False(CrosshairDecision.ReportsRightButton(profile));
    }

    [Fact]
    public void ShouldShow_EnabledCrosshair_IsTrue()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Crosshair.IsEnabled = true;
        profile.Crosshair.HideWhileRightButtonHeld = false;

        Assert.True(CrosshairDecision.ShouldShow(profile));
        Assert.False(CrosshairDecision.ReportsRightButton(profile));
    }

    [Fact]
    public void ReportsRightButton_EnabledWithHideOn_IsTrue()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Crosshair.IsEnabled = true;
        profile.Crosshair.HideWhileRightButtonHeld = true;

        Assert.True(CrosshairDecision.ShouldShow(profile));
        Assert.True(CrosshairDecision.ReportsRightButton(profile));
    }

    [Fact]
    public void ReportsRightButton_HideOnButCrosshairDisabled_IsFalse()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Crosshair.IsEnabled = false;
        profile.Crosshair.HideWhileRightButtonHeld = true;

        Assert.False(CrosshairDecision.ReportsRightButton(profile));
    }
}
