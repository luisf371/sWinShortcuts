using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;
using static Tests.ProfileActivationReliabilityTests;

namespace Tests;

public sealed class StickyColorActivationTests
{
    private const int WINDOWS = 30;
    private const int WINDOWS_SECONDARY = 35;
    private const int A = 55;
    private const int A_SECONDARY = 65;
    private const int B = 75;
    private const string GAME_EXECUTABLE = "game.exe";

    [Fact]
    public async Task AltTab_KeepColorActiveOff_UsesWindows()
    {
        var run = new ActivationRun();
        run.Game.ColorSettings.KeepColorActive = false;
        try
        {
            await run.StartAsync();
            await run.ExpectColorAsync(A, () => run.FocusGame());
            await run.ExpectColorAsync(WINDOWS, () => run.FocusDesktop());
            Assert.Equal(0, run.Watch.Attempts);
        }
        finally
        {
            await run.StopAsync();
        }
    }

    [Fact]
    public async Task AltTab_KeepColorActiveOn_RetainsColorOnly()
    {
        var run = new ActivationRun(secondDisplay: true);
        run.Other.IsEnabled = false;
        try
        {
            await run.StartAsync();
            await run.ExpectColorAsync(A, () => run.FocusGame());
            Assert.Equal(1, run.Watch.Registrations);
            Assert.Equal(1, run.Watch.Attempts);
            await WaitForAsync(() => run.Color.DisplayBrightnesses.Count >= 4);
            await run.WaitForOverlayAsync(run.Game);
            var gameGeneration = run.Input.LastForegroundIdentity!.Value.Generation;
            Assert.Equal((run.Game, gameGeneration), Assert.Single(run.Input.Activations));

            foreach (var focus in new Action[] { () => run.FocusDesktop(), () => run.FocusOther() })
            {
                focus();
                await run.WaitForOverlayAsync(null);
                Assert.Contains(run.Input.LastForegroundIdentity!.Value.Generation, run.Input.DeactivationGenerations);

                var before = run.Color.DisplayBrightnesses.Count;
                await run.ReapplyAsync(A);
                await WaitForAsync(() => run.Color.DisplayBrightnesses.Count >= before + 2);
                var applied = run.Color.DisplayBrightnesses.ToArray().Skip(before).ToArray();
                Assert.Contains(("DISPLAY1", A), applied);
                Assert.Contains(("DISPLAY2", WINDOWS + 1), applied);
            }

            await run.ReapplyAsync(A);
            await run.ExpectColorAsync(A_SECONDARY, run.Input.RaiseColorVariantToggle);
            Assert.Equal(ColorVariant.Secondary, run.Game.ColorSettings.ActiveVariant);
            Assert.Equal(ColorVariant.Primary, run.Windows.ColorSettings.ActiveVariant);
            Assert.Equal(1, run.Watch.Registrations);
            Assert.Equal(0, run.Watch.Releases);
        }
        finally
        {
            await run.StopAsync();
        }

        Assert.Equal(
            [(false, null), (true, run.Game.Name), (false, null), (false, null)],
            run.Tray.StatusUpdates);
    }

    [Fact]
    public async Task ForegroundHandoff_CoalescedProfileBThenDesktop_ReleasesOwnerA()
    {
        using var block = new BlockingColorControlService();
        var run = new ActivationRun(block: block);
        run.Other.ColorSettings.IsEnabled = false;
        try
        {
            await run.StartAsync();
            await run.ExpectColorAsync(A, () => run.FocusGame());
            SetBrightness(run.Game, "DISPLAY1", A_SECONDARY);
            run.NotifyColorEdit();
            await WaitForAsync(() => block.ApplyEntered.IsSet);

            run.FocusOther();
            run.FocusDesktop();
            await run.ExpectColorAsync(WINDOWS, block.ReleaseApply.Set);
            Assert.Equal(1, run.Watch.Registrations);
            Assert.Equal(1, run.Watch.Releases);

            await run.ExpectColorAsync(A_SECONDARY, () => run.FocusGame());
            run.Other.ColorSettings.IsEnabled = true;
            await run.ExpectColorAsync(B, () => run.FocusOther());
            Assert.Equal(3, run.Watch.Registrations);
            Assert.Equal(2, run.Watch.Releases);
            Assert.Equal(202u, run.Watch.Watches[2].ProcessId);

            run.FocusDesktop();
            await run.ReapplyAsync(B);
        }
        finally
        {
            block.ReleaseApply.Set();
            await run.StopAsync();
        }
    }

