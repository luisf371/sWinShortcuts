using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class NvidiaColorControlServiceTests
{
    [Theory]
    [InlineData(@"\\.\DISPLAY1", "DISPLAY1", true)]
    [InlineData("display1", @"\\.\DISPLAY1", true)]
    [InlineData(@"\\.\DISPLAY1", @"\\.\DISPLAY10", false)]
    [InlineData("DISPLAY1", "OTHERDISPLAY1", false)]
    [InlineData("", "", false)]
    [InlineData(null, "DISPLAY1", false)]
    [InlineData("DISPLAY1", null, false)]
    [InlineData(" ", " ", false)]
    public void DisplayNamesMatch_UsesExactNormalizedIdentity(string? requested, string? actual, bool expected)
    {
        Assert.Equal(expected, NvidiaColorControlService.DisplayNamesMatch(requested, actual));
    }

    [Theory]
    [InlineData(GpuVendor.Nvidia, 1, true)]
    [InlineData(GpuVendor.Unknown, 1, false)]
    [InlineData(GpuVendor.Amd, 1, false)]
    [InlineData(GpuVendor.Intel, 1, false)]
    [InlineData(GpuVendor.Nvidia, 0, false)]
    [InlineData(GpuVendor.Nvidia, 2, false)]
    public void UnmatchedDisplay_RequiresKnownNvidiaAndOneHandle(GpuVendor vendor, int count, bool expected)
    {
        Assert.Equal(expected, NvidiaColorControlService.CanUseUnmatchedDisplay(vendor, count));
    }
    [Fact]
    public async Task DisplayChange_WhenNativeApplyBlocked_ReturnsAndInvalidatesBeforeNextApply()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var service = new NvidiaColorControlService(new NullLoggerService(), (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            }
            return ColorApplyOutcome.Applied;
        }, () => { });
        var cache = (Dictionary<string, IntPtr>)typeof(NvidiaColorControlService)
            .GetField("_handleCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(service)!;
        cache["DISPLAY1"] = new IntPtr(1);
        var apply = Task.Run(() => service.ApplyDigitalVibrance(CreateDisplay(), CreateProfile()));
        Task? notification = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            notification = Task.Run(() => RefreshTopology(service));
            await notification.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(apply.IsCompleted);
            Assert.Single(cache);
        }
        finally
        {
            release.Set();
            await apply;
            if (notification is not null) await notification;
        }

        Assert.Equal(ColorApplyOutcome.Applied,
            service.ApplyDigitalVibrance(CreateDisplay(), CreateProfile()));
        Assert.Empty(cache);
    }

    [Fact]
    public async Task Dispose_WhenNativeApplyBlocked_ReturnsBeforeNativeCleanup()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var cleanup = new ManualResetEventSlim(false);
        var disposeCalls = 0;
        var applyCalls = 0;
        var service = new NvidiaColorControlService(new NullLoggerService(), (_, _) =>
        {
            Interlocked.Increment(ref applyCalls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            return ColorApplyOutcome.Applied;
        }, () =>
        {
            Interlocked.Increment(ref disposeCalls);
            cleanup.Set();
        });
        var apply = Task.Run(() => service.ApplyDigitalVibrance(CreateDisplay(), CreateProfile()));
        Task? dispose = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            dispose = Task.Run(service.Dispose);
            await dispose.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.False(cleanup.IsSet);
            Assert.Equal(ColorApplyOutcome.Skipped,
                await Task.Run(() => service.ApplyDigitalVibrance(CreateDisplay(), CreateProfile()))
                    .WaitAsync(TimeSpan.FromMilliseconds(500)));
            await Task.Run(() => RefreshTopology(service)).WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.False(cleanup.IsSet);
        }
        finally
        {
            release.Set();
            await apply.WaitAsync(TimeSpan.FromSeconds(2));
            if (dispose is not null)
            {
                await dispose.WaitAsync(TimeSpan.FromSeconds(2));
            }
            service.Dispose();
        }
        Assert.True(cleanup.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, Volatile.Read(ref disposeCalls));
        Assert.Equal(1, Volatile.Read(ref applyCalls));
    }

    [Fact]
    public async Task Dispose_WhenNativeCleanupBlocked_ReturnsAndRejectsNewWork()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var finished = new ManualResetEventSlim(false);
        var disposeCalls = 0;
        var applyCalls = 0;
        var service = new NvidiaColorControlService(new NullLoggerService(), (_, _) =>
        {
            Interlocked.Increment(ref applyCalls);
            return ColorApplyOutcome.Applied;
        }, () =>
        {
            Interlocked.Increment(ref disposeCalls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            finished.Set();
        });
        var dispose = Task.Run(service.Dispose);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            await dispose.WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.False(finished.IsSet);
            await Task.Run(service.Dispose).WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.Equal(ColorApplyOutcome.Skipped,
                await Task.Run(() => service.ApplyDigitalVibrance(CreateDisplay(), CreateProfile()))
                    .WaitAsync(TimeSpan.FromMilliseconds(500)));
            await Task.Run(() => RefreshTopology(service)).WaitAsync(TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            release.Set();
            await dispose.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(finished.Wait(TimeSpan.FromSeconds(2)));
            service.Dispose();
        }
        Assert.Equal(1, Volatile.Read(ref disposeCalls));
        Assert.Equal(0, Volatile.Read(ref applyCalls));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dispose_WhenCleanupCompletesOrThrows_IsAtMostOnce(bool throwCleanup)
    {
        using var cleanup = new ManualResetEventSlim(false);
        var disposeCalls = 0;
        var service = new NvidiaColorControlService(new NullLoggerService(), (_, _) => ColorApplyOutcome.Applied, () =>
        {
            Interlocked.Increment(ref disposeCalls);
            cleanup.Set();
            if (throwCleanup)
            {
                throw new InvalidOperationException("Injected cleanup failure.");
            }
        });

        var firstError = Record.Exception(service.Dispose);
        var secondError = Record.Exception(service.Dispose);

        Assert.Null(firstError);
        Assert.Null(secondError);
        Assert.True(cleanup.Wait(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, Volatile.Read(ref disposeCalls));
        Assert.Equal(ColorApplyOutcome.Skipped, service.ApplyDigitalVibrance(CreateDisplay(), CreateProfile()));
    }

    private static DisplayInfo CreateDisplay() => new()
    {
        Id = "DISPLAY1", Name = "Monitor", DeviceName = @"\\.\DISPLAY1", GpuVendor = GpuVendor.Nvidia
    };

    private static DisplayColorProfile CreateProfile() => new()
    {
        DisplayId = "DISPLAY1", IsEnabled = true, DigitalVibrance = 80
    };

    private static void RefreshTopology(NvidiaColorControlService service)
    {
        var handler = typeof(NvidiaColorControlService).GetMethod("OnDisplaySettingsChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(handler);
        handler.Invoke(service, [null, EventArgs.Empty]);
    }

}
