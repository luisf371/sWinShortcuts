using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.Configuration;

public sealed class IniProfileStore : IProfileStore
{
    private readonly string _rootDirectory;
    private readonly string _profilesDirectory;
    private readonly string _windowsProfilePath;
    private readonly string _colorProfilePath;

    public IniProfileStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _rootDirectory = Path.Combine(appData, "sWinShortcuts");
        _profilesDirectory = Path.Combine(_rootDirectory, ProfileConstants.ProfilesDirectoryName);
        _windowsProfilePath = Path.Combine(_rootDirectory, ProfileConstants.WindowsProfileFileName);
        _colorProfilePath = Path.Combine(_rootDirectory, ProfileConstants.ColorProfileFileName);

        Directory.CreateDirectory(_rootDirectory);
        Directory.CreateDirectory(_profilesDirectory);
    }

    public Task<IReadOnlyList<Profile>> LoadProfilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var profiles = new List<Profile>
        {
            LoadWindowsProfile(cancellationToken),
            LoadColorProfile(cancellationToken)
        };

        foreach (var file in Directory.EnumerateFiles(_profilesDirectory, "*.ini", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var profile = LoadProfile(file);
                profiles.Add(profile);
            }
            catch
            {
                // Skip invalid profile files.
            }
        }

        return Task.FromResult<IReadOnlyList<Profile>>(profiles);
    }

    public Task SaveProfileAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        var path = profile.IsWindowsProfile
            ? _windowsProfilePath
            : profile.IsColorProfile
                ? _colorProfilePath
                : DetermineProfilePath(profile);

        var document = profile.IsWindowsProfile
            ? SerializeWindowsProfile(profile)
            : profile.IsColorProfile
                ? SerializeColorProfile(profile)
                : SerializeProfile(profile);

        document.Save(path);
        profile.SourcePath = path;

        return Task.CompletedTask;
    }

    public Task DeleteProfileAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        if (profile.IsWindowsProfile || profile.IsColorProfile)
        {
            return Task.CompletedTask;
        }

        var path = profile.SourcePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = DetermineProfilePath(profile);
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private Profile LoadWindowsProfile(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = _windowsProfilePath;

        if (!File.Exists(path))
        {
            var profile = ProfileFactory.CreateWindowsProfile();
            profile.SourcePath = path;
            SerializeWindowsProfile(profile).Save(path);
            return profile;
        }

        var document = IniDocument.Load(path);
        var profileInstance = ProfileFactory.CreateWindowsProfile();
        profileInstance.SourcePath = path;
        profileInstance.IsEnabled = document.GetBoolean("Profile", "Enabled", true);
        profileInstance.WindowsLauncher.IsEnabled = document.GetBoolean("WindowsLauncher", "Enabled", profileInstance.WindowsLauncher.IsEnabled);
        DeserializeCapsLock(document, profileInstance.CapsLock);

        foreach (var (key, binding) in profileInstance.WindowsLauncher.Launchers.ToArray())
        {
            var section = $"WindowsLauncher.{key}";
            binding.Path = document.GetString(section, "Path", binding.Path);
            binding.Arguments = document.GetString(section, "Arguments", binding.Arguments);
            binding.RunAsAdmin = document.GetBoolean(section, "RunAsAdmin", binding.RunAsAdmin);
            profileInstance.WindowsLauncher.Launchers[key] = binding;
        }

        return profileInstance;
    }

    private Profile LoadColorProfile(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = _colorProfilePath;

        if (!File.Exists(path))
        {
            var profile = ProfileFactory.CreateColorProfile();
            profile.SourcePath = path;
            SerializeColorProfile(profile).Save(path);
            return profile;
        }

        var document = IniDocument.Load(path);
        var profileInstance = ProfileFactory.CreateColorProfile();
        profileInstance.SourcePath = path;
        profileInstance.IsEnabled = document.GetBoolean("Profile", "Enabled", true);
        DeserializeColorSettings(document, profileInstance.ColorSettings);

        return profileInstance;
    }

    private Profile LoadProfile(string path)
    {
        var document = IniDocument.Load(path);

        var fileName = Path.GetFileNameWithoutExtension(path);
        var name = document.GetString("Profile", "Name", fileName);
        var executable = document.GetString("Profile", "Executable", string.Empty);

        var profile = ProfileFactory.CreateCustomProfile(name, executable);
        profile.SourcePath = path;
        profile.IsEnabled = document.GetBoolean("Profile", "Enabled", true);

        DeserializeAltMouse(document, profile.AltMouse);
        DeserializeCombinedMappings(document, profile.CombinedMappings);
        DeserializeRightClickHoldBreath(document, profile.RightClickHoldBreath);
        DeserializeCapsLock(document, profile.CapsLock);

        return profile;
    }

    private static void DeserializeAltMouse(IniDocument document, AltMouseSettings settings)
    {
        settings.IsEnabled = document.GetBoolean("AltMouse", "Enabled", settings.IsEnabled);
        settings.HoldThresholdMilliseconds = Math.Max(10, document.GetInt32("AltMouse", "HoldThreshold", settings.HoldThresholdMilliseconds));

        settings.Bindings.Clear();

        // Try to load new format first (AltMouseBindings section)
        var bindingsSection = document.GetSection("AltMouseBindings");
        if (bindingsSection.Any())
        {
            foreach (var pair in bindingsSection)
            {
                if (Enum.TryParse<Models.MouseButton>(pair.Key, out var button))
                {
                    var parts = pair.Value.Split('|');
                    var binding = new MouseButtonBinding();
                    
                    if (parts.Length > 0)
                    {
                        binding.TapKey = KeySerializer.Deserialize(parts[0]);
                    }
                    
                    if (parts.Length > 1)
                    {
                        binding.HoldKey = KeySerializer.Deserialize(parts[1]);
                    }
                    
                    settings.Bindings[button] = binding;
                }
            }
        }
        else
        {
            // Fallback to old format for migration (AltMouse.Left, AltMouse.Right, AltMouse.Middle)
            var leftTap = document.GetKey("AltMouse.Left", "Tap");
            var leftHold = document.GetKey("AltMouse.Left", "Hold");
            if (leftTap.HasValue || leftHold.HasValue)
            {
                settings.Bindings[Models.MouseButton.Left] = new MouseButtonBinding
                {
                    TapKey = leftTap,
                    HoldKey = leftHold
                };
            }

            var rightTap = document.GetKey("AltMouse.Right", "Tap");
            var rightHold = document.GetKey("AltMouse.Right", "Hold");
            if (rightTap.HasValue || rightHold.HasValue)
            {
                settings.Bindings[Models.MouseButton.Right] = new MouseButtonBinding
                {
                    TapKey = rightTap,
                    HoldKey = rightHold
                };
            }

            var middleTap = document.GetKey("AltMouse.Middle", "Tap");
            var middleHold = document.GetKey("AltMouse.Middle", "Hold");
            if (middleTap.HasValue || middleHold.HasValue)
            {
                settings.Bindings[Models.MouseButton.Middle] = new MouseButtonBinding
                {
                    TapKey = middleTap,
                    HoldKey = middleHold
                };
            }
        }
    }

    private static void DeserializeCombinedMappings(IniDocument document, CombinedMappingsSettings settings)
    {
        settings.IsEnabled = document.GetBoolean("KeyMappings", "Enabled", settings.IsEnabled);

        settings.Mappings.Clear();
        var section = document.GetSection("KeyMappingsOverrides");
        foreach (var pair in section)
        {
            var source = KeySerializer.Deserialize(pair.Key);
            if (source is null) continue;

            var parts = pair.Value.Split('|');
            var target = parts.Length > 0 ? KeySerializer.Deserialize(parts[0]) : null;
            if (target is null) continue;

            var suppress = parts.Length > 1 && bool.TryParse(parts[1], out var sVal) ? sVal : true;
            var rightClick = parts.Length > 2 && bool.TryParse(parts[2], out var rVal) ? rVal : false;

            settings.Mappings.Add(new CombinedMappingEntry
            {
                SourceKey = source.Value,
                TargetKey = target.Value,
                SuppressOriginalKey = suppress,
                RightClickOnly = rightClick
            });
        }
    }

    private static void DeserializeRightClickHoldBreath(IniDocument document, RightClickHoldBreathSettings settings)
    {
        settings.IsEnabled = document.GetBoolean("RightClickHoldBreath", "Enabled", settings.IsEnabled);
        settings.HoldBreathKey = document.GetKey("RightClickHoldBreath", "Key") ?? settings.HoldBreathKey;
        settings.Mode = document.GetEnum("RightClickHoldBreath", "Mode", settings.Mode);
        settings.DelayMilliseconds = Math.Max(5, document.GetInt32("RightClickHoldBreath", "Delay", settings.DelayMilliseconds));
    }

    private static void DeserializeCapsLock(IniDocument document, CapsLockSettings settings)
    {
        settings.IsEnabled = document.GetBoolean("CapsLock", "Enabled", settings.IsEnabled);
        settings.Mode = document.GetEnum("CapsLock", "Mode", settings.Mode);
        settings.RemapTarget = document.GetKey("CapsLock", "RemapTarget");
    }

    private static void DeserializeColorSettings(IniDocument document, ColorSettings settings)
    {
        settings.SelectedDisplayId = document.GetString("Color", "SelectedDisplay", settings.SelectedDisplayId);
        settings.ClearProfiles();

        var section = document.GetSection("ColorDisplays");
        foreach (var pair in section)
        {
            var parts = pair.Value.Split('|');
            var brightness = ParsePercentage(parts, 0, DisplayColorProfile.DefaultBrightness);
            var contrast = ParsePercentage(parts, 1, DisplayColorProfile.DefaultContrast);
            var gamma = parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedGamma)
                ? parsedGamma
                : DisplayColorProfile.DefaultGamma;
            var vibrance = ClampDigitalVibrance(ParsePercentage(parts, 3, DisplayColorProfile.DefaultDigitalVibrance));

            var entry = new DisplayColorProfile
            {
                DisplayId = pair.Key,
                Brightness = brightness,
                Contrast = contrast,
                Gamma = ClampGamma(gamma),
                DigitalVibrance = vibrance
            };

            settings.SetProfile(entry);
        }
    }

    private static IniDocument SerializeProfile(Profile profile)
    {
        var document = new IniDocument();
        document.SetString("Profile", "Name", profile.Name);
        document.SetString("Profile", "Executable", profile.Executable);
        document.SetBoolean("Profile", "Enabled", profile.IsEnabled);

        var altMouse = profile.AltMouse;
        document.SetBoolean("AltMouse", "Enabled", altMouse.IsEnabled);
        document.SetInt32("AltMouse", "HoldThreshold", altMouse.HoldThresholdMilliseconds);
        
        // Remove old format sections
        document.RemoveSection("AltMouse.Left");
        document.RemoveSection("AltMouse.Right");
        document.RemoveSection("AltMouse.Middle");
        document.RemoveSection("AltMouseBindings");
        
        // Write new format
        foreach (var binding in altMouse.Bindings)
        {
            var tapStr = binding.Value.TapKey.HasValue ? KeySerializer.Serialize(binding.Value.TapKey.Value) : "";
            var holdStr = binding.Value.HoldKey.HasValue ? KeySerializer.Serialize(binding.Value.HoldKey.Value) : "";
            var value = $"{tapStr}|{holdStr}";
            document.SetString("AltMouseBindings", binding.Key.ToString(), value);
        }

        var mappings = profile.CombinedMappings;
        document.SetBoolean("KeyMappings", "Enabled", mappings.IsEnabled);
        document.RemoveSection("KeyMappingsOverrides");
        foreach (var entry in mappings.Mappings)
        {
            var key = KeySerializer.Serialize(entry.SourceKey);
            var value = $"{KeySerializer.Serialize(entry.TargetKey)}|{entry.SuppressOriginalKey}|{entry.RightClickOnly}";
            document.SetString("KeyMappingsOverrides", key, value);
        }

        var rightClickHoldBreath = profile.RightClickHoldBreath;
        document.SetBoolean("RightClickHoldBreath", "Enabled", rightClickHoldBreath.IsEnabled);
        document.SetKey("RightClickHoldBreath", "Key", rightClickHoldBreath.HoldBreathKey);
        document.SetEnum("RightClickHoldBreath", "Mode", rightClickHoldBreath.Mode);
        document.SetInt32("RightClickHoldBreath", "Delay", rightClickHoldBreath.DelayMilliseconds);

        var capsLock = profile.CapsLock;
        document.SetBoolean("CapsLock", "Enabled", capsLock.IsEnabled);
        document.SetEnum("CapsLock", "Mode", capsLock.Mode);
        document.SetKey("CapsLock", "RemapTarget", capsLock.RemapTarget);

        return document;
    }

    private static IniDocument SerializeColorProfile(Profile profile)
    {
        var document = new IniDocument();
        document.SetBoolean("Profile", "Enabled", profile.IsEnabled);

        var color = profile.ColorSettings;
        document.SetString("Color", "SelectedDisplay", color.SelectedDisplayId ?? string.Empty);

        document.RemoveSection("ColorDisplays");
        foreach (var pair in color.DisplayProfiles)
        {
            var value = $"{ClampPercent(pair.Value.Brightness)}|{ClampPercent(pair.Value.Contrast)}|{ClampGamma(pair.Value.Gamma).ToString("0.###", CultureInfo.InvariantCulture)}|{ClampDigitalVibrance(pair.Value.DigitalVibrance)}";
            document.SetString("ColorDisplays", pair.Key, value);
        }

        return document;
    }

    private static IniDocument SerializeWindowsProfile(Profile profile)
    {
        var document = new IniDocument();
        document.SetBoolean("Profile", "Enabled", profile.IsEnabled);
        document.SetBoolean("WindowsLauncher", "Enabled", profile.WindowsLauncher.IsEnabled);

        var capsLock = profile.CapsLock;
        document.SetBoolean("CapsLock", "Enabled", capsLock.IsEnabled);
        document.SetEnum("CapsLock", "Mode", capsLock.Mode);
        document.SetKey("CapsLock", "RemapTarget", capsLock.RemapTarget);

        foreach (var (key, binding) in profile.WindowsLauncher.Launchers)
        {
            var section = $"WindowsLauncher.{key}";
            document.SetString(section, "Path", binding.Path);
            document.SetString(section, "Arguments", binding.Arguments);
            document.SetBoolean(section, "RunAsAdmin", binding.RunAsAdmin);
        }

        return document;
    }

    private static int ParsePercentage(string[] parts, int index, int defaultValue)
    {
        if (index < parts.Length &&
            int.TryParse(parts[index], NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            return ClampPercent(value);
        }

        return ClampPercent(defaultValue);
    }

    private static int ClampPercent(int value) => Math.Clamp(value, 0, 100);

    private static int ClampDigitalVibrance(int value) => Math.Clamp(value, DisplayColorProfile.DefaultDigitalVibrance, 100);

    private static double ClampGamma(double value) => Math.Clamp(value, 0.5, 3.0);

    private string DetermineProfilePath(Profile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.SourcePath))
        {
            return profile.SourcePath;
        }

        var sanitized = SanitizeFileName(profile.Name);
        return Path.Combine(_profilesDirectory, $"{sanitized}.ini");
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Profile" : sanitized;
    }
}
