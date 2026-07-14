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
}
