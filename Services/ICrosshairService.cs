using System;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public interface ICrosshairService
{
    /// <summary>
    /// Applies the ACTIVE profile's crosshair overlay configuration: shows/positions/configures the
    /// overlay when the profile (and its Crosshair feature) is enabled, hides it otherwise.
    /// Called by ProfileActivationService on its activation worker for foreground changes and by
    /// NotifyProfileChanged for live edits; all window work is marshaled to the UI dispatcher.
    /// </summary>
    /// <param name="profile">The profile that just became active; null to hide (deactivation/stop).</param>
    /// <param name="foregroundHwnd">The game's foreground window handle; IntPtr.Zero = primary screen.</param>
    void ApplyProfile(Profile? profile, IntPtr foregroundHwnd);

    /// <summary>
    /// Feed from the input hook's right-button observation (hook thread). Hides the overlay while the
    /// button is held and restores it on release, when the applied profile enabled
    /// HideWhileRightButtonHeld. Never blocks the hook: dispatcher BeginInvoke only.
    /// </summary>
    void SetRightButtonHeld(bool isDown);
}
