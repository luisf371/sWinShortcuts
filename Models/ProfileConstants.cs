using System;
using System.Collections.Generic;

namespace sWinShortcuts.Models;

public static class ProfileConstants
{
    public const string WindowsProfileName = "Window [Default]";
    public const string ColorProfileName = "Color Settings";
    public const string ProfilesDirectoryName = "Profiles";
    public const string WindowsProfileFileName = "Win.ini";
    public const string ColorProfileFileName = "Color.ini";

    // Names no custom profile may take: the current built-in display name plus both LEGACY built-in
    // names ("Windows" pre-rename, "Color Settings" pre-merge). The legacy entries stay reserved so a
    // custom profile can never hijack an old app-level LastProfile value (restore matches by name).
    public static readonly IReadOnlyList<string> ReservedProfileNames =
    [
        WindowsProfileName,
        "Windows",
        ColorProfileName
    ];

    public static bool IsReservedProfileName(string name) =>
        ReservedProfileNames.Contains(name, StringComparer.OrdinalIgnoreCase);
}
