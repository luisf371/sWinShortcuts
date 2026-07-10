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

    // F-008: set when this profile's on-disk source could not be read at load, so its in-memory state is
    // factory defaults. Persisting would overwrite the preserved (possibly transiently-locked) source
    // with those defaults, so IniProfileStore.SaveProfileAsync skips it while this flag is set.
    public bool IsPersistenceSuspended { get; set; }

    // F-007: built-in identity is an IMMUTABLE kind assigned at the load origin / factory, NOT derived
    // from the mutable display Name. A custom INI declaring Name="Windows"/"Color Settings" therefore
    // stays Custom and can never route its save/delete onto Win.ini/Color.ini or bypass deletion guards.
    public ProfileKind Kind { get; init; } = ProfileKind.Custom;

    public bool IsWindowsProfile => Kind == ProfileKind.Windows;

    public bool IsColorProfile => Kind == ProfileKind.Color;

    private static string NormalizeExecutable(string? value) => ExecutableName.Normalize(value);
}

public enum ProfileKind
{
    Custom,
    Windows,
    Color
}
