using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

// Pure activation gating for the crosshair overlay — no window/hook/dispatcher dependencies, so the
// decision matrix (profile enabled, feature enabled, RMB reporting) is directly unit-testable.
internal static class CrosshairDecision
{
    internal static bool ShouldShow(Profile? profile) =>
        profile is { IsEnabled: true } p && p.Crosshair.IsEnabled;

    internal static bool ReportsRightButton(Profile? profile) =>
        ShouldShow(profile) && profile!.Crosshair.HideWhileRightButtonHeld;
}
