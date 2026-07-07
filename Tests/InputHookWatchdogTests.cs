using sWinShortcuts.Services;
using Xunit;

namespace Tests;

// P8: unit tests for InputHookService.ShouldReinstallHook, the pure decision function extracted from
// the hook-loss watchdog. The Win32 plumbing around it (GetLastInputInfo, SetWindowsHookEx swap)
// stays untested, consistent with the rest of this repo's Win32-boundary testing approach.
public class ShouldReinstallHookTests
{
    private const double StaleThresholdMs = 30_000;
    private const uint FreshThresholdMs = 2_000;

    [Fact]
    public void FreshSystemInput_StaleHook_ReturnsTrue()
    {
        Assert.True(InputHookService.ShouldReinstallHook(30_001, 0, StaleThresholdMs, FreshThresholdMs));
    }

    [Fact]
    public void FreshSystemInput_ActiveHook_ReturnsFalse()
    {
        Assert.False(InputHookService.ShouldReinstallHook(1_000, 0, StaleThresholdMs, FreshThresholdMs));
    }

    [Fact]
    public void StaleSystemInput_StaleHook_ReturnsFalse()
    {
        // System itself hasn't seen fresh input either (e.g. lock screen, AFK) — "hook died" is
        // indistinguishable from "nobody is providing input"; must not reinstall.
        Assert.False(InputHookService.ShouldReinstallHook(60_000, 5_000, StaleThresholdMs, FreshThresholdMs));
    }

    [Fact]
    public void StaleSystemInput_ActiveHook_ReturnsFalse()
    {
        Assert.False(InputHookService.ShouldReinstallHook(500, 5_000, StaleThresholdMs, FreshThresholdMs));
    }

    [Fact]
    public void HookIdleExactlyAtThreshold_ReturnsFalse()
    {
        // Strict '>' per plan: exactly-at-threshold is not yet stale.
        Assert.False(InputHookService.ShouldReinstallHook(StaleThresholdMs, 0, StaleThresholdMs, FreshThresholdMs));
    }

    [Fact]
    public void HookIdleJustPastThreshold_ReturnsTrue()
    {
        Assert.True(InputHookService.ShouldReinstallHook(StaleThresholdMs + 0.001, 0, StaleThresholdMs, FreshThresholdMs));
    }

    [Fact]
    public void SystemInputAgeExactlyAtFreshThreshold_ReturnsFalse()
    {
        // Strict '<' per plan: exactly-at-threshold does not count as "fresh".
        Assert.False(InputHookService.ShouldReinstallHook(60_000, FreshThresholdMs, StaleThresholdMs, FreshThresholdMs));
    }

    [Fact]
    public void NeitherFreshNorStale_ReturnsFalse()
    {
        Assert.False(InputHookService.ShouldReinstallHook(1_000, 5_000, StaleThresholdMs, FreshThresholdMs));
    }
}
