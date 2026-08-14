using sWinShortcuts.Views;
using Xunit;

namespace Tests;

// SettingsWindow.ShouldApplyStartup decides whether Save runs the schtasks startup apply at all:
// only when the user actually changed one of the two startup options from the post-coercion
// baseline the dialog loaded with. Static-only helper, so no window construction or STA is needed.
public sealed class SettingsWindowStartupLogicTests
{
    [Fact]
    public void ShouldApplyStartup_UnchangedOptions_ReturnsFalse()
    {
        Assert.False(SettingsWindow.ShouldApplyStartup(
            currentStartWithWindows: true,
            currentStartAsAdmin: true,
            baselineStartWithWindows: true,
            baselineStartAsAdmin: true));
    }

    [Fact]
    public void ShouldApplyStartup_StartWithWindowsChanged_ReturnsTrue()
    {
        Assert.True(SettingsWindow.ShouldApplyStartup(
            currentStartWithWindows: false,
            currentStartAsAdmin: true,
            baselineStartWithWindows: true,
            baselineStartAsAdmin: true));
    }

    [Fact]
    public void ShouldApplyStartup_StartAsAdminChanged_ReturnsTrue()
    {
        Assert.True(SettingsWindow.ShouldApplyStartup(
            currentStartWithWindows: true,
            currentStartAsAdmin: false,
            baselineStartWithWindows: true,
            baselineStartAsAdmin: true));
    }

    [Fact]
    public void ShouldApplyStartup_NonAdminCoercedBaselineUnchanged_ReturnsFalse()
    {
        // Regression: a non-admin session hard-coerces StartAsAdmin off, so the dialog baseline is
        // (true, false) even while the OS still holds the elevated (true, true) task. An untouched
        // save must SKIP the apply — the previous unconditional Apply(true, false) always failed
        // Access-Denied on the leftover HIGHEST task and warned on every unrelated save.
        Assert.False(SettingsWindow.ShouldApplyStartup(
            currentStartWithWindows: true,
            currentStartAsAdmin: false,
            baselineStartWithWindows: true,
            baselineStartAsAdmin: false));
    }
}
