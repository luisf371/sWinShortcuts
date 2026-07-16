using sWinShortcuts.Services;
using Xunit;

namespace Tests;

public class ForegroundWatcherTests
{
    [Fact]
    public void ProcessForegroundChange_QueuedIntermediateWindow_RequestsResetForLiveWindow()
    {
        using var watcher = new ForegroundWatcher();
        var liveWindow = new IntPtr(1);
        var intermediateWindow = new IntPtr(2);
        List<ForegroundChangedEventArgs> notifications = [];
        watcher.ForegroundChanged += (_, e) => notifications.Add(e);

        watcher.ProcessForegroundChange(liveWindow, liveWindow);
        notifications.Clear();
        watcher.ProcessForegroundChange(intermediateWindow, liveWindow);

        var notification = Assert.Single(notifications);
        Assert.Equal(liveWindow, notification.WindowHandle);
        Assert.True(notification.RequiresInputReset);
    }

    [Fact]
    public void ProcessForegroundChange_NoCurrentForegroundWindow_PublishesEventWithoutReset()
    {
        using var watcher = new ForegroundWatcher();
        var eventWindow = new IntPtr(1);
        ForegroundChangedEventArgs? notification = null;
        watcher.ForegroundChanged += (_, e) => notification = e;

        watcher.ProcessForegroundChange(eventWindow, IntPtr.Zero);

        Assert.NotNull(notification);
        Assert.Equal(eventWindow, notification.WindowHandle);
        Assert.False(notification.RequiresInputReset);
    }
}
