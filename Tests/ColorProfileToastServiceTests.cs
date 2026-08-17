using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

// Headless (no Application.Current): the window layer is skipped and the applied-variant /
// shown-count bookkeeping — enqueue-only dispatch, no dedup, dispose fence — is what the internal
// test seam (enqueue) makes observable. No WPF window is ever created. The two integration tests
// below additionally verify ProfileActivationService raises the toast from the hook event only on
// a REAL flip (the before/after ToggleVariant compare).
public sealed class ColorProfileToastServiceTests
{
    [Fact]
    public void Show_EnqueuesOnly_NeverAppliesInline()
    {
        var pending = new Queue<Action>();
        using var service = new ColorProfileToastService(new NullLoggerService(), enqueue: pending.Enqueue);

        service.Show(ColorVariant.Secondary);

        // The call only SCHEDULED the apply — nothing ran on the caller's thread. This is the
        // contract that makes Show safe to call from the keyboard-hook thread.
        Assert.Null(service.AppliedVariant);
        Assert.Equal(0, service.ShownCount);

        Assert.Single(pending)();
        Assert.Equal(ColorVariant.Secondary, service.AppliedVariant);
        Assert.Equal(1, service.ShownCount);
    }

    [Fact]
    public void Show_EveryPressApplies_NoDedup()
    {
        using var service = new ColorProfileToastService(new NullLoggerService(), enqueue: a => a());

        service.Show(ColorVariant.Primary);
        service.Show(ColorVariant.Secondary);

        // Deliberately NO dedup (unlike RapidFireStatusService): every press restarts a fresh 2s
        // window, so each applies.
        Assert.Equal(2, service.ShownCount);
        Assert.Equal(ColorVariant.Secondary, service.AppliedVariant);
    }

    [Fact]
    public void Dispose_FencesSubsequentShows()
    {
        var service = new ColorProfileToastService(new NullLoggerService(), enqueue: a => a());

        service.Show(ColorVariant.Primary);
        Assert.Equal(ColorVariant.Primary, service.AppliedVariant);

        service.Dispose();

        // Fenced: a late Show can neither apply nor throw.
        service.Show(ColorVariant.Secondary);
        Assert.Equal(ColorVariant.Primary, service.AppliedVariant);
        Assert.Equal(1, service.ShownCount);
    }

    [Fact]
    public void QueuedApplyAfterDispose_NoOps()
    {
        var pending = new Queue<Action>();
        var service = new ColorProfileToastService(new NullLoggerService(), enqueue: pending.Enqueue);

        service.Show(ColorVariant.Primary);
        Assert.Single(pending);

        service.Dispose();

        // A callback queued before Dispose but not yet run must not touch anything post-fence.
        pending.Dequeue()();
        Assert.Null(service.AppliedVariant);
        Assert.Equal(0, service.ShownCount);
    }

    [Fact]
    public async Task ColorVariantToggle_RealFlipToastsNewVariant()
    {
        var store = new InMemoryProfileStore();
        var profile = CreateColorToggleProfile(hasSecondary: true);
        store.Profiles.Add(profile);

        var manager = new ProfileManager(store);
        var watcher = new FakeForegroundWatcher();
        var input = new FakeInputHookService();
        var color = new RecordingColorControlService();
        using var toast = new ColorProfileToastService(new NullLoggerService(), enqueue: a => a());
        var service = new ProfileActivationService(
            manager,
            watcher,
            input,
            new FakeSystemTrayService(),
            color,
            new FakeDisplayService { Displays = [CreateDisplay("DISPLAY1")] },
            new FakeCrosshairService(),
            new NullLoggerService(),
            toast);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Bring the game to the foreground and wait for its color plan to apply + publish —
            // the toggle handler targets _activeColorSettings, which exists only after that
            // publication (press-time contract: a press before publication no-ops).
            watcher.RaiseForegroundChanged("game.exe", 123);
            await WaitForAsync(() => color.AppliedProfiles.Count > 0);
            await Task.Delay(100); // publication completes a few instructions after the apply

            input.RaiseColorVariantToggle();

            Assert.Equal(ColorVariant.Secondary, toast.AppliedVariant);
            Assert.Equal(1, toast.ShownCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ColorVariantToggle_NoPopulatedSecondary_NoFlipNoToast()
    {
        var store = new InMemoryProfileStore();
        var profile = CreateColorToggleProfile(hasSecondary: false);
        store.Profiles.Add(profile);

        var manager = new ProfileManager(store);
        var watcher = new FakeForegroundWatcher();
        var input = new FakeInputHookService();
        var color = new RecordingColorControlService();
        using var toast = new ColorProfileToastService(new NullLoggerService(), enqueue: a => a());
        var service = new ProfileActivationService(
            manager,
            watcher,
            input,
            new FakeSystemTrayService(),
            color,
            new FakeDisplayService { Displays = [CreateDisplay("DISPLAY1")] },
            new FakeCrosshairService(),
            new NullLoggerService(),
            toast);

        await service.StartAsync(CancellationToken.None);
        try
        {
            watcher.RaiseForegroundChanged("game.exe", 123);
            await WaitForAsync(() => color.AppliedProfiles.Count > 0);
            await Task.Delay(100);

            input.RaiseColorVariantToggle();

            // ToggleVariant no-ops without a populated Secondary: before == after, so no toast.
            Assert.Null(toast.AppliedVariant);
            Assert.Equal(0, toast.ShownCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static Profile CreateColorToggleProfile(bool hasSecondary)
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.ColorSettings.IsEnabled = true;
        profile.ColorSettings.SetProfile(new DisplayColorProfile
        {
            DisplayId = "DISPLAY1",
            IsEnabled = true,
            Brightness = 40,
            Contrast = 50,
            Gamma = 1.0,
            DigitalVibrance = 50
        }, ColorVariant.Primary);

        if (hasSecondary)
        {
            profile.ColorSettings.HasSecondary = true;
            profile.ColorSettings.SetProfile(new DisplayColorProfile
            {
                DisplayId = "DISPLAY1",
                IsEnabled = true,
                Brightness = 90,
                Contrast = 50,
                Gamma = 1.0,
                DigitalVibrance = 80
            }, ColorVariant.Secondary);
        }

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
}
