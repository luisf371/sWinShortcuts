using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class ProfileActivationReliabilityTests
{
    [Fact]
    public async Task LiveEdit_DelayedDeactivationNotification_KeepsNewActiveCrosshair()
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var first = ProfileFactory.CreateCustomProfile("First", "first.exe");
        var second = ProfileFactory.CreateCustomProfile("Second", "second.exe");
        second.Crosshair.IsEnabled = true;
        var store = new InMemoryProfileStore();
        store.Profiles.AddRange([first, second]);
        var watcher = new FakeForegroundWatcher();
        var tray = new FakeSystemTrayService();
        var crosshair = new FakeCrosshairService();
        using var input = InputHookServiceTestExtensions.CreateWithFakeForeground(
            new NullLoggerService(), new RecordingInputSender());
        input.StartInputExecutorForTesting();
        var delayDeactivation = false;
        input.ActiveProfileChanged += (_, active) =>
        {
            if (active is null && Volatile.Read(ref delayDeactivation))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            }
        };
        var service = new ProfileActivationService(new ProfileManager(store), watcher, input, tray,
            new RecordingColorControlService(), new FakeDisplayService(), crosshair, new NullLoggerService());
        await service.StartAsync(CancellationToken.None);
        Task? reconcile = null;
        try
        {
            watcher.RaiseForegroundChanged("first.exe", 101);
            await WaitForAsync(() => crosshair.Applications.Any(apply => ReferenceEquals(apply.Profile, first)));
            Volatile.Write(ref delayDeactivation, true);
            first.IsEnabled = false;
            reconcile = Task.Factory.StartNew(() => input.ReconcileProfileSettings(first, ProfileChangeKind.Master),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));

            watcher.RaiseForegroundChanged("second.exe", 202);
            await WaitForAsync(() => crosshair.Applications.Any(apply => ReferenceEquals(apply.Profile, second)));
            release.Set();
            await reconcile.WaitAsync(TimeSpan.FromSeconds(2));
            service.NotifyProfileChanged(second, ProfileChangeKind.Crosshair);

            Assert.Same(second, crosshair.Applications.Last().Profile);
            Assert.Equal((true, second.Name), tray.StatusUpdates.Last());
        }
        finally
        {
            release.Set();
            if (reconcile is not null) await reconcile.WaitAsync(TimeSpan.FromSeconds(2));
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ForegroundChanges_ColorApplyBlocked_InputActivatesEveryGenerationInOrder()
    {
        var store = new InMemoryProfileStore();
        var profileA = CreateColorProfile("Game A", "game-a.exe", 55);
        var profileB = CreateColorProfile("Game B", "game-b.exe", 65);
        store.Profiles.AddRange([profileA, profileB]);

        var manager = new ProfileManager(store);
        var watcher = new FakeForegroundWatcher();
        var input = new FakeInputHookService();
        using var color = new BlockingColorControlService();
        var service = new ProfileActivationService(
            manager,
            watcher,
            input,
            new FakeSystemTrayService(),
            color,
            new FakeDisplayService { Displays = [CreateDisplay("DISPLAY1")] },
            new FakeCrosshairService(),
            new NullLoggerService());

        await service.StartAsync(CancellationToken.None);
        try
        {
            var activationBaseline = input.Activations.Count;
            watcher.RaiseForegroundChanged("game-a.exe", 101);
            Assert.True(color.ApplyEntered.Wait(TimeSpan.FromSeconds(2)));
            await WaitForAsync(() => input.Activations.Any(x => ReferenceEquals(x.Profile, profileA)));

            // Color remains blocked on A. Input activation must still observe B and the final A instead
            // of being delayed or coalesced by the color lane.
            watcher.RaiseForegroundChanged("game-b.exe", 202);
            watcher.RaiseForegroundChanged("game-a.exe", 101);

            await WaitForAsync(() => input.Activations.Count >= activationBaseline + 3);
            var activations = input.Activations.ToArray()[activationBaseline..];

            Assert.Collection(
                activations,
                item => Assert.Same(profileA, item.Profile),
                item => Assert.Same(profileB, item.Profile),
                item => Assert.Same(profileA, item.Profile));
            Assert.True(activations[0].Generation < activations[1].Generation);
            Assert.True(activations[1].Generation < activations[2].Generation);
        }
        finally
        {
            color.ReleaseApply.Set();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ConcurrentForegroundAndProfileRepublish_QueuesMonotonicGenerations()
    {
        var store = new InMemoryProfileStore();
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        store.Profiles.Add(profile);

        var manager = new ProfileManager(store);
        var watcher = new FakeForegroundWatcher();
        var input = new FakeInputHookService();
        var service = new ProfileActivationService(
            manager,
            watcher,
            input,
            new FakeSystemTrayService(),
            new RecordingColorControlService(),
            new FakeDisplayService(),
            new FakeCrosshairService(),
            new NullLoggerService());

        await service.StartAsync(CancellationToken.None);
        using var firstPublicationEntered = new ManualResetEventSlim(false);
        using var releaseFirstPublication = new ManualResetEventSlim(false);
        var blockOnce = 0;
        input.ForegroundIdentitySet = _ =>
        {
            if (Interlocked.CompareExchange(ref blockOnce, 1, 0) == 0)
            {
                firstPublicationEntered.Set();
                Assert.True(releaseFirstPublication.Wait(TimeSpan.FromSeconds(2)));
            }
        };

        try
        {
            var activationBaseline = input.Activations.Count;
            var foregroundPublish = Task.Run(
                () => watcher.RaiseForegroundChanged("game.exe", 123));
            Assert.True(firstPublicationEntered.Wait(TimeSpan.FromSeconds(2)));

            var profileRepublish = Task.Run(
                () => service.NotifyProfileChanged(profile, ProfileChangeKind.Identity));

            releaseFirstPublication.Set();
            await Task.WhenAll(foregroundPublish, profileRepublish);
            await WaitForAsync(() => input.Activations.Count >= activationBaseline + 2);

            var activations = input.Activations.ToArray()[activationBaseline..];
            Assert.Collection(
                activations,
                first => Assert.Same(profile, first.Profile),
                second => Assert.Same(profile, second.Profile));
            Assert.True(activations[0].Generation < activations[1].Generation);
            Assert.Equal(
                input.LastForegroundIdentity?.Generation,
                activations[1].Generation);
        }
        finally
        {
            input.ForegroundIdentitySet = null;
            releaseFirstPublication.Set();
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopCanceledWhileColorApplyBlocked_RestartWaitsForRetiredWorker()
    {
        var store = new InMemoryProfileStore();
        var profile = CreateColorProfile("Game", "game.exe", 55);
        store.Profiles.Add(profile);

        var manager = new ProfileManager(store);
        var watcher = new FakeForegroundWatcher();
        var input = new FakeInputHookService();
        using var color = new BlockingColorControlService();
        var service = new ProfileActivationService(
            manager,
            watcher,
            input,
            new FakeSystemTrayService(),
            color,
            new FakeDisplayService { Displays = [CreateDisplay("DISPLAY1")] },
            new FakeCrosshairService(),
            new NullLoggerService());

        var restarted = false;
        await service.StartAsync(CancellationToken.None);
        try
        {
            watcher.RaiseForegroundChanged("game.exe", 123);
            Assert.True(color.ApplyEntered.Wait(TimeSpan.FromSeconds(2)));

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await service.StopAsync(canceled.Token);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.StartAsync(CancellationToken.None));

            color.ReleaseApply.Set();
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!restarted && DateTime.UtcNow < deadline)
            {
                try
                {
                    await service.StartAsync(CancellationToken.None);
                    restarted = true;
                }
                catch (InvalidOperationException)
                {
                    await Task.Delay(10);
                }
            }

            Assert.True(restarted);
        }
        finally
        {
            color.ReleaseApply.Set();
            if (restarted)
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task ForegroundChanges_LogsInputDecisionsFromSnapshots()
    {
        var store = new InMemoryProfileStore();
        var enabledProfile = ProfileFactory.CreateCustomProfile("Enabled Game", "enabled.exe");
        var disabledProfile = ProfileFactory.CreateCustomProfile("Disabled Game", "disabled.exe");
        disabledProfile.IsEnabled = false;
        store.Profiles.AddRange([enabledProfile, disabledProfile]);

        var manager = new ProfileManager(store);
        var watcher = new FakeForegroundWatcher();
        var input = new FakeInputHookService();
        var logger = new NullLoggerService { IsEnabled = true };
        var service = new ProfileActivationService(
            manager,
            watcher,
            input,
            new FakeSystemTrayService(),
            new RecordingColorControlService(),
            new FakeDisplayService(),
            new FakeCrosshairService(),
            logger);

        await service.StartAsync(CancellationToken.None);
        try
        {
            watcher.RaiseForegroundChanged("other.exe", 321);
            var noMatchGeneration = input.LastForegroundIdentity!.Value.Generation;
            await WaitForAsync(() => input.DeactivationGenerations.Contains(noMatchGeneration));

            Assert.Contains(
                logger.Messages,
                message => message.Contains("Foreground decision=no-match") &&
                           message.Contains("generation=") &&
                           message.Contains("hwnd=0x0") &&
                           message.Contains("pid=321") &&
                           message.Contains("process=other.exe") &&
                           message.Contains("normalized=other") &&
                           message.Contains("profile=<none>"));

            watcher.RaiseForegroundChanged("enabled.exe", 322);
            var enabledGeneration = input.LastForegroundIdentity!.Value.Generation;
            await WaitForAsync(
                () => input.Activations.Any(
                    x => x.Generation == enabledGeneration && ReferenceEquals(x.Profile, enabledProfile)));

            Assert.Contains(
                logger.Messages,
                message => message.Contains("Foreground decision=activate") &&
                           message.Contains("generation=") &&
                           message.Contains("hwnd=0x0") &&
                           message.Contains("pid=322") &&
                           message.Contains("process=enabled.exe") &&
                           message.Contains("normalized=enabled") &&
                           message.Contains("profile=Enabled Game"));

            watcher.RaiseForegroundChanged("disabled.exe", 323);
            var disabledGeneration = input.LastForegroundIdentity!.Value.Generation;
            await WaitForAsync(() => input.DeactivationGenerations.Contains(disabledGeneration));

            Assert.Contains(
                logger.Messages,
                message => message.Contains("Foreground decision=profile-disabled") &&
                           message.Contains("generation=") &&
                           message.Contains("hwnd=0x0") &&
                           message.Contains("pid=323") &&
                           message.Contains("process=disabled.exe") &&
                           message.Contains("normalized=disabled") &&
                           message.Contains("profile=Disabled Game"));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NotifyProfileChanged_MasterDisableAndEnable_ReconcilesWithoutFocusChange()
    {
        var store = new InMemoryProfileStore();
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        store.Profiles.Add(profile);

        var manager = new ProfileManager(store);
        var watcher = new FakeForegroundWatcher();
        var input = new FakeInputHookService();
        var service = new ProfileActivationService(
            manager,
            watcher,
            input,
            new FakeSystemTrayService(),
            new RecordingColorControlService(),
            new FakeDisplayService(),
            new FakeCrosshairService(),
            new NullLoggerService());

        await service.StartAsync(CancellationToken.None);
        try
        {
            watcher.RaiseForegroundChanged("game.exe", 123);
            await WaitForAsync(() => input.Activations.Any(x => ReferenceEquals(x.Profile, profile)));

            var deactivationsBeforeDisable = input.DeactivateCount;
            profile.IsEnabled = false;
            service.NotifyProfileChanged(profile, ProfileChangeKind.Master);

            await WaitForAsync(() => input.DeactivateCount > deactivationsBeforeDisable);
            Assert.Contains(
                input.ReconciledChanges,
                change => ReferenceEquals(change.Profile, profile) &&
                          change.Kind == ProfileChangeKind.Master);

            var activationsBeforeEnable = input.Activations.Count;
            profile.IsEnabled = true;
            service.NotifyProfileChanged(profile, ProfileChangeKind.Master);

            await WaitForAsync(() => input.Activations.Count > activationsBeforeEnable);
            Assert.Same(profile, input.Activations.Last().Profile);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ForegroundSameProfileDifferentProcess_ReleasesForegroundAutoRun()
    {
        var store = new InMemoryProfileStore();
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        store.Profiles.Add(profile);

        var manager = new ProfileManager(store);
        var watcher = new FakeForegroundWatcher();
        var input = new FakeInputHookService();
        var service = new ProfileActivationService(
            manager,
            watcher,
            input,
            new FakeSystemTrayService(),
            new RecordingColorControlService(),
            new FakeDisplayService(),
            new FakeCrosshairService(),
            new NullLoggerService());

        await service.StartAsync(CancellationToken.None);
        try
        {
            watcher.RaiseForegroundChanged("game.exe", 123);
            await WaitForAsync(() => input.Activations.Any(x => ReferenceEquals(x.Profile, profile)));

            var releasesBeforeProcessChange = input.ReleaseForegroundAutoRunCount;
            watcher.RaiseForegroundChanged("game.exe", 456);

            await WaitForAsync(() => input.ReleaseForegroundAutoRunCount > releasesBeforeProcessChange);

            var releasesAfterProcessChange = input.ReleaseForegroundAutoRunCount;
            watcher.RaiseForegroundChanged("game.exe", 456);

            Assert.Equal(releasesAfterProcessChange, input.ReleaseForegroundAutoRunCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ForegroundIntermediateTransition_ReleasesStateAndReactivatesOnlyLiveProfile()
    {
        var store = new InMemoryProfileStore();
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        store.Profiles.Add(profile);

        var manager = new ProfileManager(store);
        var watcher = new FakeForegroundWatcher();
        var input = new FakeInputHookService();
        var service = new ProfileActivationService(
            manager,
            watcher,
            input,
            new FakeSystemTrayService(),
            new RecordingColorControlService(),
            new FakeDisplayService(),
            new FakeCrosshairService(),
            new NullLoggerService());

        await service.StartAsync(CancellationToken.None);
        try
        {
            watcher.RaiseForegroundChanged("game.exe", 123);
            await WaitForAsync(() => input.Activations.Any(x => ReferenceEquals(x.Profile, profile)));

            var activationBaseline = input.Activations.Count;
            var releasesBeforeTransition = input.ReleaseForegroundStateCount;

            watcher.RaiseForegroundChanged(
                "game.exe",
                123,
                requiresInputReset: true);

            await WaitForAsync(() =>
                input.ReleaseForegroundStateCount > releasesBeforeTransition &&
                input.Activations.Count > activationBaseline);

            var activation = input.Activations.Last();
            Assert.Equal(releasesBeforeTransition + 1, input.ReleaseForegroundStateCount);
            Assert.Same(profile, activation.Profile);
            Assert.Equal(input.LastForegroundIdentity?.Generation, activation.Generation);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ActiveProfileRemoved_ReleasesAndDeactivatesWithoutAnotherFocusEvent()
    {
        var store = new InMemoryProfileStore();
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        store.Profiles.Add(profile);

        var manager = new ProfileManager(store);
        var watcher = new FakeForegroundWatcher();
        var input = new FakeInputHookService();
        var service = new ProfileActivationService(
            manager,
            watcher,
            input,
            new FakeSystemTrayService(),
            new RecordingColorControlService(),
            new FakeDisplayService(),
            new FakeCrosshairService(),
            new NullLoggerService());

        await service.StartAsync(CancellationToken.None);
        try
        {
            watcher.RaiseForegroundChanged("game.exe", 123);
            await WaitForAsync(() => input.Activations.Any(x => ReferenceEquals(x.Profile, profile)));

            var deactivationsBeforeRemove = input.DeactivateCount;
            await manager.RemoveProfileAsync(profile);

            await WaitForAsync(() => input.DeactivateCount > deactivationsBeforeRemove);
            Assert.Contains(
                input.ReconciledChanges,
                change => ReferenceEquals(change.Profile, profile) &&
                          change.Kind == ProfileChangeKind.Removed);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("active", 80)]
    [InlineData("inactive", 30)]
    [InlineData("game", 55)]
    [InlineData("forced", 85)]
    public async Task DefaultDisplayEdit_UsesCurrentActivationPlan(string context, int expectedBrightness)
    {
        var store = new InMemoryProfileStore();
        var game = CreateColorProfile("Game", "game.exe", 55);
        game.ColorSettings.HasSecondary = true;
        game.ColorSettings.SetProfile(new DisplayColorProfile
        {
            DisplayId = "DISPLAY1", IsEnabled = true, Brightness = 85
        }, ColorVariant.Secondary);
        store.Profiles.Add(game);
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();
        var global = manager.WindowsProfile;
        global.ColorSettings.IsEnabled = true;
        global.ColorSettings.HasSecondary = true;
        global.ColorSettings.SetProfile(new DisplayColorProfile
        {
            DisplayId = "DISPLAY1", IsEnabled = true, Brightness = 30
        });
        global.ColorSettings.EnsureSecondaryInitialized();
        var watcher = new FakeForegroundWatcher();
        var color = new QueuedColorControlService();
        var displays = new FakeDisplayService { Displays = [CreateDisplay("DISPLAY1")] };
        var service = new ProfileActivationService(manager, watcher, new FakeInputHookService(),
            new FakeSystemTrayService(), color, displays, new FakeCrosshairService(), new NullLoggerService());
        using var editor = new sWinShortcuts.ViewModels.ProfileViewModel(global, displays, color,
            profileRuntimeService: service);
        editor.ProfileChanged += (_, change) => service.NotifyProfileChanged(global, change.Kind);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => color.Brightnesses.Contains(30));
            if (context == "game")
            {
                watcher.RaiseForegroundChanged("game.exe", 123);
                await WaitForAsync(() => color.Brightnesses.Contains(55));
            }
            else if (context == "forced")
            {
                service.SetForcedColorPreview(game.ColorSettings, ColorVariant.Secondary);
                await WaitForAsync(() => color.Brightnesses.Contains(85));
            }
            else if (context == "inactive")
            {
                editor.ColorSettings.IsEditingSecondary = true;
            }

            var before = color.Brightnesses.Count;
            editor.ColorSettings.DisplayViewModels.Single().Brightness = 80;
            await WaitForAsync(() => color.Brightnesses.Count > before);

            Assert.All(color.Brightnesses.ToArray().Skip(before), value => Assert.Equal(expectedBrightness, value));
            Assert.Equal(ColorVariant.Primary, global.ColorSettings.ActiveVariant);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class QueuedColorControlService : IColorControlService
    {
        public System.Collections.Concurrent.ConcurrentQueue<int> Brightnesses { get; } = new();

        public ColorApplyOutcome Apply(DisplayInfo display, DisplayColorProfile profile)
        {
            Brightnesses.Enqueue(profile.Brightness);
            return ColorApplyOutcome.Applied;
        }
    }

    private static Profile CreateColorProfile(string name, string executable, int brightness)
    {
        var profile = ProfileFactory.CreateCustomProfile(name, executable);
        profile.ColorSettings.IsEnabled = true;
        var display = profile.ColorSettings.GetOrCreateProfile("DISPLAY1");
        display.IsEnabled = true;
        display.Brightness = brightness;
        return profile;
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

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
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

    private sealed class BlockingColorControlService : IColorControlService, IDisposable
    {
        public ManualResetEventSlim ApplyEntered { get; } = new(false);

        public ManualResetEventSlim ReleaseApply { get; } = new(false);

        public ColorApplyOutcome Apply(DisplayInfo display, DisplayColorProfile profile)
        {
            ApplyEntered.Set();
            if (!ReleaseApply.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("Test did not release the blocked color apply.");
            }

            return ColorApplyOutcome.Applied;
        }

        public void Dispose()
        {
            ApplyEntered.Dispose();
            ReleaseApply.Dispose();
        }
    }
}
