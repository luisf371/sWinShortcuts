using System;

namespace sWinShortcuts.Models;

[Flags]
public enum ProfileChangeKind
{
    None = 0,
    Master = 1 << 0,
    Identity = 1 << 1,
    AltMouse = 1 << 2,
    CombinedMappings = 1 << 3,
    HoldBreath = 1 << 4,
    AutoRun = 1 << 5,
    AntiAfk = 1 << 6,
    CapsLock = 1 << 7,
    WindowsLauncher = 1 << 8,
    Color = 1 << 9,
    Removed = 1 << 10,
    AllRuntime = Master | Identity | AltMouse | CombinedMappings | HoldBreath |
                 AutoRun | AntiAfk | CapsLock | WindowsLauncher | Color
}

public sealed class ProfileChangedEventArgs(ProfileChangeKind kind) : EventArgs
{
    public ProfileChangeKind Kind { get; } = kind;
}
