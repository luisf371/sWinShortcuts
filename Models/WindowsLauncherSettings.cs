using System.Collections.Generic;
using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class WindowsLauncherSettings
{
    public Dictionary<Key, LauncherBinding> Launchers { get; } = new();
}

public sealed class LauncherBinding
{
    public string Path { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public bool RunAsAdmin { get; set; }
}
