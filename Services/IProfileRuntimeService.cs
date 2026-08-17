using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public interface IProfileRuntimeService
{
    void NotifyProfileChanged(Profile profile, ProfileChangeKind changeKind);

    /// <summary>
    /// Force-apply <paramref name="settings"/> (an edited game profile's color settings, at the
    /// given variant) instead of whatever the current foreground app resolves to — a temporary
    /// editing preview. Replaces any previous forced preview. Slider drags flow through
    /// NotifyProfileChanged(Color) and re-apply live while the preview is active. Call
    /// <see cref="ClearForcedColorPreview"/> to end it, which re-applies the foreground-appropriate
    /// plan (auto-restore).
    /// </summary>
    void SetForcedColorPreview(ColorSettings settings, ColorVariant variant);

    /// <summary>End the forced color preview and re-apply the foreground-appropriate plan.</summary>
    void ClearForcedColorPreview();
}
