using System.Collections.Generic;
using System.Windows.Input;

namespace sWinShortcuts.Models;

/// <summary>
/// Builds a detached, deep model copy for validation and persistence. The app captures this on the
/// WPF dispatcher at edit time, so background autosave always validates and serializes the exact same
/// state even if the live profile is edited again while I/O is in progress.
/// </summary>
internal static class ProfilePersistenceSnapshot
{
    public static Profile Create(Profile source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var color = CloneColor(source.ColorSettings);
        var snapshot = new Profile
        {
            Name = source.Name,
            Executable = source.Executable,
            IsEnabled = source.IsEnabled,
            AltMouse = CloneAltMouse(source.AltMouse),
            AltKeyboard = CloneAltKeyboard(source.AltKeyboard),
            CombinedMappings = CloneCombinedMappings(source.CombinedMappings),
            RightClickHoldBreath = CloneHoldBreath(source.RightClickHoldBreath),
            AutoRun = CloneAutoRun(source.AutoRun),
            RapidFire = CloneRapidFire(source.RapidFire),
            AntiAfk = CloneAntiAfk(source.AntiAfk),
            ColorSettings = color,
            CapsLock = CloneCapsLock(source.CapsLock),
            Crosshair = CloneCrosshair(source.Crosshair),
            WindowsLauncher = CloneWindowsLauncher(source.WindowsLauncher),
            SourcePath = source.SourcePath,
            IsPersistenceSuspended = source.IsPersistenceSuspended,
            Kind = source.Kind
        };

        return snapshot;
    }

    private static AltMouseSettings CloneAltMouse(AltMouseSettings source)
    {
        var bindings = new Dictionary<MouseButton, MouseButtonBinding>();
        foreach (var (button, binding) in source.Bindings)
        {
            bindings[button] = new MouseButtonBinding
            {
                TapKey = binding.TapKey,
                HoldKey = binding.HoldKey
            };
        }

        return new AltMouseSettings
        {
            IsEnabled = source.IsEnabled,
            HoldThresholdMilliseconds = source.HoldThresholdMilliseconds,
            Bindings = bindings
        };
    }

    private static AltKeyboardSettings CloneAltKeyboard(AltKeyboardSettings source)
    {
        var bindings = new Dictionary<Key, AltKeyboardBinding>();
        foreach (var (key, binding) in source.Bindings)
        {
            bindings[key] = new AltKeyboardBinding
            {
                TapKey = binding.TapKey,
                HoldKey = binding.HoldKey
            };
        }

        return new AltKeyboardSettings
        {
            IsEnabled = source.IsEnabled,
            HoldThresholdMilliseconds = source.HoldThresholdMilliseconds,
            Bindings = bindings
        };
    }

    private static CombinedMappingsSettings CloneCombinedMappings(
        CombinedMappingsSettings source)
    {
        var mappings = new List<CombinedMappingEntry>(source.Mappings.Count);
        foreach (var mapping in source.Mappings)
        {
            mappings.Add(new CombinedMappingEntry
            {
                SourceKey = mapping.SourceKey,
                TargetKey = mapping.TargetKey,
                SuppressOriginalKey = mapping.SuppressOriginalKey,
                RightClickOnly = mapping.RightClickOnly
            });
        }

        return new CombinedMappingsSettings
        {
            IsEnabled = source.IsEnabled,
            Mappings = mappings
        };
    }

    private static RightClickHoldBreathSettings CloneHoldBreath(
        RightClickHoldBreathSettings source)
    {
        return new RightClickHoldBreathSettings
        {
            IsEnabled = source.IsEnabled,
            HoldBreathKey = source.HoldBreathKey,
            Mode = source.Mode,
            DelayMilliseconds = source.DelayMilliseconds,
            PanicTrigger = source.PanicTrigger,
            SuppressEarlyCancelInput = source.SuppressEarlyCancelInput
        };
    }

    private static AutoRunSettings CloneAutoRun(AutoRunSettings source)
    {
        return new AutoRunSettings
        {
            IsEnabled = source.IsEnabled,
            TriggerModifier = source.TriggerModifier,
            TriggerKey = source.TriggerKey,
            SprintEnabled = source.SprintEnabled,
            SprintKey = source.SprintKey,
            SprintMode = source.SprintMode,
            SendMode = source.SendMode
        };
    }

    private static AntiAfkSettings CloneAntiAfk(AntiAfkSettings source)
    {
        return new AntiAfkSettings
        {
            IsEnabled = source.IsEnabled,
            IntervalMinutes = source.IntervalMinutes,
            SendMode = source.SendMode
        };
    }

    private static RapidFireSettings CloneRapidFire(RapidFireSettings source)
    {
        return new RapidFireSettings
        {
            IsEnabled = source.IsEnabled,
            IntervalMilliseconds = source.IntervalMilliseconds,
            JitterMilliseconds = source.JitterMilliseconds
        };
    }

    private static ColorSettings CloneColor(ColorSettings source)
    {
        var clone = new ColorSettings
        {
            IsEnabled = source.IsEnabled,
            HasSecondary = source.HasSecondary
        };

        foreach (var profile in source.SnapshotProfiles(ColorVariant.Primary).Values)
        {
            clone.SetProfile(CloneDisplayColorProfile(profile), ColorVariant.Primary);
        }

        foreach (var profile in source.SnapshotProfiles(ColorVariant.Secondary).Values)
        {
            clone.SetProfile(CloneDisplayColorProfile(profile), ColorVariant.Secondary);
        }

        clone.SetActiveVariant(source.ActiveVariant);
        return clone;
    }

    private static DisplayColorProfile CloneDisplayColorProfile(DisplayColorProfile source)
    {
        return new DisplayColorProfile
        {
            DisplayId = source.DisplayId,
            IsEnabled = source.IsEnabled,
            Brightness = source.Brightness,
            Contrast = source.Contrast,
            Gamma = source.Gamma,
            DigitalVibrance = source.DigitalVibrance
        };
    }

    private static CapsLockSettings CloneCapsLock(CapsLockSettings source)
    {
        return new CapsLockSettings
        {
            IsEnabled = source.IsEnabled,
            Mode = source.Mode,
            IsRemapEnabled = source.IsRemapEnabled,
            RemapTarget = source.RemapTarget
        };
    }

    private static CrosshairSettings CloneCrosshair(CrosshairSettings source)
    {
        return new CrosshairSettings
        {
            IsEnabled = source.IsEnabled,
            HideWhileRightButtonHeld = source.HideWhileRightButtonHeld,
            SizeAdjustment = source.SizeAdjustment,
            ImagePath = source.ImagePath
        };
    }

    private static WindowsLauncherSettings CloneWindowsLauncher(
        WindowsLauncherSettings source)
    {
        var clone = new WindowsLauncherSettings
        {
            IsEnabled = source.IsEnabled
        };

        foreach (var (key, binding) in source.Launchers)
        {
            clone.Launchers[key] = new LauncherBinding
            {
                Path = binding.Path,
                Arguments = binding.Arguments,
                RunAsAdmin = binding.RunAsAdmin
            };
        }

        return clone;
    }
}
