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
            displayService);

        await service.StartAsync(CancellationToken.None);
        await WaitForAsync(() => colorControl.AppliedProfiles.Count == 1);

        foregroundWatcher.RaiseForegroundChanged("unknown.exe");
        foregroundWatcher.RaiseForegroundChanged("unknown.exe");
        await Task.Delay(100);

        Assert.Single(colorControl.AppliedProfiles);

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
