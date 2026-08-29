using System;
using System.Collections.Generic;

namespace sWinShortcuts.Models;

public static class ProfileConstants
{
    public const string WindowsProfileName = "Window [Default]";
    public const string ProfilesDirectoryName = "Profiles";
    public const string WindowsProfileFileName = "Win.ini";

    // The current built-in display name cannot be used by a custom profile.
    public static readonly IReadOnlyList<string> ReservedProfileNames =
    [
        WindowsProfileName
    ];

    public static bool IsReservedProfileName(string name) =>
        ReservedProfileNames.Contains(name, StringComparer.OrdinalIgnoreCase);
}
