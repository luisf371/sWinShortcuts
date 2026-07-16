using sWinShortcuts.Services;
using Xunit;

namespace Tests;

public class ForegroundWatcherTests
{
    [Fact]
    public void SelectForegroundWindow_StaleCallback_UsesCurrentForegroundWindow()
    {
        var callbackWindow = new IntPtr(1);
        var currentWindow = new IntPtr(2);

        Assert.Equal(currentWindow, ForegroundWatcher.SelectForegroundWindow(callbackWindow, currentWindow));
    }

    [Fact]
    public void SelectForegroundWindow_NoCurrentForegroundWindow_UsesCallbackWindow()
    {
        var callbackWindow = new IntPtr(1);

        Assert.Equal(callbackWindow, ForegroundWatcher.SelectForegroundWindow(callbackWindow, IntPtr.Zero));
    }
}
