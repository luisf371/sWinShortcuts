using System;
using sWinShortcuts.Utilities;

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

    public CombinedMappingsSettings CombinedMappings { get; init; } = new();

    public RightClickHoldBreathSettings RightClickHoldBreath { get; init; } = new();

    public AutoRunSettings AutoRun { get; init; } = new();

    public AntiAfkSettings AntiAfk { get; init; } = new();

    public ColorSettings ColorSettings { get; init; } = new();


    public CapsLockSettings CapsLock { get; init; } = new();

    public WindowsLauncherSettings WindowsLauncher { get; init; } = new();

    public string SourcePath { get; set; } = string.Empty;

    public bool IsWindowsProfile =>
        string.Equals(Name, ProfileConstants.WindowsProfileName, StringComparison.OrdinalIgnoreCase);

    public bool IsColorProfile =>
        string.Equals(Name, ProfileConstants.ColorProfileName, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeExecutable(string? value) => ExecutableName.Normalize(value);
}
