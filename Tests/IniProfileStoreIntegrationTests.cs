using Xunit;
using System.Windows.Input;
using sWinShortcuts.Configuration;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

/// <summary>
/// Integration tests for IniProfileStore.
/// These tests use the real filesystem (AppData).
/// </summary>
public class IniProfileStoreIntegrationTests : IDisposable
{
    private readonly IniProfileStore _store;
    private readonly List<Profile> _createdProfiles = [];

    public IniProfileStoreIntegrationTests()
    {
        _store = new IniProfileStore();
    }

    public void Dispose()
    {
        // Cleanup: delete any profiles we created
        foreach (var profile in _createdProfiles)
        {
            try
            {
                _store.DeleteProfileAsync(profile, CancellationToken.None).Wait();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task LoadProfilesAsync_ReturnsWindowsAndColorProfiles()
    {
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);

        Assert.Contains(profiles, p => p.IsWindowsProfile);
        Assert.Contains(profiles, p => p.IsColorProfile);
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
        profile.CapsLock.Mode = CapsLockMode.Remap;
        profile.CapsLock.RemapTarget = Key.Escape;
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.True(loaded.CapsLock.IsEnabled);
        Assert.Equal(CapsLockMode.Remap, loaded.CapsLock.Mode);
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
        _createdProfiles.Add(profile);

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var loaded = profiles.FirstOrDefault(p => p.Name == profile.Name);

        Assert.NotNull(loaded);
        Assert.True(loaded.RightClickHoldBreath.IsEnabled);
        Assert.Equal(HoldBreathMode.Toggle, loaded.RightClickHoldBreath.Mode);
        Assert.Equal(Key.LeftShift, loaded.RightClickHoldBreath.HoldBreathKey);
        Assert.Equal(150, loaded.RightClickHoldBreath.DelayMilliseconds);
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

    [Fact]
    public async Task DeleteProfileAsync_ColorProfile_DoesNothing()
    {
        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var colorProfile = profiles.First(p => p.IsColorProfile);

        // Should not throw
        await _store.DeleteProfileAsync(colorProfile, CancellationToken.None);

        // Should still exist
        profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        Assert.Contains(profiles, p => p.IsColorProfile);
    }
}
