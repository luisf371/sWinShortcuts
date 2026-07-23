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
}
