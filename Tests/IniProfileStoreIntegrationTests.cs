using Xunit;
using System.IO;
using System.Windows.Input;
using sWinShortcuts.Configuration;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

/// <summary>
/// Integration tests for IniProfileStore.
/// These tests use the real filesystem (AppData).
/// </summary>
public class IniProfileStoreIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly IniProfileStore _store;
    private readonly List<Profile> _createdProfiles = [];

    public IniProfileStoreIntegrationTests()
    {
        // F-021: unique temp root per test instance (xUnit constructs one instance per test method),
        // so these tests are hermetic and parallel-safe and never touch the user's real AppData.
        _root = Path.Combine(Path.GetTempPath(), "sWinShortcutsTests", Guid.NewGuid().ToString("N"));
        _store = new IniProfileStore(_root, new Tests.Fakes.NullLoggerService());
    }

    public void Dispose()
    {
        // Single recursive delete of the temp root. Fail visibly if it can't complete rather than
        // swallowing — a leaked/locked temp dir is a real signal, not something to hide.
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void IniDocument_Save_ExistingFile_ReplacesContentAndCleansSiblingTemp()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "Atomic.ini");
        File.WriteAllText(path, "[Profile]\nName=Old\n");

        var document = new IniDocument();
        document.SetValue("Profile", "Name", "New");

        document.Save(path);

        Assert.Equal(
            $"[Profile]{Environment.NewLine}Name=New{Environment.NewLine}",
            File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "Atomic.ini.*.tmp"));
    }

    [Fact]
    public async Task ReservedNameCustomFile_StaysCustom_AndCannotClobberWindowsIni()
    {
        // F-007: a custom INI declaring a reserved Name must load as Custom (immutable Kind from load
        // origin), keep its own SourcePath, and never route its save onto Win.ini.
        var profilesDir = Path.Combine(_root, "Profiles");
        Directory.CreateDirectory(profilesDir);
        File.WriteAllText(
            Path.Combine(profilesDir, "Reserved.ini"),
            "[Profile]\nName=Windows\nExecutable=reserved.exe\nEnabled=true\n");

        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);

        var winIniPath = Path.Combine(_root, "Win.ini");
        var winIniBefore = File.ReadAllText(winIniPath);

        var custom = profiles.Single(p =>
            string.Equals(p.NormalizedExecutable, "reserved", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProfileKind.Custom, custom.Kind);
        Assert.False(custom.IsWindowsProfile);
        Assert.EndsWith("Reserved.ini", custom.SourcePath);

        // Saving the reserved-named custom profile must write ONLY its own file, never Win.ini.
        await _store.SaveProfileAsync(custom, CancellationToken.None);

        Assert.Equal(winIniBefore, File.ReadAllText(winIniPath));
        Assert.EndsWith("Reserved.ini", custom.SourcePath);
    }

    [Fact]
    public async Task BuiltInLoadFailure_DegradesToDefaults_AndSuspendsPersistence()
    {
        // F-008: an unreadable built-in must NOT abort startup. Occupy Win.ini's path with a directory so
        // its load fails, then assert: the Windows profile still loads (defaults, persistence suspended)
        // and saving the suspended profile is refused rather than overwriting the preserved path with
        // defaults.
        Directory.CreateDirectory(Path.Combine(_root, "Win.ini"));

        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);

        var windows = profiles.Single(p => p.IsWindowsProfile);
        Assert.True(windows.IsPersistenceSuspended);

        await Assert.ThrowsAsync<PersistenceSuspendedException>(
            () => _store.SaveProfileAsync(windows, CancellationToken.None));
    }

    [Fact]
    public async Task ReservedColorName_CustomFile_StaysCustom_AndNeverCreatesColorIni()
    {
        // F-007: the retired built-in's name ("Color Settings") stays reserved; a custom file carrying
        // it loads as Custom. There is no Color built-in anymore, so nothing may create Color.ini.
        var profilesDir = Path.Combine(_root, "Profiles");
        Directory.CreateDirectory(profilesDir);
        File.WriteAllText(
            Path.Combine(profilesDir, "Reserved.ini"),
            "[Profile]\nName=Color Settings\nExecutable=reservedcolor.exe\nEnabled=true\n");

        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);

        var custom = profiles.Single(p =>
            string.Equals(p.NormalizedExecutable, "reservedcolor", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ProfileKind.Custom, custom.Kind);
        Assert.False(custom.IsWindowsProfile);

        await _store.SaveProfileAsync(custom, CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_root, "Color.ini")));
    }

    [Fact]
    public async Task LoadProfilesAsync_ReturnsSingleWindowsBuiltIn()
    {
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);

        Assert.Single(profiles, p => p.IsWindowsProfile);
        Assert.Equal(1, profiles.Count(p => p.IsWindowsProfile));
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsBasicProfile()
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "roundtrip.exe");
        profile.IsEnabled = false;
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.Equal(profile.Name, loaded.Name);
        Assert.Equal("roundtrip", loaded.NormalizedExecutable);
        Assert.False(loaded.IsEnabled);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsAltMouseSettings()
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "altmouse.exe");
        profile.AltMouse.IsEnabled = true;
        profile.AltMouse.HoldThresholdMilliseconds = 75;
        profile.AltMouse.Bindings[MouseButton.Left] = new MouseButtonBinding
        {
            TapKey = Key.F,
            HoldKey = Key.G
        };
        profile.AltMouse.Bindings[MouseButton.Right] = new MouseButtonBinding
        {
            TapKey = Key.H
        };
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.True(loaded.AltMouse.IsEnabled);
        Assert.Equal(75, loaded.AltMouse.HoldThresholdMilliseconds);
        
        Assert.True(loaded.AltMouse.Bindings.ContainsKey(MouseButton.Left));
        Assert.Equal(Key.F, loaded.AltMouse.Bindings[MouseButton.Left].TapKey);
        Assert.Equal(Key.G, loaded.AltMouse.Bindings[MouseButton.Left].HoldKey);
        
        Assert.True(loaded.AltMouse.Bindings.ContainsKey(MouseButton.Right));
        Assert.Equal(Key.H, loaded.AltMouse.Bindings[MouseButton.Right].TapKey);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsAltKeyboardSettings()
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "altkeyboard.exe");
        profile.AltKeyboard.IsEnabled = true;
        profile.AltKeyboard.HoldThresholdMilliseconds = 75;
        profile.AltKeyboard.Bindings[Key.Q] = new AltKeyboardBinding
        {
            TapKey = Key.F,
            HoldKey = Key.G
        };
        profile.AltKeyboard.Bindings[Key.E] = new AltKeyboardBinding
        {
            HoldKey = Key.H // unset TapKey round-trips through the empty-string slot
        };
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.True(loaded.AltKeyboard.IsEnabled);
        Assert.Equal(75, loaded.AltKeyboard.HoldThresholdMilliseconds);

        Assert.True(loaded.AltKeyboard.Bindings.ContainsKey(Key.Q));
        Assert.Equal(Key.F, loaded.AltKeyboard.Bindings[Key.Q].TapKey);
        Assert.Equal(Key.G, loaded.AltKeyboard.Bindings[Key.Q].HoldKey);

        Assert.True(loaded.AltKeyboard.Bindings.ContainsKey(Key.E));
        Assert.Null(loaded.AltKeyboard.Bindings[Key.E].TapKey);
        Assert.Equal(Key.H, loaded.AltKeyboard.Bindings[Key.E].HoldKey);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsColorVariants_WithoutLegacyToggleKey()
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "color.exe");
        profile.ColorSettings.IsEnabled = true;
        profile.ColorSettings.HasSecondary = true;
        profile.ColorSettings.ToggleKey = Key.F8;
        profile.ColorSettings.SetProfile(new DisplayColorProfile { DisplayId = "DISPLAY1", IsEnabled = true, Brightness = 45, Contrast = 55, Gamma = 1.2, DigitalVibrance = 60 }, ColorVariant.Primary);
        profile.ColorSettings.SetProfile(new DisplayColorProfile { DisplayId = "DISPLAY1", IsEnabled = true, Brightness = 95, Contrast = 60, Gamma = 1.0, DigitalVibrance = 90 }, ColorVariant.Secondary);
        // Toggle to Secondary BEFORE saving — the serializer must still write each variant to its own section
        // (Primary must not be overwritten by the currently-active variant).
        profile.ColorSettings.ToggleVariant();
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.True(loaded.ColorSettings.HasSecondary);
        Assert.Null(loaded.ColorSettings.ToggleKey); // the global key now lives in [App] settings
        Assert.Equal(ColorVariant.Primary, loaded.ColorSettings.ActiveVariant); // runtime state resets on load

        var primary = loaded.ColorSettings.SnapshotProfiles(ColorVariant.Primary)["DISPLAY1"];
        Assert.Equal(45, primary.Brightness);
        Assert.Equal(60, primary.DigitalVibrance);

        var secondary = loaded.ColorSettings.SnapshotProfiles(ColorVariant.Secondary)["DISPLAY1"];
        Assert.Equal(95, secondary.Brightness);
        Assert.Equal(90, secondary.DigitalVibrance);
    }

    [Fact]
    public async Task Load_SeedsSecondaryFromPrimary_WhenHasSecondaryButSectionEmpty()
    {
        // Simulates a partial/hand-edited INI: HasSecondary=true but no secondary entries were ever written.
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "seed.exe");
        profile.ColorSettings.IsEnabled = true;
        profile.ColorSettings.HasSecondary = true; // enabled...
        profile.ColorSettings.SetProfile(new DisplayColorProfile { DisplayId = "DISPLAY1", IsEnabled = true, Brightness = 65, DigitalVibrance = 75 }, ColorVariant.Primary);
        // ...but Secondary intentionally left EMPTY.
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var loaded = (await _store.LoadProfilesAsync(CancellationToken.None)).FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.True(loaded.ColorSettings.HasSecondary);
        // Secondary was seeded from Primary on load -> a toggle applies the calibrated look, NOT a blank plan.
        var seeded = loaded.ColorSettings.SnapshotProfiles(ColorVariant.Secondary)["DISPLAY1"];
        Assert.Equal(65, seeded.Brightness);
        Assert.Equal(75, seeded.DigitalVibrance);
        Assert.Equal(ColorVariant.Secondary, loaded.ColorSettings.ToggleVariant()); // now switches (populated)
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsCombinedMappings()
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "combined.exe");
        profile.CombinedMappings.IsEnabled = true;
        profile.CombinedMappings.Mappings.Add(new CombinedMappingEntry
        {
            SourceKey = Key.A,
            TargetKey = Key.B,
            SuppressOriginalKey = true,
            RightClickOnly = false
        });
        profile.CombinedMappings.Mappings.Add(new CombinedMappingEntry
        {
            SourceKey = Key.C,
            TargetKey = Key.D,
            SuppressOriginalKey = false,
            RightClickOnly = true
        });
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.True(loaded.CombinedMappings.IsEnabled);
        Assert.Equal(2, loaded.CombinedMappings.Mappings.Count);

        var entry1 = loaded.CombinedMappings.Mappings[0];
        Assert.Equal(Key.A, entry1.SourceKey);
        Assert.Equal(Key.B, entry1.TargetKey);
        Assert.True(entry1.SuppressOriginalKey);
        Assert.False(entry1.RightClickOnly);

        var entry2 = loaded.CombinedMappings.Mappings[1];
        Assert.Equal(Key.C, entry2.SourceKey);
        Assert.Equal(Key.D, entry2.TargetKey);
        Assert.False(entry2.SuppressOriginalKey);
        Assert.True(entry2.RightClickOnly);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsCapsLockSettings()
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "capslock.exe");
        profile.CapsLock.IsEnabled = true;
        profile.CapsLock.Mode = CapsLockMode.DoubleNormal;
        profile.CapsLock.IsRemapEnabled = true;
        profile.CapsLock.RemapTarget = Key.Escape;
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.True(loaded.CapsLock.IsEnabled);
        Assert.Equal(CapsLockMode.DoubleNormal, loaded.CapsLock.Mode);
        Assert.True(loaded.CapsLock.IsRemapEnabled);
        Assert.Equal(Key.Escape, loaded.CapsLock.RemapTarget);
    }

    [Theory]
    [InlineData("Hold")]
    [InlineData("MomentaryShift")]
    [InlineData("2")]
    public async Task LoadProfiles_LegacyHoldNames_MigrateToDoubleNormal(string legacyMode)
    {
        var profilesDirectory = Path.Combine(_root, "Profiles");
        Directory.CreateDirectory(profilesDirectory);
        File.WriteAllText(
            Path.Combine(profilesDirectory, "LegacyHold.ini"),
            $"[Profile]\nName=Legacy Hold\nExecutable=legacyhold.exe\nEnabled=true\n\n" +
            $"[CapsLock]\nEnabled=true\nMode={legacyMode}\n");

        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = Assert.Single(profiles, p => p.NormalizedExecutable == "legacyhold");

        Assert.Equal(CapsLockMode.DoubleNormal, loaded.CapsLock.Mode);
        Assert.False(loaded.CapsLock.IsRemapEnabled);
    }

    [Theory]
    [InlineData("Remap")]
    [InlineData("3")]
    public async Task LoadProfiles_LegacyRemapMode_MigratesToNormalWithRemapEnabled(string legacyMode)
    {
        var profilesDirectory = Path.Combine(_root, "Profiles");
        Directory.CreateDirectory(profilesDirectory);
        File.WriteAllText(
            Path.Combine(profilesDirectory, "LegacyRemap.ini"),
            $"[Profile]\nName=Legacy Remap\nExecutable=legacyremap.exe\nEnabled=true\n\n" +
            $"[CapsLock]\nEnabled=true\nMode={legacyMode}\nRemapTarget=Escape\n");

        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = Assert.Single(profiles, p => p.NormalizedExecutable == "legacyremap");

        Assert.Equal(CapsLockMode.Normal, loaded.CapsLock.Mode);
        Assert.True(loaded.CapsLock.IsRemapEnabled);
        Assert.Equal(Key.Escape, loaded.CapsLock.RemapTarget);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsRightClickHoldBreath()
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "holdbreath.exe");
        profile.RightClickHoldBreath.IsEnabled = true;
        profile.RightClickHoldBreath.Mode = HoldBreathMode.Toggle;
        profile.RightClickHoldBreath.HoldBreathKey = Key.LeftShift;
        profile.RightClickHoldBreath.DelayMilliseconds = 150;
        profile.RightClickHoldBreath.PanicTrigger = InputTrigger.FromMouseButton(MouseButton.XButton1);
        profile.RightClickHoldBreath.SuppressEarlyCancelInput = false;
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.True(loaded.RightClickHoldBreath.IsEnabled);
        Assert.Equal(HoldBreathMode.Toggle, loaded.RightClickHoldBreath.Mode);
        Assert.Equal(Key.LeftShift, loaded.RightClickHoldBreath.HoldBreathKey);
        Assert.Equal(150, loaded.RightClickHoldBreath.DelayMilliseconds);
        Assert.Equal(InputTrigger.FromMouseButton(MouseButton.XButton1), loaded.RightClickHoldBreath.PanicTrigger);
        Assert.False(loaded.RightClickHoldBreath.SuppressEarlyCancelInput);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsAutoRun()
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "autorun.exe");
        profile.AutoRun.IsEnabled = true;
        profile.AutoRun.TriggerModifier = ModifierKeys.Alt;
        profile.AutoRun.TriggerKey = Key.T;
        profile.AutoRun.SprintEnabled = true;
        profile.AutoRun.SprintKey = Key.LeftCtrl;
        profile.AutoRun.SprintMode = SprintActivation.Press;
        profile.AutoRun.SendMode = AutoRunSendMode.Background;
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.True(loaded.AutoRun.IsEnabled);
        Assert.Equal(ModifierKeys.Alt, loaded.AutoRun.TriggerModifier);
        Assert.Equal(Key.T, loaded.AutoRun.TriggerKey);
        Assert.True(loaded.AutoRun.SprintEnabled);
        Assert.Equal(Key.LeftCtrl, loaded.AutoRun.SprintKey);
        Assert.Equal(SprintActivation.Press, loaded.AutoRun.SprintMode);
        Assert.Equal(AutoRunSendMode.Background, loaded.AutoRun.SendMode);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsSingleKeyAutoRunTrigger()
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "arnone.exe");
        profile.AutoRun.IsEnabled = true;
        profile.AutoRun.TriggerModifier = ModifierKeys.None;
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.Equal(ModifierKeys.None, loaded.AutoRun.TriggerModifier);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsAntiAfk()
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "antiafk.exe");
        profile.AntiAfk.IsEnabled = true;
        profile.AntiAfk.IntervalMinutes = 10;
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.True(loaded.AntiAfk.IsEnabled);
        Assert.Equal(10, loaded.AntiAfk.IntervalMinutes);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsRapidFire()
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "rapidfire.exe");
        profile.RapidFire.IsEnabled = true;
        profile.RapidFire.IntervalMilliseconds = 75;
        profile.RapidFire.JitterMilliseconds = 20;

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.Single(p => p.Name == profile.Name);

        Assert.True(loaded.RapidFire.IsEnabled);
        Assert.Equal(75, loaded.RapidFire.IntervalMilliseconds);
        Assert.Equal(20, loaded.RapidFire.JitterMilliseconds);
    }

    [Theory]
    [InlineData(24, -1, 25, 0)]
    [InlineData(251, 21, 250, 20)]
    public async Task SaveAndLoad_ClampsRapidFireTiming(
        int interval,
        int jitter,
        int expectedInterval,
        int expectedJitter)
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "rapidfire-clamp.exe");
        profile.RapidFire.IntervalMilliseconds = interval;
        profile.RapidFire.JitterMilliseconds = jitter;

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.Single(p => p.Name == profile.Name);

        Assert.False(loaded.RapidFire.IsEnabled);
        Assert.Equal(expectedInterval, loaded.RapidFire.IntervalMilliseconds);
        Assert.Equal(expectedJitter, loaded.RapidFire.JitterMilliseconds);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsCrosshair()
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "crosshair.exe");
        profile.Crosshair.IsEnabled = true;
        profile.Crosshair.HideWhileRightButtonHeld = true;
        profile.Crosshair.ImagePath = @"C:\Screens\My Cross Hair.png";
        profile.Crosshair.SizeAdjustment = -25;

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.Single(p => p.Name == profile.Name);

        Assert.True(loaded.Crosshair.IsEnabled);
        Assert.True(loaded.Crosshair.HideWhileRightButtonHeld);
        Assert.Equal(@"C:\Screens\My Cross Hair.png", loaded.Crosshair.ImagePath);
        Assert.Equal(-25, loaded.Crosshair.SizeAdjustment);
    }

    [Theory]
    [InlineData(80, 50)]
    [InlineData(-90, -50)]
    [InlineData(50, 50)]
    [InlineData(-50, -50)]
    public async Task LoadProfile_OutOfRangeCrosshairSizeAdjustment_IsClamped(int iniValue, int expected)
    {
        var profilesDirectory = Path.Combine(_root, "Profiles");
        Directory.CreateDirectory(profilesDirectory);
        File.WriteAllText(
            Path.Combine(profilesDirectory, "CrosshairSize.ini"),
            "[Profile]\nName=CrosshairSize\nExecutable=crosshair-size.exe\nEnabled=true\n" +
            "[Crosshair]\nEnabled=true\nSizeAdjustment=" + iniValue + "\n");

        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.Single(p => p.Name == "CrosshairSize");

        Assert.Equal(expected, loaded.Crosshair.SizeAdjustment);
    }

    [Fact]
    public async Task LoadProfile_MissingCrosshairSizeAdjustment_DefaultsToZero()
    {
        var profilesDirectory = Path.Combine(_root, "Profiles");
        Directory.CreateDirectory(profilesDirectory);
        File.WriteAllText(
            Path.Combine(profilesDirectory, "CrosshairSizeMissing.ini"),
            "[Profile]\nName=CrosshairSizeMissing\nExecutable=crosshair-size-missing.exe\nEnabled=true\n" +
            "[Crosshair]\nEnabled=true\n");

        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.Single(p => p.Name == "CrosshairSizeMissing");

        Assert.Equal(0, loaded.Crosshair.SizeAdjustment);
    }

    [Fact]
    public async Task LoadProfile_MissingCrosshairSection_UsesDisabledDefaults()
    {
        var profilesDirectory = Path.Combine(_root, "Profiles");
        Directory.CreateDirectory(profilesDirectory);
        File.WriteAllText(
            Path.Combine(profilesDirectory, "LegacyCrosshair.ini"),
            "[Profile]\nName=LegacyCrosshair\nExecutable=legacy-crosshair.exe\nEnabled=true\n");

        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.Single(p => p.Name == "LegacyCrosshair");

        Assert.False(loaded.Crosshair.IsEnabled);
        Assert.False(loaded.Crosshair.HideWhileRightButtonHeld);
        Assert.Equal(string.Empty, loaded.Crosshair.ImagePath);
        Assert.Equal(0, loaded.Crosshair.SizeAdjustment);
    }

    [Fact]
    public async Task LoadProfile_MissingRapidFireSection_UsesDisabledDefaults()
    {
        var profilesDirectory = Path.Combine(_root, "Profiles");
        Directory.CreateDirectory(profilesDirectory);
        File.WriteAllText(
            Path.Combine(profilesDirectory, "Legacy.ini"),
            "[Profile]\nName=Legacy\nExecutable=legacy.exe\nEnabled=true\n");

        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.Single(p => p.Name == "Legacy");

        Assert.False(loaded.RapidFire.IsEnabled);
        Assert.Equal(90, loaded.RapidFire.IntervalMilliseconds);
        Assert.Equal(10, loaded.RapidFire.JitterMilliseconds);
    }

    [Theory]
    [InlineData(0, 1)]    // below range clamps up to 1
    [InlineData(99, 15)]  // above range clamps down to 15
    [InlineData(7, 7)]    // in range round-trips unchanged
    public async Task SaveAndLoad_ClampsAntiAfkInterval(int saved, int expected)
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "antiafkclamp.exe");
        profile.AntiAfk.IntervalMinutes = saved;
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.Equal(expected, loaded.AntiAfk.IntervalMinutes);
    }

    [Fact]
    public async Task DeleteProfileAsync_RemovesFile()
    {
        var profile = ProfileFactory.CreateCustomProfile($"Test_{Guid.NewGuid()}", "todelete.exe");
        await _store.SaveProfileAsync(profile, CancellationToken.None);
        
        await _store.DeleteProfileAsync(profile, CancellationToken.None);
        
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        Assert.DoesNotContain(profiles, p => p.Name == profile.Name);
    }

    [Fact]
    public async Task DeleteProfileAsync_WindowsProfile_DoesNothing()
    {
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var windowsProfile = profiles.First(p => p.IsWindowsProfile);

        // Should not throw
        await _store.DeleteProfileAsync(windowsProfile, CancellationToken.None);

        // Should still exist
        profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        Assert.Contains(profiles, p => p.IsWindowsProfile);
    }

    // ── Merged built-in: color sections live in Win.ini ─────────────────────────────────────────────

    [Fact]
    public async Task MissingWinIni_CreatesDefaultsWithColorEnabled()
    {
        // A fresh install writes Win.ini immediately; its color-only switch defaults to ON (the
        // retired Color built-in's default).
        Assert.False(File.Exists(Path.Combine(_root, "Win.ini")));

        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);

        var windows = profiles.Single(p => p.IsWindowsProfile);
        Assert.True(windows.ColorSettings.IsEnabled);
        Assert.True(File.Exists(Path.Combine(_root, "Win.ini")));
        Assert.False(File.Exists(Path.Combine(_root, "Color.ini"))); // never created anymore

        var iniText = File.ReadAllText(Path.Combine(_root, "Win.ini"));
        Assert.Contains("[Color]", iniText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WindowsProfile_RoundTripsColorSections_InWinIni()
    {
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var windows = profiles.Single(p => p.IsWindowsProfile);

        windows.IsEnabled = false;
        windows.WindowsLauncher.IsEnabled = true;
        windows.ColorSettings.IsEnabled = true;
        windows.ColorSettings.HasSecondary = true;
        windows.ColorSettings.SetProfile(
            new DisplayColorProfile { DisplayId = "DISPLAY1", IsEnabled = true, Brightness = 40, Contrast = 60, Gamma = 1.25, DigitalVibrance = 65 },
            ColorVariant.Primary);
        windows.ColorSettings.SetProfile(
            new DisplayColorProfile { DisplayId = "DISPLAY1", IsEnabled = true, Brightness = 90, Gamma = 0.9, DigitalVibrance = 85 },
            ColorVariant.Secondary);

        await _store.SaveProfileAsync(windows, CancellationToken.None);

        var reloaded = (await _store.LoadProfilesAsync(CancellationToken.None))
            .Single(p => p.IsWindowsProfile);
        Assert.False(reloaded.IsEnabled);
        Assert.True(reloaded.WindowsLauncher.IsEnabled);
        Assert.True(reloaded.ColorSettings.IsEnabled);
        Assert.True(reloaded.ColorSettings.HasSecondary);
        Assert.True(reloaded.LegacyColorImportCompleted); // completed migration never re-fires

        var primary = reloaded.ColorSettings.SnapshotProfiles(ColorVariant.Primary)["DISPLAY1"];
        Assert.Equal(40, primary.Brightness);
        Assert.Equal(60, primary.Contrast);
        Assert.Equal(65, primary.DigitalVibrance);

        var secondary = reloaded.ColorSettings.SnapshotProfiles(ColorVariant.Secondary)["DISPLAY1"];
        Assert.Equal(90, secondary.Brightness);
        Assert.Equal(85, secondary.DigitalVibrance);
    }

    [Fact]
    public async Task MissingWinIni_WithLegacyColorIni_ImportsBeforeCreatingDefaults()
    {
        // codex P2: a legacy Color.ini must be imported even when Win.ini is missing (partial restore,
        // manual deletion, failed write) — creating Win.ini with [Color] DEFAULTS first would mark the
        // migration complete and abandon the legacy presets forever.
        File.WriteAllText(
            Path.Combine(_root, "Color.ini"),
            "[Profile]\nEnabled=true\n\n" +
            "[Color]\nEnabled=true\n\n" +
            "[ColorDisplays]\nDISPLAY1=1|40|60|1.25|65\n");

        var loaded = (await _store.LoadProfilesAsync(CancellationToken.None))
            .Single(p => p.IsWindowsProfile);

        Assert.True(loaded.LegacyColorImportCompleted);
        Assert.True(loaded.ColorSettings.IsEnabled);
        var imported = loaded.ColorSettings.SnapshotProfiles(ColorVariant.Primary)["DISPLAY1"];
        Assert.Equal(40, imported.Brightness);
        Assert.Equal(65, imported.DigitalVibrance);
        Assert.Contains("ColorImported", File.ReadAllText(Path.Combine(_root, "Win.ini")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MigrationFailure_AutosaveWritesDefaults_StillRetriesImport()
    {
        // codex P2: a transiently locked Color.ini fails the import; if the user then edits the
        // default profile, the autosave writes [Color] DEFAULTS to Win.ini. The explicit ColorImported
        // marker (not [Color] presence) must keep the import retryable — the legacy presets are
        // restored on the next launch once the lock is gone.
        File.WriteAllText(
            Path.Combine(_root, "Win.ini"),
            "[Profile]\nEnabled=true\n");
        File.WriteAllText(
            Path.Combine(_root, "Color.ini"),
            "[Profile]\nEnabled=true\n\n" +
            "[Color]\nEnabled=true\n\n" +
            "[ColorDisplays]\nDISPLAY1=1|40|60|1.25|65\n");

        Profile loaded;
        var colorIniPath = Path.Combine(_root, "Color.ini");
        using (new FileStream(colorIniPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            loaded = (await _store.LoadProfilesAsync(CancellationToken.None))
                .Single(p => p.IsWindowsProfile);
            Assert.False(loaded.LegacyColorImportCompleted); // import pending
            Assert.False(loaded.IsPersistenceSuspended);     // session still fully usable
        }

        // User edits the profile before restarting: autosave persists [Color] defaults...
        await _store.SaveProfileAsync(loaded, CancellationToken.None);
        var winIniText = File.ReadAllText(Path.Combine(_root, "Win.ini"));
        Assert.Contains("[Color]", winIniText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ColorImported=False", winIniText, StringComparison.OrdinalIgnoreCase); // ...but NOT completion

        // ...and the next launch still imports the legacy presets.
        var retried = (await _store.LoadProfilesAsync(CancellationToken.None))
            .Single(p => p.IsWindowsProfile);
        Assert.True(retried.LegacyColorImportCompleted);
        var restored = retried.ColorSettings.SnapshotProfiles(ColorVariant.Primary)["DISPLAY1"];
        Assert.Equal(40, restored.Brightness);
        Assert.Equal(65, restored.DigitalVibrance);
    }

    [Fact]
    public async Task PreMergeWinIni_ImportsLegacyColorIni_Once()
    {
        // A Win.ini from before the merge has no [Color] section; the legacy Color.ini's presets
        // are imported into the merged profile and Win.ini is persisted immediately — so a SECOND
        // load reads Win.ini and never re-imports (edits to Color.ini afterwards are ignored).
        File.WriteAllText(
            Path.Combine(_root, "Win.ini"),
            "[Profile]\nEnabled=true\n\n[WindowsLauncher]\nEnabled=true\n\n" +
            "[CapsLock]\nEnabled=true\nMode=0\n");
        File.WriteAllText(
            Path.Combine(_root, "Color.ini"),
            "[Profile]\nEnabled=true\n\n" +
            "[Color]\nEnabled=true\nHasSecondary=false\n\n" +
            "[ColorDisplays]\nDISPLAY1=1|40|60|1.25|65\n");

        var first = (await _store.LoadProfilesAsync(CancellationToken.None))
            .Single(p => p.IsWindowsProfile);

        // The import landed: legacy color values + the preserved Win.ini system settings.
        Assert.True(first.ColorSettings.IsEnabled);
        var imported = first.ColorSettings.SnapshotProfiles(ColorVariant.Primary)["DISPLAY1"];
        Assert.Equal(40, imported.Brightness);
        Assert.Equal(65, imported.DigitalVibrance);
        Assert.True(first.CapsLock.IsEnabled);

        // ...and Win.ini now carries the [Color] sections (the one-time import is durable).
        var winIniText = File.ReadAllText(Path.Combine(_root, "Win.ini"));
        Assert.Contains("[ColorDisplays]", winIniText, StringComparison.OrdinalIgnoreCase);

        // Second load: values come from Win.ini — mutating the legacy Color.ini changes nothing.
        File.WriteAllText(
            Path.Combine(_root, "Color.ini"),
            "[Profile]\nEnabled=true\n\n[Color]\nEnabled=false\n\n" +
            "[ColorDisplays]\nDISPLAY1=1|10|10|1|50\n");
        var second = (await _store.LoadProfilesAsync(CancellationToken.None))
            .Single(p => p.IsWindowsProfile);
        Assert.True(second.ColorSettings.IsEnabled);
        var stable = second.ColorSettings.SnapshotProfiles(ColorVariant.Primary)["DISPLAY1"];
        Assert.Equal(40, stable.Brightness);
    }

    [Fact]
    public async Task PreMergeWinIni_DisabledLegacyColorMaster_ImportsAsColorDisabled()
    {
        // The legacy sidebar master ([Profile] Enabled) and the color-only switch ([Color] Enabled)
        // collapse into the one remaining color switch: either being off leaves color off.
        File.WriteAllText(
            Path.Combine(_root, "Win.ini"),
            "[Profile]\nEnabled=true\n");
        File.WriteAllText(
            Path.Combine(_root, "Color.ini"),
            "[Profile]\nEnabled=false\n\n" +
            "[Color]\nEnabled=true\n");

        var loaded = (await _store.LoadProfilesAsync(CancellationToken.None))
            .Single(p => p.IsWindowsProfile);

        Assert.False(loaded.ColorSettings.IsEnabled);
    }

    [Fact]
    public async Task PreMergeWinIni_WindowsDisabled_ColorEnabled_KeepsColorAlive()
    {
        // codex P2: a user who disabled the old Windows profile but kept the Color profile must not
        // lose global color to the merge. The merged sidebar master follows "either side was on";
        // the disabled Windows master folds into its feature switches so Caps Lock / launchers stay
        // off exactly as before.
        File.WriteAllText(
            Path.Combine(_root, "Win.ini"),
            "[Profile]\nEnabled=false\n\n" +
            "[CapsLock]\nEnabled=true\nMode=0\n");
        File.WriteAllText(
            Path.Combine(_root, "Color.ini"),
            "[Profile]\nEnabled=true\n\n" +
            "[Color]\nEnabled=true\n\n" +
            "[ColorDisplays]\nDISPLAY1=1|40|60|1.25|65\n");

        var loaded = (await _store.LoadProfilesAsync(CancellationToken.None))
            .Single(p => p.IsWindowsProfile);

        Assert.True(loaded.IsEnabled);                       // merged master on via the color side
        Assert.True(loaded.ColorSettings.IsEnabled);         // global color survives the merge
        Assert.Equal(40, loaded.ColorSettings.SnapshotProfiles(ColorVariant.Primary)["DISPLAY1"].Brightness);
        Assert.False(loaded.CapsLock.IsEnabled);             // folded: was master-gated off pre-merge
        Assert.False(loaded.WindowsLauncher.IsEnabled);
    }

    [Fact]
    public async Task PreMergeWinIni_WindowsEnabled_ColorDisabled_KeepsSystemFeatures()
    {
        // The mirror case: color off, Windows features on — Caps Lock must keep working (the merged
        // master stays on via the Windows side) while color imports as disabled.
        File.WriteAllText(
            Path.Combine(_root, "Win.ini"),
            "[Profile]\nEnabled=true\n\n" +
            "[CapsLock]\nEnabled=true\nMode=0\n");
        File.WriteAllText(
            Path.Combine(_root, "Color.ini"),
            "[Profile]\nEnabled=false\n\n" +
            "[Color]\nEnabled=true\n");

        var loaded = (await _store.LoadProfilesAsync(CancellationToken.None))
            .Single(p => p.IsWindowsProfile);

        Assert.True(loaded.IsEnabled);
        Assert.True(loaded.CapsLock.IsEnabled);
        Assert.False(loaded.ColorSettings.IsEnabled);
    }

    [Fact]
    public async Task PreMergeWinIni_BothMastersDisabled_ImportsAllOff()
    {
        // Completes the migration master truth table: both legacy masters off -> merged master off,
        // system features folded off, color off.
        File.WriteAllText(
            Path.Combine(_root, "Win.ini"),
            "[Profile]\nEnabled=false\n\n" +
            "[CapsLock]\nEnabled=true\nMode=0\n");
        File.WriteAllText(
            Path.Combine(_root, "Color.ini"),
            "[Profile]\nEnabled=false\n\n" +
            "[Color]\nEnabled=true\n");

        var loaded = (await _store.LoadProfilesAsync(CancellationToken.None))
            .Single(p => p.IsWindowsProfile);

        Assert.False(loaded.IsEnabled);
        Assert.False(loaded.CapsLock.IsEnabled);
        Assert.False(loaded.WindowsLauncher.IsEnabled);
        Assert.False(loaded.ColorSettings.IsEnabled);
    }

    [Fact]
    public async Task MigrationFailure_ColorIniUnreadable_KeepsLoadedWinIni()
    {
        // codex P2: the optional import must not take down the Win.ini load (the two legacy built-ins
        // failed independently pre-merge). A transiently locked Color.ini keeps the loaded launcher /
        // Caps Lock settings, no persistence suspension, and the import retries next launch.
        File.WriteAllText(
            Path.Combine(_root, "Win.ini"),
            "[Profile]\nEnabled=true\n\n" +
            "[WindowsLauncher]\nEnabled=true\n\n" +
            "[CapsLock]\nEnabled=true\nMode=1\n");
        File.WriteAllText(
            Path.Combine(_root, "Color.ini"),
            "[Profile]\nEnabled=true\n\n[Color]\nEnabled=true\n");

        var colorIniPath = Path.Combine(_root, "Color.ini");
        using (new FileStream(colorIniPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var loaded = (await _store.LoadProfilesAsync(CancellationToken.None))
                .Single(p => p.IsWindowsProfile);

            Assert.False(loaded.IsPersistenceSuspended); // Win.ini itself loaded fine
            Assert.True(loaded.CapsLock.IsEnabled);      // its settings survive
            Assert.True(loaded.WindowsLauncher.IsEnabled);
            Assert.True(loaded.ColorSettings.IsEnabled); // color stays at its enabled default
        }

        // Win.ini was never persisted with [Color] (the save is part of the failed migration), so the
        // import retries once the lock is gone.
        Assert.DoesNotContain("[Color]", File.ReadAllText(Path.Combine(_root, "Win.ini")), StringComparison.OrdinalIgnoreCase);

        var retried = (await _store.LoadProfilesAsync(CancellationToken.None))
            .Single(p => p.IsWindowsProfile);
        Assert.True(retried.CapsLock.IsEnabled); // still the loaded Win.ini settings after the retry
        Assert.Contains("[Color]", File.ReadAllText(Path.Combine(_root, "Win.ini")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PreMergeWinIni_WithoutLegacyColorIni_KeepsColorDefaults()
    {
        // No legacy Color.ini (e.g. manually deleted): the merged profile keeps the factory default
        // (color enabled) and Win.ini is persisted with the [Color] sections so the check never re-fires.
        File.WriteAllText(
            Path.Combine(_root, "Win.ini"),
            "[Profile]\nEnabled=true\n");

        var loaded = (await _store.LoadProfilesAsync(CancellationToken.None))
            .Single(p => p.IsWindowsProfile);

        Assert.True(loaded.ColorSettings.IsEnabled);
        var winIniText = File.ReadAllText(Path.Combine(_root, "Win.ini"));
        Assert.Contains("[Color]", winIniText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LegacyColorIni_IsNeverWrittenOrDeletedByTheStore()
    {
        // The migration reads Color.ini and leaves it untouched on disk.
        File.WriteAllText(Path.Combine(_root, "Win.ini"), "[Profile]\nEnabled=true\n");
        var legacyContent = "[Profile]\nEnabled=true\n\n[Color]\nEnabled=true\n";
        var colorIniPath = Path.Combine(_root, "Color.ini");
        File.WriteAllText(colorIniPath, legacyContent);

        await _store.LoadProfilesAsync(CancellationToken.None);
        var windows = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.IsWindowsProfile);
        await _store.SaveProfileAsync(windows, CancellationToken.None);

        Assert.Equal(legacyContent, File.ReadAllText(colorIniPath));
    }
}
