using System.Collections.Generic;
using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class KeyRemapperSettings
{
    public bool IsEnabled { get; set; }

    public List<KeyRemapperEntry> Overrides { get; } = new();
}

