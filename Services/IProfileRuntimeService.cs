using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public interface IProfileRuntimeService
{
    void NotifyProfileChanged(Profile profile, ProfileChangeKind changeKind);

    /// <summary>
    /// Temporarily apply <paramref name="settings"/> at the given variant over the currently
    /// selected color source. Replaces any previous forced preview. Slider drags flow through
    /// NotifyProfileChanged(Color) and re-apply live while the preview is active. Call
    /// <see cref="ClearForcedColorPreview"/> to end it and re-apply the currently selected color source.
    /// </summary>
    void SetForcedColorPreview(ColorSettings settings, ColorVariant variant);

    /// <summary>
    /// End the forced color preview and re-apply the currently selected color source. This is the
    /// foreground profile or a game held by Keep color active, with Window [Default] as fallback.
    /// </summary>
    void ClearForcedColorPreview();
}
