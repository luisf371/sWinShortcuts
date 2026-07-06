using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

/// <summary>
/// Outcome of a single display color apply, so the orchestrator can dedup correctly:
/// only <see cref="Failed"/> (an intended write that failed transiently) is retry-worthy;
/// <see cref="Skipped"/> (deliberate no-op: NVAPI unavailable, fail-closed CreateDC, unmappable DVC)
/// counts as applied to avoid a per-event retry storm.
/// </summary>
public enum ColorApplyOutcome
{
    Applied,
    Skipped,
    Failed
}

public interface IColorControlService
{
    /// <summary>
    /// Apply the provided color profile to the given display.
    /// </summary>
    ColorApplyOutcome Apply(DisplayInfo display, DisplayColorProfile profile);
}
