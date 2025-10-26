using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class CapsLockSettings
{
    public bool IsEnabled { get; set; } = true;

    public CapsLockMode Mode { get; set; } = CapsLockMode.Normal;

    public Key? RemapTarget { get; set; }
}

public enum CapsLockMode
{
    Normal = 0,
    Disabled = 1,
    MomentaryShift = 2,
    Remap = 3
}
