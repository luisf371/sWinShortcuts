using sWinShortcuts.ViewModels;
using Xunit;

namespace Tests;

// MainViewModel.CoerceTabIndex is the pure core of the profile-editor tab coercion: WPF keeps
// rendering a collapsed-but-selected tab, so every profile-kind change must re-land the
// selection on a tab that is visible for the current profile kind. Static-only, so no
// dispatcher or profile construction is needed.
public sealed class MainViewModelTabTests
{
    // Windows profile: only the System tab is visible — everything else coerces to System.
    [Theory]
    [InlineData(MainViewModel.TabIndexKeys)]
    [InlineData(MainViewModel.TabIndexAdvanced)]
    [InlineData(MainViewModel.TabIndexDisplay)]
    [InlineData(MainViewModel.TabIndexSystem)]
    public void WindowsProfile_AnyRequestedIndex_LandsOnSystem(int requested)
    {
        Assert.Equal(
            MainViewModel.TabIndexSystem,
            MainViewModel.CoerceTabIndex(requested, isWindowsProfile: true, isColorProfile: false));
    }

    // Color profile: only the Display tab is visible (advanced mode is irrelevant — the tab
    // requires a custom profile regardless).
    [Theory]
    [InlineData(MainViewModel.TabIndexKeys)]
    [InlineData(MainViewModel.TabIndexAdvanced)]
    [InlineData(MainViewModel.TabIndexDisplay)]
    [InlineData(MainViewModel.TabIndexSystem)]
    public void ColorProfile_AnyRequestedIndex_LandsOnDisplay(int requested)
    {
        Assert.Equal(
            MainViewModel.TabIndexDisplay,
            MainViewModel.CoerceTabIndex(requested, isWindowsProfile: false, isColorProfile: true));
    }

    // A locked Advanced Mode grays the Advanced page out in place rather than hiding the tab,
    // so selection stays put — the regression this pins: mode toggles must not yank the user
    // off the (still-visible) Advanced tab.
    [Fact]
    public void CustomProfile_RequestedAdvanced_StaysAdvanced()
    {
        Assert.Equal(
            MainViewModel.TabIndexAdvanced,
            MainViewModel.CoerceTabIndex(MainViewModel.TabIndexAdvanced, isWindowsProfile: false, isColorProfile: false));
    }

    // Custom profile: any still-visible selection passes through unchanged (in-session tab
    // retention across profile switches).
    [Theory]
    [InlineData(MainViewModel.TabIndexKeys)]
    [InlineData(MainViewModel.TabIndexAdvanced)]
    [InlineData(MainViewModel.TabIndexDisplay)]
    [InlineData(MainViewModel.TabIndexSystem)]
    public void CustomProfile_VisibleSelection_PassesThroughUnchanged(int requested)
    {
        Assert.Equal(
            requested,
            MainViewModel.CoerceTabIndex(requested, isWindowsProfile: false, isColorProfile: false));
    }
}
