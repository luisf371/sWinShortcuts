using System.Collections.Concurrent;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public class ProfileActivationServiceShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopAsync_WhileActivationNotificationBlocked_KeepsCrosshairHidden(bool cancelStopWait)
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Crosshair.IsEnabled = true;
        profile.Crosshair.HideWhileRightButtonHeld = true;
        var store = new InMemoryProfileStore();
        store.Profiles.Add(profile);
        var watcher = new FakeForegroundWatcher();
        var input = new FakeInputHookService();
        var tray = new FakeSystemTrayService();
        var pending = new ConcurrentQueue<Action>();
        using var crosshair = new CrosshairService(new NullLoggerService(), input, pending.Enqueue);
        input.ActiveProfileChanged += (_, active) =>
        {
            if (ReferenceEquals(active, profile))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        };
        var service = new ProfileActivationService(new ProfileManager(store), watcher, input,
            tray, new RecordingColorControlService(), new FakeDisplayService(),
            crosshair, new NullLoggerService());
        await service.StartAsync(CancellationToken.None);
        Task? stop = null;
        try
        {
            watcher.RaiseForegroundChanged("game.exe", 101);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            stop = service.StopAsync(new CancellationToken(canceled: cancelStopWait));
            if (cancelStopWait) await stop.WaitAsync(TimeSpan.FromSeconds(2));
            release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            if (cancelStopWait) await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            while (pending.TryDequeue(out var apply)) apply();

            Assert.False(crosshair.AppliedVisibility);
            Assert.False(input.RightButtonObservation);
            Assert.DoesNotContain(tray.StatusUpdates, update => update.IsProfileActive);
        }
        finally
        {
            release.Set();
            await (stop ?? service.StopAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task StopAsync_AfterForegroundEvent_PreventsFurtherColorApply()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        var foregroundWatcher = new FakeForegroundWatcher();
        var colorControl = new RecordingColorControlService();
        var displayService = new FakeDisplayService
        {
            Displays = [CreateDisplay("DISPLAY1")]
        };
        var service = new ProfileActivationService(
            manager,
            foregroundWatcher,
            new FakeInputHookService(),
            new FakeSystemTrayService(),
            colorControl,
            displayService,
            new FakeCrosshairService(),
            new NullLoggerService());

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        await service.StopAsync(CancellationToken.None);
        colorControl.AppliedProfiles.Clear();

        foregroundWatcher.RaiseForegroundChanged("unknown.exe");
        await Task.Delay(100);

        Assert.Empty(colorControl.AppliedProfiles);
    }

    [Fact]
    public async Task StopAsync_WhileFirstDisplayApplyBlocked_DoesNotApplySecondDisplay()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var manager = new ProfileManager(new InMemoryProfileStore());
        await manager.InitializeAsync();
        manager.WindowsProfile.ColorSettings.IsEnabled = true;
        foreach (var id in new[] { "DISPLAY1", "DISPLAY2" })
        {
            manager.WindowsProfile.ColorSettings.SetProfile(new DisplayColorProfile
            {
                DisplayId = id, IsEnabled = true, Brightness = 70
            });
        }
        var color = new BlockingFirstDisplayColorService(entered, release);
        var service = new ProfileActivationService(manager, new FakeForegroundWatcher(), new FakeInputHookService(),
            new FakeSystemTrayService(), color,
            new FakeDisplayService { Displays = [CreateDisplay("DISPLAY1"), CreateDisplay("DISPLAY2")] },
            new FakeCrosshairService(), new NullLoggerService());
        await service.StartAsync(CancellationToken.None);
        Task? stop = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            // StopAsync publishes its stopping flag before its first await.
            stop = service.StopAsync(CancellationToken.None);
            release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(["DISPLAY1"], color.DisplayIds.ToArray());
        }
        finally
        {
            release.Set();
            await (stop ?? service.StopAsync(CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class BlockingFirstDisplayColorService(ManualResetEventSlim entered, ManualResetEventSlim release)
        : IColorControlService
    {
        public ConcurrentQueue<string> DisplayIds { get; } = new();

        public ColorApplyOutcome Apply(DisplayInfo display, DisplayColorProfile profile)
        {
            DisplayIds.Enqueue(display.Id);
            if (DisplayIds.Count == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
            return ColorApplyOutcome.Applied;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static DisplayInfo CreateDisplay(string id)
    {
        return new DisplayInfo
        {
            Id = id,
            Name = id,
            DeviceName = $@"\\.\{id}",
            IsPrimary = true
        };
    }
}
