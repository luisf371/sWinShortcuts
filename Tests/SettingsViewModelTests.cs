using System.IO;
using System.Windows.Input;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void ColorToggleKey_UpdatesHookImmediately_AndNoneClearsIt()
    {
        var inputHook = new FakeInputHookService();
        var viewModel = new SettingsViewModel(new NullLoggerService(), inputHook);

        viewModel.ColorToggleKey = Key.F8;
        Assert.Equal(Key.F8, inputHook.LastColorToggleKey);

        viewModel.ColorToggleKey = Key.None;
        Assert.Null(inputHook.LastColorToggleKey);
    }

    [Fact]
    public void RapidFireToggleKey_UpdatesHookImmediately_AndNoneClearsIt()
    {
        var inputHook = new FakeInputHookService();
        var viewModel = new SettingsViewModel(new NullLoggerService(), inputHook);

        viewModel.RapidFireToggleKey = Key.F8;
        Assert.Equal(Key.F8, inputHook.LastRapidFireToggleKey);

        viewModel.RapidFireToggleKey = Key.None;
        Assert.Null(inputHook.LastRapidFireToggleKey);
    }

    [Fact]
    public void StartMinimized_RoundTripsAndNotifies()
    {
        var viewModel = new SettingsViewModel(new NullLoggerService(), new FakeInputHookService());
        Assert.False(viewModel.StartMinimized);

        var fired = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.StartMinimized)) fired++;
        };

        viewModel.StartMinimized = true;
        Assert.True(viewModel.StartMinimized);
        Assert.Equal(1, fired);

        // Setting the same value must not re-fire (avoids spurious autosave/etc. churn).
        viewModel.StartMinimized = true;
        Assert.Equal(1, fired);

        viewModel.StartMinimized = false;
        Assert.False(viewModel.StartMinimized);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void CheckForUpdates_RoundTripsAndNotifies()
    {
        // Default OFF (card requirement): a fresh view model must report disabled.
        var viewModel = new SettingsViewModel(new NullLoggerService(), new FakeInputHookService());
        Assert.False(viewModel.CheckForUpdates);

        var fired = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.CheckForUpdates)) fired++;
        };

        viewModel.CheckForUpdates = true;
        Assert.True(viewModel.CheckForUpdates);
        Assert.Equal(1, fired);

        // Setting the same value must not re-fire.
        viewModel.CheckForUpdates = true;
        Assert.Equal(1, fired);

        viewModel.CheckForUpdates = false;
        Assert.False(viewModel.CheckForUpdates);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void CanChooseAdmin_RequiresElevationEvenWhenStartupLoadedAndStartWithWindows()
    {
        var viewModel = new SettingsViewModel(new NullLoggerService(), new FakeInputHookService())
        {
            IsRunningAsAdmin = false,
            IsStartupLoaded = true,
            StartWithWindows = true
        };

        // Non-admin: the option is grayed out regardless of the other conditions.
        Assert.False(viewModel.CanChooseAdmin);

        // Elevating re-enables it.
        viewModel.IsRunningAsAdmin = true;
        Assert.True(viewModel.CanChooseAdmin);
    }

    [Fact]
    public void StartAsAdmin_CoercedOffWhenNonAdmin()
    {
        var viewModel = new SettingsViewModel(new NullLoggerService(), new FakeInputHookService())
        {
            IsRunningAsAdmin = false,
            IsStartupLoaded = true,
            StartWithWindows = true
        };

        // A non-admin process cannot manage the HIGHEST task; assigning true must NOT stick.
        viewModel.StartAsAdmin = true;
        Assert.False(viewModel.StartAsAdmin);

        // Elevating allows it again.
        viewModel.IsRunningAsAdmin = true;
        viewModel.StartAsAdmin = true;
        Assert.True(viewModel.StartAsAdmin);
    }

    [Fact]
    public void EnableDebugLogging_UserToggle_RecordsEntryWithLoggerStateOrdering()
    {
        var logger = new NullLoggerService();
        var viewModel = new SettingsViewModel(logger, new FakeInputHookService());

        // Enable: the logger is switched on BEFORE the entry is written (it would otherwise be
        // dropped by the logger's own gating — a recorded entry proves the ordering).
        viewModel.EnableDebugLogging = true;
        Assert.True(logger.IsEnabled);
        Assert.Single(logger.Messages, m => m == "[Settings] Debug logging enabled via settings");

        // Same-value reassignment records nothing.
        viewModel.EnableDebugLogging = true;
        Assert.Single(logger.Messages);

        // Disable: the entry is written while the logger is still enabled, then it goes off.
        viewModel.EnableDebugLogging = false;
        Assert.False(logger.IsEnabled);
        Assert.Equal("[Settings] Debug logging disabled via settings", logger.Messages[^1]);
    }

    [Fact]
    public void EnableDebugLogging_ProgrammaticApply_ChangesStateWithoutViaSettingsEntry()
    {
        var logger = new NullLoggerService { IsEnabled = true };
        var viewModel = new SettingsViewModel(logger, new FakeInputHookService());
        viewModel.EnableDebugLogging = true;
        logger.Messages.Clear();

        // INI hydration to a DIFFERING value: the live state flips with no "via settings" entry —
        // hydration is not a user toggle.
        viewModel.SetEnableDebugLoggingProgrammatically(false);
        Assert.False(viewModel.EnableDebugLogging);
        Assert.False(logger.IsEnabled);
        Assert.Empty(logger.Messages);

        viewModel.SetEnableDebugLoggingProgrammatically(true);
        Assert.True(viewModel.EnableDebugLogging);
        Assert.True(logger.IsEnabled);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public void RollBackEnableDebugLogging_RecordsRollbackWithCauseAndOrdering()
    {
        var logger = new NullLoggerService { IsEnabled = true };
        var viewModel = new SettingsViewModel(logger, new FakeInputHookService());
        viewModel.EnableDebugLogging = true;
        logger.Messages.Clear();

        // Baseline equal to the current value: nothing recorded, no state touched.
        viewModel.RollBackEnableDebugLogging(true);
        Assert.True(logger.IsEnabled);
        Assert.Empty(logger.Messages);

        // Roll back to disabled: the entry is written while the logger still holds the enabled
        // state being described.
        viewModel.RollBackEnableDebugLogging(false);
        Assert.False(viewModel.EnableDebugLogging);
        Assert.False(logger.IsEnabled);
        Assert.Single(logger.Messages, m => m == "[Settings] Debug logging disabled (settings dialog cancelled)");

        // Roll back to enabled: the logger is re-enabled FIRST so the entry is recorded.
        viewModel.RollBackEnableDebugLogging(true);
        Assert.True(viewModel.EnableDebugLogging);
        Assert.True(logger.IsEnabled);
        Assert.Single(logger.Messages, m => m == "[Settings] Debug logging re-enabled (settings dialog cancelled)");
    }

    [Fact]
    public void TryLoadIniState_ReadFails_PreservesLiveSettingsAndDisablesSave()
    {
        var path = Path.GetTempFileName();
        try
        {
            var logger = new NullLoggerService { IsEnabled = true };
            var hook = new FakeInputHookService { HookWatchdogEnabled = false };
            var vm = new SettingsViewModel(logger, hook)
            {
                ColorToggleKey = Key.F8,
                RapidFireToggleKey = Key.F9,
                IsStartupLoaded = true
            };
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            Assert.False(vm.TryLoadIniState(path, out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.True(logger.IsEnabled);
            Assert.False(hook.HookWatchdogEnabled);
            Assert.Equal(Key.F8, hook.LastColorToggleKey);
            Assert.Equal(Key.F9, hook.LastRapidFireToggleKey);
            Assert.False(vm.IsIniLoaded);
            Assert.False(vm.CanSave);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryLoadIniState_DirectoryPath_PreservesLiveSettingsAndDisablesSave()
    {
        var directory = Directory.CreateTempSubdirectory("sWinShortcuts-settings-");
        try
        {
            var logger = new NullLoggerService { IsEnabled = true };
            var hook = new FakeInputHookService { HookWatchdogEnabled = false };
            var vm = new SettingsViewModel(logger, hook)
            {
                ColorToggleKey = Key.F8,
                RapidFireToggleKey = Key.F9,
                IsStartupLoaded = true
            };

            Assert.False(vm.TryLoadIniState(directory.FullName, out var error));
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.False(vm.IsIniLoaded);
            Assert.False(vm.CanSave);
            Assert.True(logger.IsEnabled);
            Assert.False(hook.HookWatchdogEnabled);
            Assert.Equal(Key.F8, hook.LastColorToggleKey);
            Assert.Equal(Key.F9, hook.LastRapidFireToggleKey);
        }
        finally
        {
            directory.Delete();
        }
    }

    [Fact]
    public void TryLoadIniState_DefaultValues_ReplaceDifferentLiveSettings()
    {
        var hook = new FakeInputHookService { HookWatchdogEnabled = true, AdvancedModeEnabled = true };
        hook.SetColorToggleKey(Key.F8);
        hook.SetRapidFireToggleKey(Key.F9);
        var vm = new SettingsViewModel(new NullLoggerService(), hook);
        var ini = new sWinShortcuts.Utilities.IniDocument();
        ini.SetValue("App", "HookWatchdog", "false");
        ini.SetValue("App", "AdvancedMode", "false");
        ini.SetValue("App", "ColorToggleKey", "None");
        ini.SetValue("App", "RapidFireToggleKey", "None");

        Assert.True(vm.TryLoadIniState(ini, out var error));

        Assert.Null(error);
        Assert.False(hook.HookWatchdogEnabled);
        Assert.False(vm.HookWatchdogEnabled);
        Assert.False(hook.AdvancedModeEnabled);
        Assert.False(vm.AdvancedModeEnabled);
        Assert.Null(hook.LastColorToggleKey);
        Assert.Equal(Key.None, vm.ColorToggleKey);
        Assert.Null(hook.LastRapidFireToggleKey);
        Assert.Equal(Key.None, vm.RapidFireToggleKey);
    }

    [Fact]
    public void TryLoadIniState_ReadSucceeds_AppliesCapturedSettingsWithoutUserToggleLog()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """
                [App]
                EnableDebugLogging=true
                HookWatchdog=true
                AdvancedMode=true
                StartMinimized=true
                CheckForUpdates=true
                ColorToggleKey=F6
                RapidFireToggleKey=F7
                """);
            var logger = new NullLoggerService();
            var hook = new FakeInputHookService { HookWatchdogEnabled = false };
            var vm = new SettingsViewModel(logger, hook) { IsStartupLoaded = true };

            Assert.True(vm.TryLoadIniState(path, out var error));

            Assert.Null(error);
            Assert.True(vm.IsIniLoaded);
            Assert.True(vm.CanSave);
            Assert.True(vm.EnableDebugLogging);
            Assert.True(logger.IsEnabled);
            Assert.True(vm.HookWatchdogEnabled);
            Assert.True(hook.HookWatchdogEnabled);
            Assert.True(vm.AdvancedModeEnabled);
            Assert.True(hook.AdvancedModeEnabled);
            Assert.True(vm.StartMinimized);
            Assert.True(vm.CheckForUpdates);
            Assert.Equal(Key.F6, hook.LastColorToggleKey);
            Assert.Equal(Key.F7, hook.LastRapidFireToggleKey);
            Assert.Empty(logger.Messages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryLoadIniState_MissingFile_LoadsDefaultsAndKeepsLiveAdvancedMode(bool advancedMode)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sWinShortcuts-settings-{Guid.NewGuid():N}.ini");
        var logger = new NullLoggerService { IsEnabled = true };
        var hook = new FakeInputHookService { AdvancedModeEnabled = advancedMode, HookWatchdogEnabled = false };
        var vm = new SettingsViewModel(logger, hook)
        {
            ColorToggleKey = Key.F8,
            RapidFireToggleKey = Key.F9,
            StartMinimized = true,
            CheckForUpdates = true
        };

        Assert.True(vm.TryLoadIniState(path, out var error));

        Assert.Null(error);
        Assert.True(vm.IsIniLoaded);
        Assert.False(vm.CanSave);
        Assert.False(logger.IsEnabled);
        Assert.True(hook.HookWatchdogEnabled);
        Assert.Equal(advancedMode, hook.AdvancedModeEnabled);
        Assert.Equal(advancedMode, vm.AdvancedModeEnabled);
        Assert.False(vm.StartMinimized);
        Assert.False(vm.CheckForUpdates);
        Assert.Equal(Key.None, vm.ColorToggleKey);
        Assert.Equal(Key.None, vm.RapidFireToggleKey);
        Assert.Null(hook.LastColorToggleKey);
        Assert.Null(hook.LastRapidFireToggleKey);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void TryLoadIniState_RetryAfterUnlock_EnablesSaveAndNotifiesReadiness()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[App]\nColorToggleKey=F6\nRapidFireToggleKey=F7\n");
            var hook = new FakeInputHookService();
            var vm = new SettingsViewModel(new NullLoggerService(), hook) { IsStartupLoaded = true };
            var changed = new List<string?>();
            vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.False(vm.TryLoadIniState(path, out _));
                Assert.False(vm.CanSave);
            }

            Assert.True(vm.TryLoadIniState(path, out var error));

            Assert.Null(error);
            Assert.True(vm.IsIniLoaded);
            Assert.True(vm.CanSave);
            Assert.Equal(Key.F6, hook.LastColorToggleKey);
            Assert.Equal(Key.F7, hook.LastRapidFireToggleKey);
            Assert.Contains(nameof(SettingsViewModel.IsIniLoaded), changed);
            Assert.Contains(nameof(SettingsViewModel.CanSave), changed);

            changed.Clear();
            using var lockedAgain = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.False(vm.TryLoadIniState(path, out _));
            Assert.False(vm.CanSave);
            Assert.Equal(Key.F6, hook.LastColorToggleKey);
            Assert.Equal(Key.F7, hook.LastRapidFireToggleKey);
            Assert.Contains(nameof(SettingsViewModel.IsIniLoaded), changed);
            Assert.Contains(nameof(SettingsViewModel.CanSave), changed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void CanSave_RequiresIniAndStartupReadiness_AndNoSaveInFlight(
        bool iniLoaded, bool startupLoaded, bool saving, bool expected)
    {
        var vm = new SettingsViewModel(new NullLoggerService(), new FakeInputHookService());
        if (iniLoaded)
        {
            var missingPath = Path.Combine(Path.GetTempPath(), $"sWinShortcuts-settings-{Guid.NewGuid():N}.ini");
            Assert.True(vm.TryLoadIniState(missingPath, out _));
        }
        vm.IsStartupLoaded = startupLoaded;
        vm.IsSaving = saving;

        Assert.Equal(expected, vm.CanSave);
    }

}
