using System;
using System.IO;

namespace sWinShortcuts.Utilities;

/// <summary>
/// Canonical match-key normalization for executable names.
/// Strips ONLY a trailing ".exe" (case-insensitive), never other dotted segments,
/// so dotted base names such as "paint.net" survive intact and still match "paint.net.exe".
/// </summary>
public static class ExecutableName
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(value.Trim());
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }

        return fileName.Trim().ToLowerInvariant();
    }
}
