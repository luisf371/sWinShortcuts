using System.Collections.Immutable;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Xunit;

namespace Tests;

public class ColorPlanTests
{
    [Fact]
    public void Equals_SameDisplayPlans_ReturnsTrue()
    {
        var displays = ImmutableArray.Create(new DisplayColorPlan("DISPLAY1", true, 55, 60, 1.1, 70));

        var first = new ColorPlan(displays);
        var second = new ColorPlan(displays);

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void BuildColorPlan_ReorderedDisplays_ProducesStablePlan()
    {
        var manager = CreateInitializedManager();
        var firstDisplays = new[]
        {
            CreateDisplay("DISPLAY2"),
            CreateDisplay("DISPLAY1")
        };
        var secondDisplays = new[]
        {
            CreateDisplay("DISPLAY1"),
            CreateDisplay("DISPLAY2")
        };

        var first = ProfileActivationService.BuildColorPlan(null, firstDisplays, manager);
        var second = ProfileActivationService.BuildColorPlan(null, secondDisplays, manager);

        Assert.Equal(first, second);
        Assert.Equal(["DISPLAY1", "DISPLAY2"], first.Displays.Select(display => display.DisplayId));
    }

    [Fact]
    public void Equals_ChangedBrightness_ReturnsFalse()
    {
        var first = new ColorPlan(ImmutableArray.Create(new DisplayColorPlan("DISPLAY1", true, 55, 60, 1.1, 70)));
        var second = new ColorPlan(ImmutableArray.Create(new DisplayColorPlan("DISPLAY1", true, 56, 60, 1.1, 70)));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Equals_ChangedDisplayCount_ReturnsFalse()
    {
        var first = new ColorPlan(ImmutableArray.Create(new DisplayColorPlan("DISPLAY1", false, 50, 50, 1.0, 50)));
        var second = new ColorPlan(ImmutableArray.Create(
            new DisplayColorPlan("DISPLAY1", false, 50, 50, 1.0, 50),
            new DisplayColorPlan("DISPLAY2", false, 50, 50, 1.0, 50)));

        Assert.NotEqual(first, second);
    }

    private static ProfileManager CreateInitializedManager()
    {
        var store = new Fakes.InMemoryProfileStore();
        var manager = new ProfileManager(store);
        manager.InitializeAsync().GetAwaiter().GetResult();
        return manager;
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
