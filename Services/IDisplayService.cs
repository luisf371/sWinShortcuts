using System.Collections.Generic;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public interface IDisplayService
{
    IReadOnlyList<DisplayInfo> GetDisplays();
}
