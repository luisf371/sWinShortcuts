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
    private readonly Services.ILoggerService? _logger;

    public IniProfileStore(Services.ILoggerService? logger = null)
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sWinShortcuts"),
            logger)
    {
    }

    // F-021: storage-root seam. Tests pass a unique temp root so they never mutate the real
    // %APPDATA%\sWinShortcuts, can run in parallel, and clean up deterministically. Production still
    // resolves AppData via the public ctor above.
    internal IniProfileStore(string rootDirectory, Services.ILoggerService? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _logger = logger;
        _rootDirectory = rootDirectory;
        _profilesDirectory = Path.Combine(_rootDirectory, ProfileConstants.ProfilesDirectoryName);
        _windowsProfilePath = Path.Combine(_rootDirectory, ProfileConstants.WindowsProfileFileName);
        _colorProfilePath = Path.Combine(_rootDirectory, ProfileConstants.ColorProfileFileName);

        // F-008: an inaccessible/redirected AppData must not abort construction (and thus the whole app).
        // Loads then fall back to in-memory defaults with persistence suspended; the app still starts with
        // input cleanup + tray. A later save re-attempts directory creation via the atomic INI writer.
        try
        {
            Directory.CreateDirectory(_rootDirectory);
            Directory.CreateDirectory(_profilesDirectory);
        }
        catch (Exception ex)
        {
            _logger?.Log($"[Profile] Failed to create storage directories under '{_rootDirectory}': {ex}");
        }
    }

    public Task<IReadOnlyList<Profile>> LoadProfilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // F-008: isolate EACH built-in load. Previously both sat in a list initializer, so an unreadable
        // Win.ini aborted the whole method (and the app) and Color.ini was never even attempted. Now a
        // failed built-in degrades to in-memory defaults with persistence suspended (so a later autosave
        // can't overwrite the preserved, possibly transiently-locked source), and the other still loads.
        var profiles = new List<Profile>
        {
            LoadBuiltInProfile("Windows", _windowsProfilePath, LoadWindowsProfile, CreateWindowsFallback, cancellationToken),
            LoadBuiltInProfile("Color", _colorProfilePath, LoadColorProfile, CreateColorFallback, cancellationToken)
        };

        try
        {
            foreach (var file in Directory.EnumerateFiles(_profilesDirectory, "*.ini", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var profile = LoadProfile(file);
                    profiles.Add(profile);
                }
                catch (Exception ex)
                {
                    // Don't let one malformed/partially-migrated file drop silently. Log the path + error so a
                    // parse/migration failure is diagnosable (a user reporting "my settings vanished" now has a
                    // trace). The file is left in place — never destroyed — so a transient lock is recoverable.
                    _logger?.Log($"[Profile] Failed to load '{file}': {ex}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // F-008: a damaged/inaccessible Profiles directory must not abort startup — the built-ins,
            // input cleanup, and tray still come up. Custom profiles are simply unavailable this run.
            _logger?.Log($"[Profile] Failed to enumerate profiles directory '{_profilesDirectory}': {ex}");
        }

        return Task.FromResult<IReadOnlyList<Profile>>(profiles);
    }

    // F-008: run one built-in loader in isolation. On any non-cancellation failure, fall back to factory
    // defaults, tag the source path, and suspend persistence so the unreadable source is preserved.
    private Profile LoadBuiltInProfile(
        string label,
        string path,
        Func<CancellationToken, Profile> loader,
        Func<Profile> makeFallback,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 4; // F-008 (codex #5): initial attempt + 3 transient retries.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return loader(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts && (ex is IOException || ex is UnauthorizedAccessException))
            {
                // F-008: a transient lock (another instance, an AV scan) is the common built-in-load
                // failure. Brief backoff then retry BEFORE degrading, so a fleeting lock doesn't force a
                // read-only degraded session. Runs on the startup thread before the hooks install.
                System.Threading.Thread.Sleep(100 * attempt);
            }
            catch (Exception ex)
            {
                _logger?.Log($"[Profile] Failed to load built-in '{label}' profile '{path}': {ex}. Using in-memory defaults; persistence suspended to preserve the on-disk file.");
                var fallback = makeFallback();
                fallback.SourcePath = path;
                fallback.IsPersistenceSuspended = true;
                return fallback;
            }
        }
    }

    private static Profile CreateWindowsFallback() => ProfileFactory.CreateWindowsProfile();

    private static Profile CreateColorFallback()
    {
        var profile = ProfileFactory.CreateColorProfile();
        profile.ColorSettings.IsEnabled = true;
        return profile;
    }

    public Task SaveProfileAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        if (profile.IsPersistenceSuspended)
        {
            // F-008: this built-in loaded as defaults because its source was unreadable. Refuse to
            // overwrite the preserved source with defaults, and signal explicitly (NOT silent success) so
            // the dirty-tracking layer keeps the edit and never reports a false save — a silent success
            // would clear the dirty flag and lose the change (codex CRITICAL #1).
            throw new PersistenceSuspendedException(profile.Name);
        }

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

        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.CompletedTask;
        }

        try
        {
            // F-015: do NOT gate on File.Exists — it returns false for access/IO errors too, which would
            // report a FALSE successful delete (manager/UI drop the profile, autosave cancels, and the
            // surviving INI resurrects next launch). File.Delete is already a no-op for an absent file;
            // a genuinely locked/denied file throws, which the manager relies on to keep the profile.
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
            // Parent directory is gone — there is nothing to delete.
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
            // Ensure default is enabled
            profile.ColorSettings.IsEnabled = true;
            SerializeColorProfile(profile).Save(path);
            return profile;
        }

        var document = IniDocument.Load(path);
        var profileInstance = ProfileFactory.CreateColorProfile();
        profileInstance.SourcePath = path;
        profileInstance.IsEnabled = document.GetBoolean("Profile", "Enabled", true);
        
        // Default to enabled for global color profile
        profileInstance.ColorSettings.IsEnabled = true;
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
        DeserializeAutoRun(document, profile.AutoRun);
        DeserializeAntiAfk(document, profile.AntiAfk);
        DeserializeCapsLock(document, profile.CapsLock);
        DeserializeColorSettings(document, profile.ColorSettings);

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

            // Legacy master also wrote [AltMouse.Button4]/[AltMouse.Button5] -> XButton1(4)/XButton2(5).
            var button4Tap = document.GetKey("AltMouse.Button4", "Tap");
            var button4Hold = document.GetKey("AltMouse.Button4", "Hold");
            if (button4Tap.HasValue || button4Hold.HasValue)
            {
                settings.Bindings[Models.MouseButton.XButton1] = new MouseButtonBinding
                {
                    TapKey = button4Tap,
                    HoldKey = button4Hold
                };
            }

            var button5Tap = document.GetKey("AltMouse.Button5", "Tap");
            var button5Hold = document.GetKey("AltMouse.Button5", "Hold");
            if (button5Tap.HasValue || button5Hold.HasValue)
            {
                settings.Bindings[Models.MouseButton.XButton2] = new MouseButtonBinding
                {
                    TapKey = button5Tap,
                    HoldKey = button5Hold
                };
            }
        }
    }

    private static void DeserializeCombinedMappings(IniDocument document, CombinedMappingsSettings settings)
    {
        settings.Mappings.Clear();

        var overrides = document.GetSection("KeyMappingsOverrides");

        // Prefer the current format. A branch profile always writes [KeyMappings] Enabled=…;
        // a legacy master INI never has it, so its absence (with no overrides) means "try legacy".
        if (document.GetSection("KeyMappings").Any() || overrides.Any())
        {
            settings.IsEnabled = document.GetBoolean("KeyMappings", "Enabled", settings.IsEnabled);

            foreach (var pair in overrides)
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

            return;
        }

        // Legacy master migration: [RightMouse] Enabled/SuppressOriginal + [RightMouseOverrides] Src=Tgt.
        // Each legacy override fired only while RMB was held with a single global SuppressOriginal flag,
        // which is exactly CombinedMappingEntry { RightClickOnly = true, SuppressOriginalKey = <global> }.
        var legacyOverrides = document.GetSection("RightMouseOverrides");
        if (document.GetSection("RightMouse").Any() || legacyOverrides.Any())
        {
            settings.IsEnabled = document.GetBoolean("RightMouse", "Enabled", settings.IsEnabled);
            var suppress = document.GetBoolean("RightMouse", "SuppressOriginal", true);

            foreach (var pair in legacyOverrides)
            {
                var source = KeySerializer.Deserialize(pair.Key);
                if (source is null) continue;

                var target = KeySerializer.Deserialize(pair.Value);
                if (target is null) continue;

                settings.Mappings.Add(new CombinedMappingEntry
                {
                    SourceKey = source.Value,
                    TargetKey = target.Value,
                    SuppressOriginalKey = suppress,
                    RightClickOnly = true
                });
            }
        }
    }

    private static void DeserializeRightClickHoldBreath(IniDocument document, RightClickHoldBreathSettings settings)
    {
        settings.IsEnabled = document.GetBoolean("RightClickHoldBreath", "Enabled", settings.IsEnabled);
        settings.HoldBreathKey = document.GetKey("RightClickHoldBreath", "Key") ?? settings.HoldBreathKey;
        settings.PanicTrigger = document.GetInputTrigger("RightClickHoldBreath", "Panic", settings.PanicTrigger);
        settings.SuppressEarlyCancelInput = document.GetBoolean("RightClickHoldBreath", "SuppressEarlyCancel", settings.SuppressEarlyCancelInput);
        settings.Mode = document.GetEnum("RightClickHoldBreath", "Mode", settings.Mode);
        // 0 is a designed value (fully synchronous, jitter-free activation) selectable in the UI —
        // clamping to 5 here silently re-enabled the jitter path on the next launch.
        settings.DelayMilliseconds = Math.Max(0, document.GetInt32("RightClickHoldBreath", "Delay", settings.DelayMilliseconds));
    }

    private static void DeserializeAutoRun(IniDocument document, AutoRunSettings settings)
    {
        settings.IsEnabled = document.GetBoolean("AutoRun", "Enabled", settings.IsEnabled);
        // GetEnum's Enum.IsDefined guard rejects a combined ModifierKeys flag (e.g. Control|Alt=6)
        // and falls back to the default, matching the "single side-agnostic modifier only" constraint.
        settings.TriggerModifier = document.GetEnum("AutoRun", "TriggerModifier", settings.TriggerModifier);
        // ...but IsDefined still accepts ModifierKeys.None (=0), which the UI never offers and which makes
        // the chord un-triggerable (IsTriggerModifierDown(None) is always false) + shows a blank ComboBox.
        // Coerce anything outside the supported single-value set back to the default (E2).
        if (settings.TriggerModifier is not (ModifierKeys.Control or ModifierKeys.Alt or ModifierKeys.Shift or ModifierKeys.Windows))
        {
            settings.TriggerModifier = ModifierKeys.Control;
        }
        settings.TriggerKey = document.GetKey("AutoRun", "TriggerKey") ?? settings.TriggerKey;
        settings.SprintEnabled = document.GetBoolean("AutoRun", "SprintEnabled", settings.SprintEnabled);
        settings.SprintKey = document.GetKey("AutoRun", "SprintKey") ?? settings.SprintKey;
        settings.SprintMode = document.GetEnum("AutoRun", "SprintMode", settings.SprintMode);
        // Missing/old INIs (no key) fall back to Foreground via the model default.
        settings.SendMode = document.GetEnum("AutoRun", "SendMode", settings.SendMode);
    }

    private static void DeserializeAntiAfk(IniDocument document, AntiAfkSettings settings)
    {
        settings.IsEnabled = document.GetBoolean("AntiAfk", "Enabled", settings.IsEnabled);
        // Clamp to the UI's 1..15 slider range on load (mirrors the Delay clamp above) so a
        // hand-edited or out-of-range INI can't drive the always-ticking timer with a bogus period.
        settings.IntervalMinutes = Math.Clamp(document.GetInt32("AntiAfk", "IntervalMinutes", settings.IntervalMinutes), 1, 15);
    }

    private static void DeserializeCapsLock(IniDocument document, CapsLockSettings settings)
    {
        settings.IsEnabled = document.GetBoolean("CapsLock", "Enabled", settings.IsEnabled);

        // Migration: legacy master stored CapsLockMode.MomentaryShift (renamed to Hold, same value=2).
        // Old INIs persist the NAME, so Enum.TryParse fails and would silently reset to Normal.
        var rawMode = document.GetString("CapsLock", "Mode", string.Empty);
        settings.Mode = string.Equals(rawMode, "MomentaryShift", StringComparison.OrdinalIgnoreCase)
            ? CapsLockMode.Hold
            : document.GetEnum("CapsLock", "Mode", settings.Mode);

        settings.RemapTarget = document.GetKey("CapsLock", "RemapTarget");
    }

    private static void DeserializeColorSettings(IniDocument document, ColorSettings settings)
    {
        settings.IsEnabled = document.GetBoolean("Color", "Enabled", settings.IsEnabled);
#pragma warning disable CS0618 // Type or member is obsolete
        settings.SelectedDisplayId = document.GetString("Color", "SelectedDisplay", settings.SelectedDisplayId);
#pragma warning restore CS0618
        settings.HasSecondary = document.GetBoolean("Color", "HasSecondary", settings.HasSecondary);
        settings.ToggleKey = document.GetKey("Color", "ToggleKey");
        settings.ClearProfiles();

        // Primary keeps the historical [ColorDisplays] section (back-compat); Secondary is the new preset.
        LoadColorDisplaySection(document, "ColorDisplays", settings, ColorVariant.Primary);
        LoadColorDisplaySection(document, "ColorDisplaysSecondary", settings, ColorVariant.Secondary);

        // Enforce the invariant HasSecondary => Secondary is POPULATED. A hand-edited / partial INI with
        // HasSecondary=true but an empty [ColorDisplaysSecondary] must not let the editor later create blank
        // (disabled) entries that a toggle would then apply as a neutral plan, wiping Primary. Seed from
        // Primary (no-op if the section had entries).
        if (settings.HasSecondary)
        {
            settings.EnsureSecondaryInitialized();
        }
    }

    private static void LoadColorDisplaySection(IniDocument document, string sectionName, ColorSettings settings, ColorVariant variant)
    {
        var section = document.GetSection(sectionName);
        foreach (var pair in section)
        {
            var parts = pair.Value.Split('|');

            // Detect format: new format has 5 fields (IsEnabled|Brightness|Contrast|Gamma|DigitalVibrance)
            // Old format has 4 fields (Brightness|Contrast|Gamma|DigitalVibrance)
            bool isEnabled;
            int brightnessIndex, contrastIndex, gammaIndex, vibranceIndex;

            if (parts.Length >= 5)
            {
                // New format: IsEnabled|Brightness|Contrast|Gamma|DigitalVibrance
                isEnabled = parts[0] == "1" || string.Equals(parts[0], "true", StringComparison.OrdinalIgnoreCase);
                brightnessIndex = 1;
                contrastIndex = 2;
                gammaIndex = 3;
                vibranceIndex = 4;
            }
            else
            {
                // Old format: Brightness|Contrast|Gamma|DigitalVibrance (defaults to enabled)
                isEnabled = true;
                brightnessIndex = 0;
                contrastIndex = 1;
                gammaIndex = 2;
                vibranceIndex = 3;
            }

            var brightness = ParsePercentage(parts, brightnessIndex, DisplayColorProfile.DefaultBrightness);
            var contrast = ParsePercentage(parts, contrastIndex, DisplayColorProfile.DefaultContrast);
            var gamma = parts.Length > gammaIndex && double.TryParse(parts[gammaIndex], NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedGamma)
                ? parsedGamma
                : DisplayColorProfile.DefaultGamma;
            var vibrance = ClampDigitalVibrance(ParsePercentage(parts, vibranceIndex, DisplayColorProfile.DefaultDigitalVibrance));

            var entry = new DisplayColorProfile
            {
                DisplayId = pair.Key,
                IsEnabled = isEnabled,
                Brightness = brightness,
                Contrast = contrast,
                Gamma = ClampGamma(gamma),
                DigitalVibrance = vibrance
            };

            settings.SetProfile(entry, variant);
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
        document.SetInputTrigger("RightClickHoldBreath", "Panic", rightClickHoldBreath.PanicTrigger);
        document.SetBoolean("RightClickHoldBreath", "SuppressEarlyCancel", rightClickHoldBreath.SuppressEarlyCancelInput);
        document.SetEnum("RightClickHoldBreath", "Mode", rightClickHoldBreath.Mode);
        document.SetInt32("RightClickHoldBreath", "Delay", rightClickHoldBreath.DelayMilliseconds);

        var autoRun = profile.AutoRun;
        document.SetBoolean("AutoRun", "Enabled", autoRun.IsEnabled);
        document.SetEnum("AutoRun", "TriggerModifier", autoRun.TriggerModifier);
        document.SetKey("AutoRun", "TriggerKey", autoRun.TriggerKey);
        document.SetBoolean("AutoRun", "SprintEnabled", autoRun.SprintEnabled);
        document.SetKey("AutoRun", "SprintKey", autoRun.SprintKey);
        document.SetEnum("AutoRun", "SprintMode", autoRun.SprintMode);
        document.SetEnum("AutoRun", "SendMode", autoRun.SendMode);

        var antiAfk = profile.AntiAfk;
        document.SetBoolean("AntiAfk", "Enabled", antiAfk.IsEnabled);
        document.SetInt32("AntiAfk", "IntervalMinutes", antiAfk.IntervalMinutes);

        var capsLock = profile.CapsLock;
        document.SetBoolean("CapsLock", "Enabled", capsLock.IsEnabled);
        document.SetEnum("CapsLock", "Mode", capsLock.Mode);
        document.SetKey("CapsLock", "RemapTarget", capsLock.RemapTarget);

        WriteColorSection(document, profile.ColorSettings);

        return document;
    }

    private static IniDocument SerializeColorProfile(Profile profile)
    {
        var document = new IniDocument();
        document.SetBoolean("Profile", "Enabled", profile.IsEnabled);

        WriteColorSection(document, profile.ColorSettings);

        return document;
    }

    // Single source of truth for the [Color]/[ColorDisplays] block so SerializeProfile and
    // SerializeColorProfile produce byte-identical output. Enumerates a snapshot (C3 §14.3) so a
    // concurrent UI/hot-plug mutation cannot throw "collection was modified" while autosave runs.
    private static void WriteColorSection(IniDocument document, ColorSettings color)
    {
        document.SetBoolean("Color", "Enabled", color.IsEnabled);
        document.SetBoolean("Color", "HasSecondary", color.HasSecondary);
        // Note: SelectedDisplayId is deprecated but kept for backward compatibility
#pragma warning disable CS0618 // Type or member is obsolete
        document.SetString("Color", "SelectedDisplay", color.SelectedDisplayId ?? string.Empty);
#pragma warning restore CS0618

        // Serialize each variant EXPLICITLY (not SnapshotProfiles(), which returns whichever is currently
        // active) so an autosave while Secondary is toggled-on still writes Primary->[ColorDisplays].
        WriteColorDisplaySection(document, "ColorDisplays", color.SnapshotProfiles(ColorVariant.Primary));
        WriteColorDisplaySection(document, "ColorDisplaysSecondary", color.SnapshotProfiles(ColorVariant.Secondary));
    }

    private static void WriteColorDisplaySection(IniDocument document, string sectionName, IReadOnlyDictionary<string, DisplayColorProfile> profiles)
    {
        document.RemoveSection(sectionName);
        foreach (var pair in profiles)
        {
            // New format: IsEnabled|Brightness|Contrast|Gamma|DigitalVibrance
            var isEnabledStr = pair.Value.IsEnabled ? "1" : "0";
            var value = $"{isEnabledStr}|{ClampPercent(pair.Value.Brightness)}|{ClampPercent(pair.Value.Contrast)}|{ClampGamma(pair.Value.Gamma).ToString("0.###", CultureInfo.InvariantCulture)}|{ClampDigitalVibrance(pair.Value.DigitalVibrance)}";
            document.SetString(sectionName, pair.Key, value);
        }
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
        // An established profile keeps writing to its own file, independent of its display name.
        if (!string.IsNullOrWhiteSpace(profile.SourcePath))
        {
            return profile.SourcePath;
        }

        // First save of a genuinely new profile: never resolve onto an existing file, otherwise a
        // reused name (e.g. after a rename) would clobber another profile's .ini.
        var sanitized = SanitizeFileName(profile.Name);
        var candidate = Path.Combine(_profilesDirectory, $"{sanitized}.ini");
        for (var n = 2; File.Exists(candidate); n++)
        {
            candidate = Path.Combine(_profilesDirectory, $"{sanitized} ({n}).ini");
        }

        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Profile" : sanitized;
    }
}
