using Xunit;
using System.Windows.Input;
using sWinShortcuts.Models;

namespace Tests;

public class ProfileTests
{
    [Fact]
    public void Executable_SetsNormalizedExecutable()
    {
        var profile = new Profile { Name = "Test" };

        profile.Executable = "MyGame.exe";

        Assert.Equal("mygame", profile.NormalizedExecutable);
    }

    [Fact]
    public void Executable_NormalizesFullPath()
    {
        var profile = new Profile { Name = "Test" };

        profile.Executable = @"C:\Games\Some Folder\MyGame.exe";

        Assert.Equal("mygame", profile.NormalizedExecutable);
    }

    [Fact]
    public void Executable_HandlesNullAndEmpty()
    {
        var profile = new Profile { Name = "Test" };

        profile.Executable = null!;
        Assert.Equal(string.Empty, profile.NormalizedExecutable);

        profile.Executable = "";
        Assert.Equal(string.Empty, profile.NormalizedExecutable);

        profile.Executable = "   ";
        Assert.Equal(string.Empty, profile.NormalizedExecutable);
    }

    [Fact]
    public void IsWindowsProfile_TrueForWindowsProfileName()
    {
        var profile = new Profile { Name = ProfileConstants.WindowsProfileName };

        Assert.True(profile.IsWindowsProfile);
    }

    [Fact]
    public void IsWindowsProfile_IsCaseInsensitive()
    {
        var profile = new Profile { Name = ProfileConstants.WindowsProfileName.ToUpper() };

        Assert.True(profile.IsWindowsProfile);
    }

    [Fact]
    public void IsColorProfile_TrueForColorProfileName()
    {
        var profile = new Profile { Name = ProfileConstants.ColorProfileName };

        Assert.True(profile.IsColorProfile);
    }

    [Fact]
    public void IsColorProfile_IsCaseInsensitive()
    {
        var profile = new Profile { Name = ProfileConstants.ColorProfileName.ToUpper() };

        Assert.True(profile.IsColorProfile);
    }

    [Fact]
    public void NewProfile_HasDefaultSettings()
    {
        var profile = new Profile { Name = "Test" };

        Assert.NotNull(profile.AltMouse);
        Assert.NotNull(profile.CombinedMappings);
        Assert.NotNull(profile.RightClickHoldBreath);
        Assert.NotNull(profile.ColorSettings);
        Assert.NotNull(profile.CapsLock);
        Assert.NotNull(profile.WindowsLauncher);
    }

    [Fact]
    public void NewProfile_IsEnabledByDefault()
    {
        var profile = new Profile { Name = "Test" };

        Assert.True(profile.IsEnabled);
    }
}

public class AltMouseSettingsTests
{
    [Fact]
    public void DefaultHoldThreshold_Is50Ms()
    {
        var settings = new AltMouseSettings();

        Assert.Equal(50, settings.HoldThresholdMilliseconds);
    }

    [Fact]
    public void Bindings_StartsEmpty()
    {
        var settings = new AltMouseSettings();

        Assert.Empty(settings.Bindings);
    }

    [Fact]
    public void IsEnabled_DefaultsFalse()
    {
        var settings = new AltMouseSettings();

        Assert.False(settings.IsEnabled);
    }
}

public class MouseButtonBindingTests
{
    [Fact]
    public void SuppressOriginalWhileAltIsHeld_TrueWhenTapKeySet()
    {
        var binding = new MouseButtonBinding { TapKey = Key.A };

        Assert.True(binding.SuppressOriginalWhileAltIsHeld);
    }

    [Fact]
    public void SuppressOriginalWhileAltIsHeld_TrueWhenHoldKeySet()
    {
        var binding = new MouseButtonBinding { HoldKey = Key.B };

        Assert.True(binding.SuppressOriginalWhileAltIsHeld);
    }

    [Fact]
    public void SuppressOriginalWhileAltIsHeld_FalseWhenNoKeysSet()
    {
        var binding = new MouseButtonBinding();

        Assert.False(binding.SuppressOriginalWhileAltIsHeld);
    }

    [Fact]
    public void SuppressOriginalWhileAltIsHeld_TrueWhenBothKeysSet()
    {
        var binding = new MouseButtonBinding { TapKey = Key.A, HoldKey = Key.B };

        Assert.True(binding.SuppressOriginalWhileAltIsHeld);
    }
}
