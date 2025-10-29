using System;
using System.IO;

namespace sWinShortcuts.Models;

public sealed class Profile
{
    private string _executable = string.Empty;

    public required string Name { get; set; }

    public string Executable
    {
        get => _executable;
        set
        {
            _executable = value;
            NormalizedExecutable = NormalizeExecutable(value);
        }
    }

    public string NormalizedExecutable { get; private set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public AltMouseSettings AltMouse { get; init; } = new();

    public RightMouseOverrideSettings RightMouseOverrides { get; init; } = new();

    public RightClickHoldBreathSettings RightClickHoldBreath { get; init; } = new();

    public CapsLockSettings CapsLock { get; init; } = new();

    public WindowsLauncherSettings WindowsLauncher { get; init; } = new();

    public string SourcePath { get; set; } = string.Empty;

    public bool IsWindowsProfile =>
        string.Equals(Name, ProfileConstants.WindowsProfileName, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileNameWithoutExtension(value);
        return fileName?.Trim().ToLowerInvariant() ?? string.Empty;
    }
}
