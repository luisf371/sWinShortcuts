using sWinShortcuts.ViewModels;
using Xunit;

namespace Tests;

// MainViewModel.CoerceTabIndex is the pure core of the profile-editor tab coercion: WPF keeps
// rendering a collapsed-but-selected tab, so every profile-kind change must re-land the
// selection on a tab that is visible for the current profile kind. Static-only, so no
// dispatcher or profile construction is needed.
public sealed class MainViewModelTabTests
{
    // Built-in default profile: Keys/Advanced are custom-only, so they coerce to System (its
    // home tab — Display for the global color settings is one tab away).
    [Theory]
    [InlineData(MainViewModel.TabIndexKeys)]
    [InlineData(MainViewModel.TabIndexAdvanced)]
    public void WindowsProfile_HiddenTabRequested_LandsOnSystem(int requested)
    {
        Assert.Equal(
            MainViewModel.TabIndexSystem,
            MainViewModel.CoerceTabIndex(requested, isWindowsProfile: true));
    }

    // Built-in default profile: Display and System are both visible, so a still-valid
    // selection passes through unchanged.
    [Theory]
    [InlineData(MainViewModel.TabIndexDisplay)]
    [InlineData(MainViewModel.TabIndexSystem)]
    public void WindowsProfile_VisibleSelection_PassesThroughUnchanged(int requested)
    {
        Assert.Equal(
            requested,
            MainViewModel.CoerceTabIndex(requested, isWindowsProfile: true));
    }

    // A locked Advanced Mode grays the Advanced page out in place rather than hiding the tab,
    // so selection stays put — the regression this pins: mode toggles must not yank the user
    // off the (still-visible) Advanced tab.
    [Fact]
    public void CustomProfile_RequestedAdvanced_StaysAdvanced()
    {
        Assert.Equal(
            MainViewModel.TabIndexAdvanced,
            MainViewModel.CoerceTabIndex(MainViewModel.TabIndexAdvanced, isWindowsProfile: false));
    }

    // Custom profile: every tab is visible, so any selection passes through unchanged
    // (in-session tab retention across profile switches).
    [Theory]
    [InlineData(MainViewModel.TabIndexKeys)]
    [InlineData(MainViewModel.TabIndexAdvanced)]
    [InlineData(MainViewModel.TabIndexDisplay)]
    [InlineData(MainViewModel.TabIndexSystem)]
    public void CustomProfile_VisibleSelection_PassesThroughUnchanged(int requested)
    {
        Assert.Equal(
            requested,
            MainViewModel.CoerceTabIndex(requested, isWindowsProfile: false));
    }
}
