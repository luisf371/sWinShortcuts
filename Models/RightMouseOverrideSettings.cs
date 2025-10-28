using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class RightMouseOverrideSettings
{
    public bool IsEnabled { get; set; }

    public List<RightMouseOverrideEntry> Overrides { get; } = new();

}
