using Xunit;
using System.Windows.Input;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;

namespace Tests;

public class ProfileFactoryTests
{
    [Fact]
    public void CreateWindowsProfile_HasCorrectName()
    {
        var profile = ProfileFactory.CreateWindowsProfile();

        Assert.Equal(ProfileConstants.WindowsProfileName, profile.Name);
        Assert.True(profile.IsWindowsProfile);
        Assert.False(profile.IsColorProfile);
    }

    [Fact]
    public void CreateWindowsProfile_HasNumpadLaunchers()
    {
        var profile = ProfileFactory.CreateWindowsProfile();

        var expectedKeys = new[]
        {
            Key.NumPad0, Key.NumPad1, Key.NumPad2, Key.NumPad3, Key.NumPad4,
            Key.NumPad5, Key.NumPad6, Key.NumPad7, Key.NumPad8, Key.NumPad9
        };

        foreach (var key in expectedKeys)
        {
            Assert.True(profile.WindowsLauncher.Launchers.ContainsKey(key),
                $"Missing launcher for {key}");
        }
    }

    [Fact]
    public void CreateWindowsProfile_IsEnabled()
    {
        var profile = ProfileFactory.CreateWindowsProfile();

        Assert.True(profile.IsEnabled);
    }

    [Fact]
    public void CreateWindowsProfile_HasEmptyExecutable()
    {
        var profile = ProfileFactory.CreateWindowsProfile();

        Assert.Equal(string.Empty, profile.Executable);
        Assert.Equal(string.Empty, profile.NormalizedExecutable);
    }

    [Fact]
    public void CreateColorProfile_HasCorrectName()
    {
        var profile = ProfileFactory.CreateColorProfile();

        Assert.Equal(ProfileConstants.ColorProfileName, profile.Name);
        Assert.True(profile.IsColorProfile);
        Assert.False(profile.IsWindowsProfile);
    }

    [Fact]
    public void CreateColorProfile_HasAllFeaturesDisabled()
    {
        var profile = ProfileFactory.CreateColorProfile();

        Assert.False(profile.AltMouse.IsEnabled);
        Assert.False(profile.CombinedMappings.IsEnabled);
        Assert.False(profile.RightClickHoldBreath.IsEnabled);
        Assert.False(profile.CapsLock.IsEnabled);
        Assert.False(profile.WindowsLauncher.IsEnabled);
    }

    [Fact]
    public void CreateCustomProfile_SetsNameAndExecutable()
    {
        var profile = ProfileFactory.CreateCustomProfile("MyGame", "game.exe");

        Assert.Equal("MyGame", profile.Name);
        Assert.Equal("game.exe", profile.Executable);
        Assert.Equal("game", profile.NormalizedExecutable);
    }

    [Fact]
    public void CreateCustomProfile_IsEnabled()
    {
        var profile = ProfileFactory.CreateCustomProfile("MyGame", "game.exe");

        Assert.True(profile.IsEnabled);
    }

    [Fact]
    public void CreateCustomProfile_HasCapsLockDisabled()
    {
        var profile = ProfileFactory.CreateCustomProfile("MyGame", "game.exe");

        Assert.False(profile.CapsLock.IsEnabled);
    }

    [Fact]
    public void CreateCustomProfile_IsNotSpecialProfile()
    {
        var profile = ProfileFactory.CreateCustomProfile("MyGame", "game.exe");

        Assert.False(profile.IsWindowsProfile);
        Assert.False(profile.IsColorProfile);
    }
}
