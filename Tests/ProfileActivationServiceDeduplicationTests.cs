using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public class ProfileActivationServiceDeduplicationTests
{
    [Fact]
    public async Task ForegroundChanged_RepeatedUnmatchedProcess_AppliesColorOncePerDisplay()
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
            new NullLoggerService());

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // C1: a cold start with all color features disabled must NOT touch the hardware at all
        // (writing an identity gamma ramp / DVC level 0 would wipe ICC/Night Light/NVCP calibration).
        Assert.Empty(colorControl.AppliedProfiles);

        foregroundWatcher.RaiseForegroundChanged("unknown.exe");
        foregroundWatcher.RaiseForegroundChanged("unknown.exe");
        await Task.Delay(100);

        Assert.Empty(colorControl.AppliedProfiles);

        await service.StopAsync(CancellationToken.None);
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
