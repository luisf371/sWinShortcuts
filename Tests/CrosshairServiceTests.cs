using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class CrosshairServiceTests
{
    [Fact]
    public void Stop_RejectsLateApplyAndToggle_UntilRestartWithCenteredOverlay()
    {
        var pending = new Queue<Action>();
        var hook = new FakeInputHookService();
        using var service = new CrosshairService(new NullLoggerService(), hook, pending.Enqueue);
        var profile = CreateGatedProfile();
        profile.Crosshair.OffsetX = 30;
        service.ApplyProfile(profile, (IntPtr)100);
        hook.RaiseCrosshairOffsetToggle(profile);

        service.Stop();
        service.ApplyProfile(profile, (IntPtr)100);
        hook.RaiseCrosshairOffsetToggle(profile);
        Drain(pending);
        Assert.False(service.AppliedVisibility);
        Assert.False(hook.RightButtonObservation);

        service.Start();
        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        Assert.True(service.AppliedVisibility);
        Assert.True(hook.RightButtonObservation);
        Assert.Equal((0, 0), service.AppliedOffset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OffsetToggle_ProfileChangesDuringDelivery_DoesNotToggleNewProfile(bool returnToOriginal)
    {
        var pending = new Queue<Action>();
        using var hook = InputHookServiceTestExtensions.CreateWithFakeForeground(
            new NullLoggerService(), new RecordingInputSender());
        hook.StartInputExecutorForTesting();
        try
        {
            var original = CreateGatedProfile();
            original.Crosshair.OffsetX = 30;
            var next = CreateGatedProfile();
            next.Crosshair.OffsetX = 75;
            CrosshairService? overlay = null;
            // Deliver activation between input admission and the overlay's event subscriber.
            hook.CrosshairOffsetToggleRequested += (_, _) =>
            {
                hook.SetForegroundIdentity((IntPtr)101, 42, next.NormalizedExecutable, 2);
                hook.ActivateProfile(next, 2);
                overlay!.ApplyProfile(next, (IntPtr)101, 2);
                if (returnToOriginal)
                {
                    hook.SetForegroundIdentity((IntPtr)100, 42, original.NormalizedExecutable, 3);
                    hook.ActivateProfile(original, 3);
                    overlay.ApplyProfile(original, (IntPtr)100, 3);
                }
            };
            using var service = new CrosshairService(new NullLoggerService(), hook, pending.Enqueue);
            overlay = service;
            hook.SetForegroundIdentity((IntPtr)100, 42, original.NormalizedExecutable, 1);
            hook.ActivateProfile(original, 1);
            service.ApplyProfile(original, (IntPtr)100, 1);
            Drain(pending);

            hook.SetCrosshairOffsetToggleKey(System.Windows.Input.Key.F8);
            hook.DispatchDecodedKeyboardEvent(0x77, true, false);
            Drain(pending);

            Assert.True(service.AppliedVisibility);
            Assert.Equal((0, 0), service.AppliedOffset);
        }
        finally
        {
            hook.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void OffsetToggle_SameProfileRepublished_UsesNewGenerationWithoutResettingMode()
    {
        var pending = new Queue<Action>();
        var hook = new FakeInputHookService();
        using var service = new CrosshairService(new NullLoggerService(), hook, pending.Enqueue);
        var profile = CreateGatedProfile();
        profile.Crosshair.OffsetX = 20;
        service.ApplyProfile(profile, (IntPtr)100, 1);
        hook.RaiseCrosshairOffsetToggle(profile, 1);
        Drain(pending);
        Assert.Equal((20, 0), service.AppliedOffset);

        service.ApplyProfile(profile, (IntPtr)100, 2);
        Assert.Empty(pending); // Same configuration need not redraw, but its generation must advance.
        hook.RaiseCrosshairOffsetToggle(profile, 1);
        Assert.Empty(pending);
        Assert.Equal((20, 0), service.AppliedOffset);
        hook.RaiseCrosshairOffsetToggle(profile, 2);
        Drain(pending);
        Assert.Equal((0, 0), service.AppliedOffset);
    }

    [Fact]
    public void OffsetToggle_UsesSavedPositionThenCentersWithoutChangingVisibility()
    {
        var pending = new Queue<Action>();
        var hook = new FakeInputHookService();
        using var service = new CrosshairService(new NullLoggerService(), hook, pending.Enqueue);
        var profile = CreateGatedProfile();
        profile.Crosshair.OffsetX = 120;
        profile.Crosshair.OffsetY = -40;
        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        Assert.Equal((0, 0), service.AppliedOffset);

        hook.RaiseCrosshairOffsetToggle(profile);
        Assert.Equal((0, 0), service.AppliedOffset); // hook callback only queues window work
        Drain(pending);
        Assert.Equal((120, -40), service.AppliedOffset);
        Assert.True(service.AppliedVisibility);

        hook.RaiseRightButton(true);
        hook.RaiseCrosshairOffsetToggle(profile);
        Drain(pending);
        Assert.Equal((0, 0), service.AppliedOffset);
        Assert.False(service.AppliedVisibility);
        hook.RaiseRightButton(false);
        Drain(pending);
        Assert.True(service.AppliedVisibility);
        Assert.Equal(120, profile.Crosshair.OffsetX);
        Assert.Equal(-40, profile.Crosshair.OffsetY);
    }

    [Fact]
    public void OffsetToggle_ProfileEditsAndFocusChanges_KeepIndependentModesUntilStop()
    {
        var pending = new Queue<Action>();
        var hook = new FakeInputHookService();
        using var service = new CrosshairService(new NullLoggerService(), hook, pending.Enqueue);
        var profile = CreateGatedProfile();
        profile.Crosshair.OffsetX = 20;
        service.ApplyProfile(profile, (IntPtr)100);
        hook.RaiseCrosshairOffsetToggle(profile);
        Drain(pending);

        profile.Crosshair.OffsetX = -30;
        profile.Crosshair.OffsetY = 45;
        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        Assert.Equal((-30, 45), service.AppliedOffset);
        service.ApplyProfile(profile, (IntPtr)100);
        Assert.Empty(pending);
        RaiseDisplaySettingsChanged(service);
        Drain(pending);
        Assert.Equal((-30, 45), service.AppliedOffset);

        // Even an otherwise identical profile starts centered.
        var next = CreateGatedProfile();
        next.Crosshair.OffsetX = -30;
        next.Crosshair.OffsetY = 45;
        service.ApplyProfile(next, (IntPtr)100);
        Drain(pending);
        Assert.Equal((0, 0), service.AppliedOffset);

        hook.RaiseCrosshairOffsetToggle(next);
        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        Assert.Equal((-30, 45), service.AppliedOffset);
        hook.RaiseCrosshairOffsetToggle(profile); // Center only this profile.
        service.ApplyProfile(next, (IntPtr)100);
        Drain(pending);
        Assert.Equal((-30, 45), service.AppliedOffset);
        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        Assert.Equal((0, 0), service.AppliedOffset);

        // Stop clears remembered modes for inactive profiles as well.
        service.Stop();
        service.Start();
        service.ApplyProfile(next, (IntPtr)100);
        Drain(pending);
        Assert.Equal((0, 0), service.AppliedOffset);
    }

    [Fact]
    public void OffsetToggle_QueuedBeforeDisableUsesLatestStateAndCannotArmHiddenProfile()
    {
        var pending = new Queue<Action>();
        var hook = new FakeInputHookService();
        using var service = new CrosshairService(new NullLoggerService(), hook, pending.Enqueue);
        var profile = CreateGatedProfile();
        profile.Crosshair.OffsetX = 20;
        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        hook.RaiseCrosshairOffsetToggle(profile);
        var oldToggle = pending.Dequeue();

        profile.Crosshair.IsEnabled = false;
        service.ApplyProfile(profile, (IntPtr)100);
        hook.RaiseCrosshairOffsetToggle(profile);
        Drain(pending);
        oldToggle();
        Assert.Equal((0, 0), service.AppliedOffset);
        Assert.False(service.AppliedVisibility);

        profile.Crosshair.IsEnabled = true;
        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        Assert.Equal((0, 0), service.AppliedOffset);
        hook.RaiseCrosshairOffsetToggle(profile);
        service.Dispose();
        Drain(pending);
        hook.RaiseCrosshairOffsetToggle(profile);
        Assert.Empty(pending);
        Assert.False(service.AppliedVisibility);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hide_NeverCreatedWindow_DoesNotWaitForBlockedDispatcher(bool previouslyHidden)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            using var started = new ManualResetEventSlim();
            using var completed = new ManualResetEventSlim();
            using var service = new CrosshairService(new NullLoggerService(), new FakeInputHookService());
            typeof(CrosshairService).GetField("_dispatcher",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(service, dispatcher);
            Task hide = Task.CompletedTask;
            Exception? failure = null;
            try
            {
                if (previouslyHidden)
                {
                    service.ApplyProfile(null, IntPtr.Zero);
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                        new Action(() => frame.Continue = false));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                hide = Task.Run(() =>
                {
                    started.Set();
                    service.ApplyProfile(null, IntPtr.Zero);
                    completed.Set();
                });
                Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                // Match Application.Exit: the owner waits while HasShutdownStarted is still false.
                Assert.False(dispatcher.HasShutdownStarted);
                Assert.True(completed.Wait(TimeSpan.FromSeconds(1)), "Hide waited for the blocked dispatcher.");
                Assert.Null(typeof(CrosshairService).GetField("_window",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                dispatcher.InvokeShutdown();
                hide.GetAwaiter().GetResult(); // abort any blocked Invoke before disposing its signals
            }
            if (failure is null)
            {
                result.SetResult();
            }
            else
            {
                result.SetException(failure);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

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

    [Fact]
    public void DisplaySettingsChanged_UnchangedProfile_QueuesConfigurationRefresh()
    {
        var pending = new Queue<Action>();
        using var service = new CrosshairService(new NullLoggerService(), new FakeInputHookService(), pending.Enqueue);
        var profile = CreateGatedProfile();
        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        service.ApplyProfile(profile, (IntPtr)100);
        Assert.Empty(pending);

        RaiseDisplaySettingsChanged(service);

        Assert.Single(pending);
        Drain(pending);
        Assert.True(service.AppliedVisibility);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisplaySettingsChanged_QueuedBeforeNewerState_UsesLatestVisibility(bool disableProfile)
    {
        var pending = new Queue<Action>();
        var hook = new FakeInputHookService();
        using var service = new CrosshairService(new NullLoggerService(), hook, pending.Enqueue);
        var profile = CreateGatedProfile();
        service.ApplyProfile(profile, (IntPtr)100);
        Drain(pending);
        RaiseDisplaySettingsChanged(service);
        var displayRefresh = Assert.Single(pending);
        pending.Clear();

        if (disableProfile)
        {
            profile.Crosshair.IsEnabled = false;
            service.ApplyProfile(profile, (IntPtr)100);
        }
        else
        {
            hook.RaiseRightButton(true);
        }

        Assert.True(service.AppliedVisibility);
        displayRefresh();

        Assert.False(service.AppliedVisibility);
    }

    [Fact]
    public void DisplaySettingsChanged_AfterDispose_DoesNotQueueOrReshowCrosshair()
    {
        var pending = new Queue<Action>();
        var hook = new FakeInputHookService();
        using var service = new CrosshairService(new NullLoggerService(), hook, pending.Enqueue);
        service.ApplyProfile(CreateGatedProfile(), (IntPtr)100);
        Drain(pending);
        RaiseDisplaySettingsChanged(service);
        var displayRefresh = Assert.Single(pending);
        pending.Clear();

        service.Dispose();
        RaiseDisplaySettingsChanged(service);
        displayRefresh();

        Assert.Empty(pending);
        Assert.False(service.AppliedVisibility);
        Assert.False(hook.RightButtonObservation);
    }

    private static void RaiseDisplaySettingsChanged(CrosshairService service)
    {
        var handler = typeof(CrosshairService).GetMethod("OnDisplaySettingsChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(handler);
        handler.Invoke(service, [null, EventArgs.Empty]);
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
