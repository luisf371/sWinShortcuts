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

    public RapidFireSettings RapidFire { get; init; } = new();

    public AntiAfkSettings AntiAfk { get; init; } = new();

    public ColorSettings ColorSettings { get; init; } = new();


    public CapsLockSettings CapsLock { get; init; } = new();

    public CrosshairSettings Crosshair { get; init; } = new();

    public WindowsLauncherSettings WindowsLauncher { get; init; } = new();

    public string SourcePath { get; set; } = string.Empty;

    // F-008: set when this profile's on-disk source could not be read at load, so its in-memory state is
    // factory defaults. Persisting would overwrite the preserved (possibly transiently-locked) source
    // with those defaults, so IniProfileStore.SaveProfileAsync skips it while this flag is set.
    public bool IsPersistenceSuspended { get; set; }

    // One-time legacy-Color.ini migration marker, persisted ONLY in Win.ini ([Profile] ColorImported).
    // True (default) = nothing pending — a fresh/merged profile starts complete. LoadWindowsProfile sets
    // it false when the marker is absent (pre-merge Win.ini, or a failed import) and flips it back only
    // after the import actually completed, so an autosave that writes [Color] defaults mid-migration can
    // never suppress the import (codex P2: section presence alone is a false "done" signal).
    public bool LegacyColorImportCompleted { get; set; } = true;

    // F-007: built-in identity is an IMMUTABLE kind assigned at the load origin / factory, NOT derived
    // from the mutable display Name. A custom INI declaring Name="Window [Default]"/"Windows" therefore
    // stays Custom and can never route its save/delete onto Win.ini or bypass deletion guards.
    public ProfileKind Kind { get; init; } = ProfileKind.Custom;

    public bool IsWindowsProfile => Kind == ProfileKind.Windows;

    private static string NormalizeExecutable(string? value) => ExecutableName.Normalize(value);
}

public enum ProfileKind
{
    Custom,
    Windows
}
