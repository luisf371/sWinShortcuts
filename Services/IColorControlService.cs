using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public interface IColorControlService
{
    /// <summary>
    /// Apply the provided color profile to the given display. Returns true if any action was attempted.
    /// </summary>
    bool Apply(DisplayInfo display, DisplayColorProfile profile);
}
