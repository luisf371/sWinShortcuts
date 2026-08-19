using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class CapsLockSettings
{
    public bool IsEnabled { get; set; } = false;

    public CapsLockMode Mode { get; set; } = CapsLockMode.Normal;

    public bool IsRemapEnabled { get; set; }

    public Key? RemapTarget { get; set; }
}

public enum CapsLockMode
{
    Normal = 0,
    Disabled = 1,
    DoubleNormal = 2
}
