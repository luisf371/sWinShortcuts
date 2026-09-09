using System;
using System.IO;
using System.Windows.Input;
using sWinShortcuts.Utilities;
using Xunit;

namespace Tests;

public sealed class AppSettingsTests
{
    [Theory]
    [InlineData(Key.LeftShift, null)]
    [InlineData(Key.RightShift, null)]
    [InlineData(Key.LeftCtrl, null)]
    [InlineData(Key.RightCtrl, null)]
    [InlineData(Key.LeftAlt, null)]
    [InlineData(Key.RightAlt, null)]
    [InlineData(Key.LWin, null)]
    [InlineData(Key.RWin, null)]
    [InlineData(Key.System, null)]
    [InlineData(Key.DeadCharProcessed, null)]
    [InlineData((Key)9999, null)]
    [InlineData(Key.None, null)]
    [InlineData(Key.F8, Key.F8)]
    [InlineData(Key.F13, Key.F13)]
    [InlineData(Key.ImeProcessed, Key.ImeProcessed)]
    public void ToggleKeys_LoadAndSave_NormalizeUnsupportedKeys(Key key, Key? expected)
    {
        var path = Path.GetTempFileName();
        try
        {
            var ini = new IniDocument();
            ini.SetValue("App", AppSettings.ColorToggleKeyName, key.ToString());
            ini.SetValue("App", AppSettings.RapidFireToggleKeyName, key.ToString());
            ini.Save(path);
            Assert.Equal(expected, AppSettings.LoadColorToggleKey(path));
            Assert.Equal(expected, AppSettings.LoadRapidFireToggleKey(path));

            AppSettings.SetColorToggleKey(ini, key);
            AppSettings.SetRapidFireToggleKey(ini, key);
            Assert.Equal(expected?.ToString() ?? "None", ini.GetValue("App", AppSettings.ColorToggleKeyName));
            Assert.Equal(expected?.ToString() ?? "None", ini.GetValue("App", AppSettings.RapidFireToggleKeyName));
            ini.Save(path);
            Assert.Equal(expected, AppSettings.LoadColorToggleKey(path));
            Assert.Equal(expected, AppSettings.LoadRapidFireToggleKey(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SetColorToggleKey_UsesExplicitNoneMarker()
    {
        var document = new IniDocument();

        AppSettings.SetColorToggleKey(document, Key.None);

        Assert.Equal("None", document.GetValue("App", AppSettings.ColorToggleKeyName));
    }

    [Fact]
    public void RapidFireToggleKey_RoundTripsAndUsesExplicitNoneMarker()
    {
        var root = CreateTempDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "sWinShortcuts.ini");
            var document = new IniDocument();
            AppSettings.SetRapidFireToggleKey(document, Key.F8);
            document.Save(settingsPath);

            Assert.Equal(Key.F8, AppSettings.LoadRapidFireToggleKey(settingsPath));

            AppSettings.SetRapidFireToggleKey(document, Key.None);
            Assert.Equal("None", document.GetValue("App", AppSettings.RapidFireToggleKeyName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadColorToggleKey_ConfiguredAppValue_ReturnsKey()
    {
        var root = CreateTempDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "sWinShortcuts.ini");
            var appSettings = new IniDocument();
            AppSettings.SetColorToggleKey(appSettings, Key.F8);
            appSettings.Save(settingsPath);

            Assert.Equal(Key.F8, AppSettings.LoadColorToggleKey(settingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadColorToggleKey_ExplicitNone_ReturnsNull()
    {
        var root = CreateTempDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "sWinShortcuts.ini");
            var appSettings = new IniDocument();
            AppSettings.SetColorToggleKey(appSettings, Key.None);
            appSettings.Save(settingsPath);

            Assert.Null(AppSettings.LoadColorToggleKey(settingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadColorToggleKey_MissingKey_ReturnsNull()
    {
        var root = CreateTempDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "sWinShortcuts.ini");

            Assert.Null(AppSettings.LoadColorToggleKey(settingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadColorToggleKey_MissingKey_IgnoresColorIni()
    {
        var root = CreateTempDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "sWinShortcuts.ini");
            var legacy = new IniDocument();
            legacy.SetKey("Color", "ToggleKey", Key.F9);
            legacy.Save(Path.Combine(root, "Color.ini"));

            Assert.Null(AppSettings.LoadColorToggleKey(settingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadCheckForUpdatesEnabled_AbsentKeyOrFile_ReturnsFalse()
    {
        var root = CreateTempDirectory();
        try
        {
            // Missing file entirely.
            var missingPath = Path.Combine(root, "missing.ini");
            Assert.False(AppSettings.LoadCheckForUpdatesEnabled(missingPath));

            // File present, key absent.
            var settingsPath = Path.Combine(root, "sWinShortcuts.ini");
            new IniDocument().Save(settingsPath);
            Assert.False(AppSettings.LoadCheckForUpdatesEnabled(settingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadCheckForUpdatesEnabled_TrueLiteral_ReturnsTrue()
    {
        var root = CreateTempDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "sWinShortcuts.ini");
            var document = new IniDocument();
            document.SetValue("App", AppSettings.CheckForUpdatesKeyName, "true");
            document.Save(settingsPath);

            Assert.True(AppSettings.LoadCheckForUpdatesEnabled(settingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadCheckForUpdatesEnabled_FalseLiteral_ReturnsFalse()
    {
        var root = CreateTempDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "sWinShortcuts.ini");
            var document = new IniDocument();
            document.SetValue("App", AppSettings.CheckForUpdatesKeyName, "false");
            document.Save(settingsPath);

            Assert.False(AppSettings.LoadCheckForUpdatesEnabled(settingsPath));

            // Default-off is literal-match only: any other casing must not enable the check.
            document.SetValue("App", AppSettings.CheckForUpdatesKeyName, "TRUE");
            document.Save(settingsPath);
            Assert.False(AppSettings.LoadCheckForUpdatesEnabled(settingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sWinShortcutsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
