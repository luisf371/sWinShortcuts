using System;
using System.Collections.Generic;
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

    public IniProfileStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _rootDirectory = Path.Combine(appData, "sWinShortcuts");
        _profilesDirectory = Path.Combine(_rootDirectory, ProfileConstants.ProfilesDirectoryName);
        _windowsProfilePath = Path.Combine(_rootDirectory, ProfileConstants.WindowsProfileFileName);

        Directory.CreateDirectory(_rootDirectory);
        Directory.CreateDirectory(_profilesDirectory);
    }

    public Task<IReadOnlyList<Profile>> LoadProfilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var profiles = new List<Profile>
        {
            LoadWindowsProfile(cancellationToken)
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
            : DetermineProfilePath(profile);

        var document = profile.IsWindowsProfile
            ? SerializeWindowsProfile(profile)
            : SerializeProfile(profile);

        document.Save(path);
        profile.SourcePath = path;

        return Task.CompletedTask;
    }

    public Task DeleteProfileAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        if (profile.IsWindowsProfile)
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
        DeserializeRightMouse(document, profile.RightMouseOverrides);
        DeserializeCapsLock(document, profile.CapsLock);

        return profile;
    }

    private static void DeserializeAltMouse(IniDocument document, AltMouseSettings settings)
    {
        settings.IsEnabled = document.GetBoolean("AltMouse", "Enabled", settings.IsEnabled);
        settings.HoldThresholdMilliseconds = Math.Max(10, document.GetInt32("AltMouse", "HoldThreshold", settings.HoldThresholdMilliseconds));

        settings.LeftButton.TapKey = document.GetKey("AltMouse.Left", "Tap");
        settings.LeftButton.HoldKey = document.GetKey("AltMouse.Left", "Hold");

        settings.RightButton.TapKey = document.GetKey("AltMouse.Right", "Tap");
        settings.RightButton.HoldKey = document.GetKey("AltMouse.Right", "Hold");

        settings.MiddleButton.TapKey = document.GetKey("AltMouse.Middle", "Tap");
        settings.MiddleButton.HoldKey = document.GetKey("AltMouse.Middle", "Hold");
    }

    private static void DeserializeRightMouse(IniDocument document, RightMouseOverrideSettings settings)
    {
        settings.IsEnabled = document.GetBoolean("RightMouse", "Enabled", settings.IsEnabled);
        settings.SuppressOriginalKey = document.GetBoolean("RightMouse", "SuppressOriginal", settings.SuppressOriginalKey);

        settings.Overrides.Clear();
        var section = document.GetSection("RightMouseOverrides");
        foreach (var pair in section)
        {
            var source = KeySerializer.Deserialize(pair.Key);
            var target = KeySerializer.Deserialize(pair.Value);

            if (source is { } sourceKey && target is { } targetKey)
            {
                settings.Overrides[sourceKey] = targetKey;
            }
        }
    }

    private static void DeserializeCapsLock(IniDocument document, CapsLockSettings settings)
    {
        settings.Mode = document.GetEnum("CapsLock", "Mode", settings.Mode);
        settings.RemapTarget = document.GetKey("CapsLock", "RemapTarget");
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
        document.SetKey("AltMouse.Left", "Tap", altMouse.LeftButton.TapKey);
        document.SetKey("AltMouse.Left", "Hold", altMouse.LeftButton.HoldKey);
        document.SetKey("AltMouse.Right", "Tap", altMouse.RightButton.TapKey);
        document.SetKey("AltMouse.Right", "Hold", altMouse.RightButton.HoldKey);
        document.SetKey("AltMouse.Middle", "Tap", altMouse.MiddleButton.TapKey);
        document.SetKey("AltMouse.Middle", "Hold", altMouse.MiddleButton.HoldKey);

        var rightMouse = profile.RightMouseOverrides;
        document.SetBoolean("RightMouse", "Enabled", rightMouse.IsEnabled);
        document.SetBoolean("RightMouse", "SuppressOriginal", rightMouse.SuppressOriginalKey);
        document.RemoveSection("RightMouseOverrides");
        foreach (var mapping in rightMouse.Overrides)
        {
            document.SetString("RightMouseOverrides", KeySerializer.Serialize(mapping.Key), KeySerializer.Serialize(mapping.Value));
        }

        var capsLock = profile.CapsLock;
        document.SetEnum("CapsLock", "Mode", capsLock.Mode);
        document.SetKey("CapsLock", "RemapTarget", capsLock.RemapTarget);

        return document;
    }

    private static IniDocument SerializeWindowsProfile(Profile profile)
    {
        var document = new IniDocument();
        document.SetBoolean("Profile", "Enabled", profile.IsEnabled);

        foreach (var (key, binding) in profile.WindowsLauncher.Launchers)
        {
            var section = $"WindowsLauncher.{key}";
            document.SetString(section, "Path", binding.Path);
            document.SetString(section, "Arguments", binding.Arguments);
            document.SetBoolean(section, "RunAsAdmin", binding.RunAsAdmin);
        }

        return document;
    }

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
