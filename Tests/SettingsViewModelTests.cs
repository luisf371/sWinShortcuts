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
}