    [Fact]
    public async Task OwnerExit_WithoutForegroundChange_RestoresWindows()
    {
        var run = new ActivationRun();
        try
        {
            await run.StartAsync();
            await run.ExpectColorAsync(A, () => run.FocusGame());
            run.FocusDesktop();
            await run.WaitForOverlayAsync(null);
            await run.ReapplyAsync(A);
            var foreground = run.Input.LastForegroundIdentity;
            var deactivations = run.Input.DeactivationGenerations.Count;

            await run.ExpectColorAsync(WINDOWS, run.Watch.Watches[0].FireExit);
            Assert.Equal(1, run.Watch.Releases);
            Assert.Equal(foreground, run.Input.LastForegroundIdentity);
            Assert.Equal(deactivations, run.Input.DeactivationGenerations.Count);

            await run.ExpectColorAsync(A, () => run.FocusGame(pid: 303));
            // The real watch rejects the exited process's destroyed window.
            run.Watch.Deny = true;
            run.Watch.Watches[1].FireExit();
            await WaitForAsync(() => run.Watch.Releases == 2 && run.Watch.Attempts == 3);
            await run.ReapplyAsync(A);
            Assert.Equal(2, run.Watch.Registrations);
            await run.ExpectColorAsync(WINDOWS, () => run.FocusDesktop());
        }
        finally
        {
            await run.StopAsync();
        }
    }

    [Fact]
    public async Task OwnerExit_DuringWatchRegistration_DoesNotRetain()
    {
        var watch = new FakeProcessExitWatch { RunExitBeforeReturn = true };
        var run = new ActivationRun(watch: watch, tryWatch: (hwnd, pid, onExit) =>
            watch.TryWatch(hwnd, pid, () =>
            {
                // The real watch rejects retries against the exited process's destroyed window.
                watch.Deny = true;
                onExit();
            }));
        try
        {
            await run.StartAsync();
            await run.ExpectColorAsync(A, () => run.FocusGame());
            Assert.Equal(1, watch.Registrations);
            Assert.Equal(1, watch.Releases);
            await run.ReapplyAsync(A);
            Assert.Equal(2, watch.Attempts);
            await run.ExpectColorAsync(WINDOWS, () => run.FocusDesktop());
            Assert.Equal(1, watch.Registrations);
        }
        finally
        {
            await run.StopAsync();
        }
    }

    [Fact]
    public async Task ForegroundChange_DuringWatchRegistration_DoesNotRetain()
    {
        using var registration = new ManualResetEventSlim();
        var run = new ActivationRun();
        run.Watch.BlockRegistration = registration;
        try
        {
            await run.StartAsync();
            run.FocusGame();
            await WaitForAsync(() => run.Watch.RegistrationEntered.IsSet);
            run.FocusDesktop();
            run.NotifyColorEdit();
            await run.ExpectColorAsync(WINDOWS, registration.Set);
            Assert.Equal(1, run.Watch.Registrations);
            Assert.Equal(1, run.Watch.Releases);
        }
        finally
        {
            registration.Set();
            await run.StopAsync();
        }
    }

    [Fact]
    public async Task WatchRegistration_Denied_KeepsForegroundOnlyColors()
    {
        var run = new ActivationRun();
        run.Watch.Deny = true;
        try
        {
            await run.StartAsync();
            await run.ExpectColorAsync(A, () => run.FocusGame());

            // A reapply of the same generation settles the initial hotkey target before the press.
            await run.ReapplyAsync(A);
            var before = run.Watch.Attempts;
            await run.ExpectColorAsync(A_SECONDARY, run.Input.RaiseColorVariantToggle);
            Assert.Equal(before + 1, run.Watch.Attempts);

            await run.ExpectColorAsync(WINDOWS, () => run.FocusDesktop());
            Assert.Equal(before + 1, run.Watch.Attempts);
            await run.ExpectColorAsync(A_SECONDARY, () => run.FocusGame());
            Assert.Equal(before + 2, run.Watch.Attempts);
            Assert.Equal(0, run.Watch.Registrations);
        }
        finally
        {
            await run.StopAsync();
        }
    }

