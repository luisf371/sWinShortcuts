using System;
using System.Collections.Generic;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public interface IDisplayService
{
    IReadOnlyList<DisplayInfo> GetDisplays();

    /// <summary>Raised after the connected-display set changes (hot-plug, resolution/topology change).</summary>
    event EventHandler? DisplaysChanged;
}
