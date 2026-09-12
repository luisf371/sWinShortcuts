using System.Collections.Concurrent;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class CrosshairPublicationTests
{
    [Theory]
    [InlineData("desktop.exe", false)]
    [InlineData("other.exe", false)]
    [InlineData("other.exe", true)]
    public async Task ForegroundRoundTrip_RestoresOffsetMode(string awayExecutable, bool otherCrosshair)
    {
        var game = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        game.Crosshair.IsEnabled = true;
        game.Crosshair.OffsetX = 37;
        game.Crosshair.OffsetY = -19;
        var other = ProfileFactory.CreateCustomProfile("Other", "other.exe");
        other.Crosshair.IsEnabled = otherCrosshair;
        var store = new InMemoryProfileStore();
        store.Profiles.AddRange([game, other]);
        var input = new FakeInputHookService();
        var watcher = new FakeForegroundWatcher();
        var pending = new ConcurrentQueue<Action>();
        using var crosshair = new CrosshairService(new NullLoggerService(), input, pending.Enqueue);
        var observed = new ObservedCrosshairService(crosshair);
        var service = CreateService(store, input, watcher, observed);
        await service.StartAsync(CancellationToken.None);
        try
        {
            watcher.RaiseForegroundChanged("game.exe", 101);
            await WaitForAsync(() => observed.Applications.Contains(game));
            var firstGeneration = input.LastForegroundIdentity!.Value.Generation;
            input.RaiseCrosshairOffsetToggle(game, firstGeneration);
            while (pending.TryDequeue(out var apply)) apply();
            Assert.Equal((37, -19), crosshair.AppliedOffset);

            observed.Applications.Clear();
            watcher.RaiseForegroundChanged(awayExecutable, 202);
            await WaitForAsync(() => !observed.Applications.IsEmpty);
            while (pending.TryDequeue(out var apply)) apply();
            Assert.Equal((0, 0), crosshair.AppliedOffset);
            Assert.Equal(otherCrosshair, crosshair.AppliedVisibility);

            observed.Applications.Clear();
            watcher.RaiseForegroundChanged("game.exe", 101);
            await WaitForAsync(() => observed.Applications.Contains(game));
            while (pending.TryDequeue(out var apply)) apply();
            Assert.True(crosshair.AppliedVisibility);
            Assert.Equal((37, -19), crosshair.AppliedOffset);

            input.RaiseCrosshairOffsetToggle(game, firstGeneration);
            Assert.Empty(pending); // A late press from before the focus change is still rejected.
            input.RaiseCrosshairOffsetToggle(game, input.LastForegroundIdentity!.Value.Generation);
            while (pending.TryDequeue(out var apply)) apply();
            Assert.Equal((0, 0), crosshair.AppliedOffset);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LiveEdit_BlockedBeforeApply_CannotOverwriteLaterProfile()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var secondApplied = new ManualResetEventSlim();
        var first = ProfileFactory.CreateCustomProfile("First", "first.exe");
        first.Crosshair.IsEnabled = true;
        var second = ProfileFactory.CreateCustomProfile("Second", "second.exe");
        var store = new InMemoryProfileStore();
        store.Profiles.AddRange([first, second]);
        var input = new FakeInputHookService();
        var watcher = new FakeForegroundWatcher();
        var pending = new ConcurrentQueue<Action>();
        using var crosshair = new CrosshairService(new NullLoggerService(), input, pending.Enqueue);
        var wrapper = new ObservedCrosshairService(crosshair);
        var service = CreateService(store, input, watcher, wrapper);
        await service.StartAsync(CancellationToken.None);
        Task? edit = null;
        Task? switchProfile = null;
        try
        {
            watcher.RaiseForegroundChanged("first.exe", 101);
            await WaitForAsync(() => wrapper.Applications.Contains(first));
            wrapper.BeforeApply = profile =>
            {
                if (!ReferenceEquals(profile, first)) return;
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            };
            wrapper.AfterApply = profile =>
            {
                if (ReferenceEquals(profile, second)) secondApplied.Set();
            };
            edit = Task.Factory.StartNew(() => service.NotifyProfileChanged(first, ProfileChangeKind.Crosshair),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
            switchProfile = Task.Factory.StartNew(() => watcher.RaiseForegroundChanged("second.exe", 202),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            // Give a competing publication the chance to overtake the paused edit. Correct ordering
            // holds it until the edit returns; the original race lets it complete during this wait.
            var overtookEdit = secondApplied.Wait(TimeSpan.FromMilliseconds(500));
            release.Set();
            await Task.WhenAll(edit, switchProfile).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(secondApplied.Wait(TimeSpan.FromSeconds(2)));
            while (pending.TryDequeue(out var apply)) apply();

            Assert.Same(second, input.ActiveProfile);
            Assert.False(crosshair.AppliedVisibility);
            Assert.False(overtookEdit);
        }
        finally
        {
            release.Set();
            if (edit is not null) await edit.WaitAsync(TimeSpan.FromSeconds(2));
            if (switchProfile is not null) await switchProfile.WaitAsync(TimeSpan.FromSeconds(2));
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ForegroundChanges_WhileActivationBlocked_SkipsObsoleteOverlay()
    {
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var first = ProfileFactory.CreateCustomProfile("First", "first.exe");
        first.Crosshair.IsEnabled = true;
        var second = ProfileFactory.CreateCustomProfile("Second", "second.exe");
        var store = new InMemoryProfileStore();
        store.Profiles.AddRange([first, second]);
        var input = new FakeInputHookService();
        var watcher = new FakeForegroundWatcher();
        var pending = new ConcurrentQueue<Action>();
        using var crosshair = new CrosshairService(new NullLoggerService(), input, pending.Enqueue);
        input.ActiveProfileChanged += (_, profile) =>
        {
            if (ReferenceEquals(profile, first))
            {
                firstEntered.Set();
                Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
            }
            if (ReferenceEquals(profile, second))
            {
                secondEntered.Set();
                Assert.True(releaseSecond.Wait(TimeSpan.FromSeconds(5)));
            }
        };
        var service = CreateService(store, input, watcher, crosshair);
        await service.StartAsync(CancellationToken.None);
        try
        {
            watcher.RaiseForegroundChanged("first.exe", 101);
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(2)));
            watcher.RaiseForegroundChanged("second.exe", 202);
            releaseFirst.Set();
            Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(2)));
            while (pending.TryDequeue(out var apply)) apply();
            Assert.False(crosshair.AppliedVisibility);
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static ProfileActivationService CreateService(InMemoryProfileStore store,
        FakeInputHookService input, FakeForegroundWatcher watcher, ICrosshairService crosshair) =>
        new(new ProfileManager(store), watcher, input, new FakeSystemTrayService(),
            new RecordingColorControlService(), new FakeDisplayService(), crosshair, new NullLoggerService());

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class ObservedCrosshairService(CrosshairService inner) : ICrosshairService
    {
        public ConcurrentQueue<Profile?> Applications { get; } = new();
        public Action<Profile?>? BeforeApply { get; set; }
        public Action<Profile?>? AfterApply { get; set; }
        public void Start() => inner.Start();
        public void Stop() => inner.Stop();
        public void SetRightButtonHeld(bool isDown) => inner.SetRightButtonHeld(isDown);
        public void ApplyProfile(Profile? profile, IntPtr foregroundHwnd, long foregroundGeneration = 0)
        {
            BeforeApply?.Invoke(profile);
            inner.ApplyProfile(profile, foregroundHwnd, foregroundGeneration);
            Applications.Enqueue(profile);
            AfterApply?.Invoke(profile);
        }
    }
}
