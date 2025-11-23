using System.Collections.Generic;
using System.Windows.Input;
using sWinShortcuts.Models;

namespace sWinShortcuts.Factories;

public static class ProfileFactory
{
    private static readonly IReadOnlyList<Key> NumpadKeys =
    [
        Key.NumPad0,
        Key.NumPad1,
        Key.NumPad2,
        Key.NumPad3,
        Key.NumPad4,
        Key.NumPad5,
        Key.NumPad6,
        Key.NumPad7,
        Key.NumPad8,
        Key.NumPad9,
    ];

    public static Profile CreateWindowsProfile()
    {
        var profile = new Profile
        {
            Name = ProfileConstants.WindowsProfileName,
            Executable = string.Empty,
            IsEnabled = true
        };

        foreach (var key in NumpadKeys)
        {
            profile.WindowsLauncher.Launchers[key] = new LauncherBinding();
        }

        return profile;
    }

    public static Profile CreateCustomProfile(string name, string executable)
    {
        var profile = new Profile
        {
            Name = name,
            Executable = executable,
            IsEnabled = true
        };

        profile.CapsLock.IsEnabled = false;
        return profile;
    }

    public static Profile CreateColorProfile()
    {
        var profile = new Profile
        {
            Name = ProfileConstants.ColorProfileName,
            Executable = string.Empty,
            IsEnabled = true
        };

        // Disable unrelated features for the dedicated color profile
        profile.AltMouse.IsEnabled = false;
        profile.CombinedMappings.IsEnabled = false;
        profile.RightClickHoldBreath.IsEnabled = false;
        profile.CapsLock.IsEnabled = false;
        profile.WindowsLauncher.IsEnabled = false;

        return profile;
    }
}
