using sWinShortcuts.ViewModels;
using Xunit;

namespace Tests;

// MainViewModel.CoerceTabIndex is the pure core of the profile-editor tab coercion: WPF keeps
// rendering a collapsed-but-selected tab, so every profile-kind / advanced-mode change must
// re-land the selection on a tab that is visible for the current profile kind. Static-only, so
// no dispatcher or profile construction is needed.
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
            MainViewModel.CoerceTabIndex(requested, isWindowsProfile: true, isColorProfile: false, advancedModeEnabled: true));
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
            MainViewModel.CoerceTabIndex(requested, isWindowsProfile: false, isColorProfile: true, advancedModeEnabled: false));
    }

    [Fact]
    public void CustomProfile_AdvancedOff_RequestedAdvanced_FallsBackToKeys()
    {
        // Regression shape: the Advanced tab vanishes when Advanced Mode is toggled off in
        // Settings while it is the selected tab — selection must auto-move, not linger on a
        // collapsed tab that WPF would keep rendering.
        Assert.Equal(
            MainViewModel.TabIndexKeys,
            MainViewModel.CoerceTabIndex(MainViewModel.TabIndexAdvanced, isWindowsProfile: false, isColorProfile: false, advancedModeEnabled: false));
    }

    [Fact]
    public void CustomProfile_AdvancedOn_RequestedAdvanced_StaysAdvanced()
    {
        Assert.Equal(
            MainViewModel.TabIndexAdvanced,
            MainViewModel.CoerceTabIndex(MainViewModel.TabIndexAdvanced, isWindowsProfile: false, isColorProfile: false, advancedModeEnabled: true));
    }

    // Custom profile: any still-visible selection passes through unchanged (in-session tab
    // retention across profile switches), under both advanced-mode states.
    [Theory]
    [InlineData(MainViewModel.TabIndexKeys, false)]
    [InlineData(MainViewModel.TabIndexKeys, true)]
    [InlineData(MainViewModel.TabIndexDisplay, false)]
    [InlineData(MainViewModel.TabIndexDisplay, true)]
    [InlineData(MainViewModel.TabIndexSystem, false)]
    [InlineData(MainViewModel.TabIndexSystem, true)]
    public void CustomProfile_VisibleSelection_PassesThroughUnchanged(int requested, bool advancedModeEnabled)
    {
        Assert.Equal(
            requested,
            MainViewModel.CoerceTabIndex(requested, isWindowsProfile: false, isColorProfile: false, advancedModeEnabled));
    }
}