    [Fact]
    public async Task Stop_DuringWatchRegistration_RejectsLateOwner()
    {
        using var registration = new ManualResetEventSlim();
        var run = new ActivationRun();
        run.Watch.BlockRegistration = registration;
        Task? stopping = null;
        try
        {
            await run.StartAsync();
            run.FocusGame();
            await WaitForAsync(() => run.Watch.RegistrationEntered.IsSet);
            stopping = run.StopAsync();
            await WaitForAsync(() => !run.Watcher.IsStarted);
            registration.Set();
            await stopping;
            stopping = null;
            var old = Assert.Single(run.Watch.Watches);
            Assert.Equal(1, run.Watch.Releases);
            Assert.True(old.IsDisposed);
            Assert.DoesNotContain(A, run.Color.Brightnesses);

            await run.StartAsync();
            old.FireExit();
            await run.ExpectColorAsync(A, () => run.FocusGame());
            run.FocusDesktop();
            await run.ReapplyAsync(A);
            Assert.Equal(2, run.Watch.Registrations);
            Assert.Equal(1, run.Watch.Releases);
        }
        finally
        {
            registration.Set();
            if (stopping is not null)
            {
                await stopping;
            }
            await run.StopAsync();
        }
    }

    [Fact]
    public async Task FailedStart_WithLiveRegistration_ReleasesWatch()
    {
        var run = new ActivationRun();
        run.Watcher.OnStart = () =>
        {
            run.FocusGame();
            Assert.True(SpinWait.SpinUntil(
                () => run.Watch.Registrations == 1 && run.Color.Brightnesses.Contains(A),
                TimeSpan.FromSeconds(10)));
            throw new InvalidOperationException();
        };

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(run.StartAsync);
            Assert.Equal(1, run.Watch.Releases);
            var old = Assert.Single(run.Watch.Watches);
            Assert.True(old.IsDisposed);
            run.Watcher.OnStart = null;
            try
            {
                await run.StartAsync();
            }
            catch (InvalidOperationException ex) when (
                ex.Message.StartsWith("The previous activation workers are still shutting down", StringComparison.Ordinal))
            {
                await run.StopAsync();
                await run.StartAsync();
            }

            old.FireExit();
            await run.ExpectColorAsync(A, () => run.FocusGame());
            run.FocusDesktop();
            await run.ReapplyAsync(A);
            Assert.Equal(2, run.Watch.Registrations);
            Assert.Equal(1, run.Watch.Releases);
        }
        finally
        {
            await run.StopAsync();
        }
    }

    [Fact]
    public async Task ForegroundProcessChange_OldExitCallback_DoesNotRestoreOverNewOwner()
    {
        var run = new ActivationRun();
        try
        {
            await run.StartAsync();
            await run.ExpectColorAsync(A, () => run.FocusGame());
            run.FocusGame(hwnd: 102);
            await run.ReapplyAsync(A);
            var first = Assert.Single(run.Watch.Watches);
            Assert.Equal(new IntPtr(101), first.WindowHandle);
            Assert.Equal(1, run.Watch.Attempts);

            run.FocusGame(hwnd: 103, pid: 303);
            await run.ReapplyAsync(A);
            Assert.Equal(2, run.Watch.Registrations);
            Assert.True(first.IsDisposed);
            var second = run.Watch.Watches[1];
            Assert.Equal(303u, second.ProcessId);

            first.FireExit();
            run.FocusDesktop();
            await run.ReapplyAsync(A);
            Assert.Equal(1, run.Watch.Releases);

            await run.StopAsync();
            await run.StartAsync();
            await run.ExpectColorAsync(A, () => run.FocusGame(hwnd: 104, pid: 303));
            Assert.True(second.IsDisposed);
            second.FireExit();
            run.FocusDesktop();
            await run.ReapplyAsync(A);
            Assert.Equal(3, run.Watch.Registrations);
            Assert.Equal(2, run.Watch.Releases);
        }
        finally
        {
            await run.StopAsync();
        }
    }

    [Theory]
    [InlineData("keep-active")]
    [InlineData("color")]
    [InlineData("master")]
    [InlineData("executable")]
    [InlineData("removed")]
    [InlineData("rename")]
    public async Task OwnerEdit_WhileUnfocused_ReleasesAndRequiresFocusToRearm(string edit)
    {
        var run = new ActivationRun();
        try
        {
            await run.StartAsync();
            await run.ExpectColorAsync(A, () => run.FocusGame());
            run.FocusDesktop();
            await run.ReapplyAsync(A);

            var profile = run.Game;
            if (edit == "rename")
            {
                profile.Name = "Renamed A";
                run.Service.NotifyProfileChanged(profile, ProfileChangeKind.Identity);
                await run.ReapplyAsync(A);
                Assert.Equal(1, run.Watch.Registrations);
                Assert.Equal(0, run.Watch.Releases);
                return;
            }

            if (edit == "removed")
            {
                var before = run.Color.Brightnesses.Count;
                await run.Manager.RemoveProfileAsync(profile);
                await WaitForAsync(() => run.Color.Brightnesses.ToArray().Skip(before).Contains(WINDOWS));
                profile = await run.Manager.AddProfileAsync(profile.Name, profile.Executable);
                SetBrightness(profile, "DISPLAY1", A);
            }

            var change = edit switch
            {
                "keep-active" or "color" => ProfileChangeKind.Color,
                "master" => ProfileChangeKind.Master,
                _ => ProfileChangeKind.Identity
            };

            if (edit != "removed")
            {
                await run.ExpectColorAsync(WINDOWS, () =>
                {
                    switch (edit)
                    {
                        case "keep-active": profile.ColorSettings.KeepColorActive = false; break;
                        case "color": profile.ColorSettings.IsEnabled = false; break;
                        case "master": profile.IsEnabled = false; break;
                        case "executable": profile.Executable = "changed.exe"; break;
                    }

                    run.Service.NotifyProfileChanged(profile, change);
                });
            }

            Assert.Equal(1, run.Watch.Releases);
            profile.ColorSettings.KeepColorActive = true;
            profile.ColorSettings.IsEnabled = true;
            profile.IsEnabled = true;
            profile.Executable = GAME_EXECUTABLE;
            run.Service.NotifyProfileChanged(profile, change);
            await run.ReapplyAsync(WINDOWS);
            Assert.Equal(1, run.Watch.Registrations);
            await run.ExpectColorAsync(A, () => run.FocusGame());
            Assert.Equal(2, run.Watch.Registrations);
        }
        finally
        {
            await run.StopAsync();
        }
    }

    [Fact]
    public async Task ForcePreview_Clear_ReevaluatesRetainedOwner()
    {
        var run = new ActivationRun();
        try
        {
            await run.StartAsync();
            await run.ExpectColorAsync(B, run.PreviewOther);
            await run.ExpectColorAsync(WINDOWS, run.Service.ClearForcedColorPreview);
            Assert.Equal(0, run.Watch.Attempts);

            await run.ExpectColorAsync(A, () => run.FocusGame());
            run.FocusDesktop();
            await run.ExpectColorAsync(B, run.PreviewOther);
            await run.ExpectColorAsync(A, run.Service.ClearForcedColorPreview);
            Assert.Equal(0, run.Watch.Releases);

            await run.ExpectColorAsync(B, run.PreviewOther);
            run.Watch.Watches[0].FireExit();
            await WaitForAsync(() => run.Watch.Releases == 1);
            await run.ReapplyAsync(B);
            await run.ExpectColorAsync(WINDOWS, run.Service.ClearForcedColorPreview);
            Assert.Equal(1, run.Watch.Registrations);
        }
        finally
        {
            await run.StopAsync();
        }
    }

    [Fact]
    public async Task OwnerExit_DuringColorWrite_NextPlanRestoresWindows()
    {
        using var block = new BlockingColorControlService();
        var run = new ActivationRun(block: block);
        try
        {
            await run.StartAsync();
            await run.ExpectColorAsync(A, () => run.FocusGame());
            run.FocusDesktop();
            await run.ReapplyAsync(A);
            await run.ReapplyAsync(A);
            run.Input.RaiseColorVariantToggle();
            await WaitForAsync(() => block.ApplyEntered.IsSet);
            Assert.Equal(ColorVariant.Secondary, run.Game.ColorSettings.ActiveVariant);

            var before = run.Color.Brightnesses.Count;
            run.Watch.Watches[0].FireExit();
            await run.ExpectColorAsync(WINDOWS, block.ReleaseApply.Set);
            Assert.Equal([A_SECONDARY, WINDOWS], run.Color.Brightnesses.ToArray().Skip(before));
            Assert.Equal(1, run.Watch.Releases);

            await run.ReapplyAsync(WINDOWS);
            await run.ExpectColorAsync(WINDOWS_SECONDARY, run.Input.RaiseColorVariantToggle);
            Assert.Equal(ColorVariant.Secondary, run.Windows.ColorSettings.ActiveVariant);
            Assert.Equal(ColorVariant.Secondary, run.Game.ColorSettings.ActiveVariant);
        }
        finally
        {
            block.ReleaseApply.Set();
            await run.StopAsync();
        }
    }

    private static void SetBrightness(Profile profile, string displayId, int brightness, ColorVariant variant = ColorVariant.Primary)
    {
        profile.ColorSettings.SetProfile(new DisplayColorProfile
        {
            DisplayId = displayId, IsEnabled = true, Brightness = brightness
        }, variant);
    }

    private sealed class ActivationRun : IColorControlService
    {
        private readonly BlockingColorControlService? _block;
        private readonly bool _secondDisplay;

        public Profile Game { get; } = CreateColorProfile("Game A", GAME_EXECUTABLE, A);
        public Profile Other { get; } = CreateColorProfile("Game B", "other.exe", B);
        public Profile Windows => Manager.WindowsProfile;
        public FakeProcessExitWatch Watch { get; }
        public FakeForegroundWatcher Watcher { get; } = new();
        public FakeInputHookService Input { get; } = new();
        public FakeCrosshairService Crosshair { get; } = new();
        public FakeSystemTrayService Tray { get; } = new();
        public QueuedColorControlService Color { get; } = new();
        public ProfileManager Manager { get; }
        public ProfileActivationService Service { get; }

        public ActivationRun(bool secondDisplay = false, BlockingColorControlService? block = null,
            FakeProcessExitWatch? watch = null, Func<IntPtr, uint, Action, IDisposable?>? tryWatch = null)
        {
            _secondDisplay = secondDisplay;
            _block = block;
            Watch = watch ?? new FakeProcessExitWatch();
            Game.ColorSettings.KeepColorActive = true;
            Game.ColorSettings.HasSecondary = true;
            SetBrightness(Game, "DISPLAY1", A_SECONDARY, ColorVariant.Secondary);
            Other.ColorSettings.KeepColorActive = true;
            var store = new InMemoryProfileStore();
            store.Profiles.AddRange([Game, Other]);
            Manager = new ProfileManager(store);
            var displays = new FakeDisplayService
            {
                Displays = secondDisplay ? [Display("DISPLAY1"), Display("DISPLAY2")] : [Display("DISPLAY1")]
            };
            Service = new ProfileActivationService(Manager, Watcher, Input, Tray, this, displays,
                Crosshair, new NullLoggerService(), tryWatchColorProcess: tryWatch ?? Watch.TryWatch);
        }

        public async Task StartAsync()
        {
            await Manager.InitializeAsync();
            Windows.ColorSettings.IsEnabled = true;
            Windows.ColorSettings.HasSecondary = true;
            SetBrightness(Windows, "DISPLAY1", WINDOWS);
            SetBrightness(Windows, "DISPLAY1", WINDOWS_SECONDARY, ColorVariant.Secondary);
            if (_secondDisplay)
            {
                SetBrightness(Windows, "DISPLAY2", WINDOWS + 1);
            }

            var before = Color.Brightnesses.Count;
            await Service.StartAsync(CancellationToken.None);
            await WaitForAsync(() => Color.Brightnesses.ToArray().Skip(before).Contains(WINDOWS));
            if (_secondDisplay)
            {
                await WaitForAsync(() => Color.DisplayBrightnesses.Count >= before + 2);
            }
        }

        public Task StopAsync() => Service.StopAsync(CancellationToken.None);

        public void FocusGame(int hwnd = 101, uint pid = 101) =>
            Watcher.RaiseForegroundChanged(GAME_EXECUTABLE, pid, windowHandle: new IntPtr(hwnd));

        public void FocusOther() =>
            Watcher.RaiseForegroundChanged(Other.Executable, 202, windowHandle: new IntPtr(202));

        public void FocusDesktop() =>
            Watcher.RaiseForegroundChanged("explorer.exe", 404, windowHandle: new IntPtr(404));

        public void NotifyColorEdit() => Service.NotifyProfileChanged(Game, ProfileChangeKind.Color);

        public void PreviewOther() => Service.SetForcedColorPreview(Other.ColorSettings, ColorVariant.Primary);

        public Task ReapplyAsync(int brightness) => ExpectColorAsync(brightness, NotifyColorEdit);

        public Task ExpectColorAsync(int brightness, Action action)
        {
            var before = Color.Brightnesses.Count;
            action();
            return WaitForAsync(() => Color.Brightnesses.ToArray().Skip(before).Contains(brightness));
        }

        public Task WaitForOverlayAsync(Profile? profile)
        {
            var foreground = Input.LastForegroundIdentity!.Value;
            return WaitForAsync(() => Crosshair.Applications.Any(application =>
                application.ForegroundGeneration == foreground.Generation &&
                application.ForegroundHwnd == foreground.Hwnd &&
                ReferenceEquals(application.Profile, profile)));
        }

        public ColorApplyOutcome Apply(DisplayInfo display, DisplayColorProfile profile)
        {
            if (_block is not null && profile.Brightness == A_SECONDARY)
            {
                _block.Apply(display, profile);
            }

            return Color.Apply(display, profile);
        }

        private static DisplayInfo Display(string id) => new()
        {
            Id = id, Name = id, DeviceName = $@"\\.\{id}", IsPrimary = id == "DISPLAY1"
        };
    }
}
