using System.Collections.Generic;
using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class RightMouseOverrideSettings
{
    public bool IsEnabled { get; set; }

    public Dictionary<Key, Key> Overrides { get; } = new();

    public bool SuppressOriginalKey { get; set; } = true;
}
