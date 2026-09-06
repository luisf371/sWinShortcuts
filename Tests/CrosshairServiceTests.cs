using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class CrosshairServiceTests
{
    [Fact]
    public void OldRightDownCallback_AfterUngatedProfile_DoesNotHideCrosshair()
    {
        var pending = new Queue<Action>();
        var hook = new FakeInputHookService();
        using var service = new CrosshairService(new NullLoggerService(), hook, pending.Enqueue);
        var profile = CreateGatedProfile();

        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        hook.RaiseRightButton(true);
        var olderButtonCallback = pending.Dequeue();

        profile.Crosshair.HideWhileRightButtonHeld = false;
        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        olderButtonCallback();

        Assert.True(service.AppliedVisibility);
        Assert.False(hook.RightButtonObservation);
    }

    [Fact]
    public void OldRightDownCallback_AfterUngatedProfile_ReapplyStillShowsCrosshair()
    {
        var pending = new Queue<Action>();
        var hook = new FakeInputHookService();
        using var service = new CrosshairService(new NullLoggerService(), hook, pending.Enqueue);
        var profile = CreateGatedProfile();
        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        hook.RaiseRightButton(true);
        var olderButtonCallback = pending.Dequeue();

        profile.Crosshair.HideWhileRightButtonHeld = false;
        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        olderButtonCallback();

        // Moving focus to another monitor reapplies the same ungated policy.
        service.ApplyProfile(profile, (IntPtr)101);
        Drain(pending);

        Assert.True(service.AppliedVisibility);
        Assert.False(hook.RightButtonObservation);
    }

    [Fact]
    public void DisableThenReenable_WithNewerUp_ReverseCallbacksUseCurrentState()
    {
        var pending = new Queue<Action>();
        var hook = new FakeInputHookService();
        using var service = new CrosshairService(new NullLoggerService(), hook, pending.Enqueue);
        var profile = CreateGatedProfile();
        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        hook.RaiseRightButton(true);
        profile.Crosshair.IsEnabled = false;
        service.ApplyProfile(profile, (IntPtr)100);
        profile.Crosshair.IsEnabled = true;
        service.ApplyProfile(profile, (IntPtr)100);
        hook.RaiseRightButton(false);

        foreach (var callback in pending.Reverse())
        {
            callback();
        }

        Assert.True(service.AppliedVisibility);
        Assert.True(hook.RightButtonObservation);
    }

    [Fact]
    public void QueuedWork_AfterDispose_CannotShowOrRearmCrosshair()
    {
        var pending = new Queue<Action>();
        var hook = new FakeInputHookService();
        using var service = new CrosshairService(new NullLoggerService(), hook, pending.Enqueue);
        var profile = CreateGatedProfile();
        service.ApplyProfile(profile, (IntPtr)100);
        service.Dispose();

        Drain(pending);
        Assert.False(service.AppliedVisibility);
        service.ApplyProfile(profile, (IntPtr)101);
        hook.RaiseRightButton(false);
        Drain(pending);

        Assert.False(service.AppliedVisibility);
        Assert.False(hook.RightButtonObservation);
    }

    [Fact]
    public void RightButtonEvent_ChangesVisibilityOnlyWhenQueuedWorkRuns()
    {
        var pending = new Queue<Action>();
        var hook = new FakeInputHookService();
        using var service = new CrosshairService(new NullLoggerService(), hook, pending.Enqueue);
        service.ApplyProfile(CreateGatedProfile(), (IntPtr)100);
        Drain(pending);

        hook.RaiseRightButton(true);

        Assert.True(service.AppliedVisibility);
        Assert.Single(pending);
        Drain(pending);
        Assert.False(service.AppliedVisibility);
    }

    private static Profile CreateGatedProfile()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Crosshair.IsEnabled = true;
        profile.Crosshair.HideWhileRightButtonHeld = true;
        return profile;
    }

    private static void Drain(Queue<Action> pending)
    {
        while (pending.TryDequeue(out var callback))
        {
            callback();
        }
    }
}
