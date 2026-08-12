using System.Collections.Concurrent;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class InputExecutorReliabilityTests
{
    [Fact]
    public async Task Executor_TapAndTransitions_EmitFifoOnOneWorker()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            Assert.True(service.EnqueueTapForTesting(Key.A, durationMs: 1));
            Assert.True(service.EnqueueTransitionForTesting(Key.B, isDown: true));
            Assert.True(service.EnqueueTransitionForTesting(Key.B, isDown: false));

            await WaitForAsync(() => sender.Transitions.Count == 4);
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            var transitions = sender.Transitions.ToArray();

            var expected = new[]
            {
                (Key.A, true),
                (Key.A, false),
                (Key.B, true),
                (Key.B, false)
            };
            Assert.True(expected.SequenceEqual(transitions.Select(x => (x.Key, x.IsDown))));
            Assert.Single(transitions.Select(x => x.ThreadId).Distinct());
            Assert.Equal(
                transitions[0].ThreadId,
                Assert.Single(sender.DummyThreadIds));
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void AutoRunPhysicalKey_HeldThroughActivation_RepeatIsNotFreshButPostReleasePressIsFresh()
    {
        var physicallyDown = false;

        // Initial physical W down arrives before the Auto-Run trigger chord.
        Assert.True(InputHookService.ApplyAutoRunPhysicalKeyEvent(
            ref physicallyDown,
            isKeyDown: true,
            isKeyUp: false));

        // Activation preserves the hook-owned state. A typematic repeat from the held press is not fresh.
        Assert.False(InputHookService.ApplyAutoRunPhysicalKeyEvent(
            ref physicallyDown,
            isKeyDown: true,
            isKeyUp: false));

        // Release clears the physical edge latch. The active handoff handler separately decides whether
        // the target-visible UP is suppressed; this pure helper only owns physical-state bookkeeping.
        Assert.False(InputHookService.ApplyAutoRunPhysicalKeyEvent(
            ref physicallyDown,
            isKeyDown: false,
            isKeyUp: true));

        // A genuinely new physical press after that release is fresh and therefore cancels Auto-Run.
        Assert.True(InputHookService.ApplyAutoRunPhysicalKeyEvent(
            ref physicallyDown,
            isKeyDown: true,
            isKeyUp: false));
    }

    [Fact]
    public void AutoRunTriggerModifier_NoneAllowsSingleKeyTrigger()
    {
        Assert.True(InputHookService.IsTriggerModifierDown(ModifierKeys.None));
    }

    [Fact]
    public async Task AutoRunForeground_PhysicalWHeldAtActivation_SuppressesReleaseThenStartsScriptedHold()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = new Profile { Name = "Game", Executable = "game.exe" };
            service.ConfigureForegroundAutoRunHandoffForTesting(
                profile,
                sprintEnabled: true,
                sprintMode: SprintActivation.Hold,
                sprintKey: Key.LeftShift);

            // Typematic W repeats remain ordinary physical input and do not start the scripted sequence.
            Assert.False(service.HandleAutoRunForTesting(Key.W, isKeyDown: true, isKeyUp: false));
            Assert.Empty(sender.Transitions);

            // KeyWait semantics: suppress the target-visible UP so movement never stops. The observed hook
            // event is authoritative; the off-hook executor emits one W DOWN followed by deferred sprint.
            Assert.True(service.HandleAutoRunForTesting(Key.W, isKeyDown: false, isKeyUp: true));
            await WaitForAsync(() => sender.Transitions.Count == 2);
            Assert.Equal(
                new[] { (Key.W, true), (Key.LeftShift, true) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());

            // A duplicate UP cannot start a second handoff.
            Assert.False(service.HandleAutoRunForTesting(Key.W, isKeyDown: false, isKeyUp: true));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(2, sender.Transitions.Count);
        }
        finally
        {
            service.ReleaseForegroundAutoRun();
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task AutoRunForeground_QueuedPhysicalWHandoffReleasedBeforeExecutorDrain_DoesNotInjectDown()
    {
        var sender = new RecordingInputSender(blockDummy: true);
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var gate = service.EnqueueDummyForTesting();
            Assert.True(sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
            var profile = new Profile { Name = "Game", Executable = "game.exe" };
            service.ConfigureForegroundAutoRunHandoffForTesting(
                profile,
                sprintEnabled: true,
                sprintMode: SprintActivation.Hold,
                sprintKey: Key.LeftShift);

            Assert.True(service.HandleAutoRunForTesting(Key.W, isKeyDown: false, isKeyUp: true));
            Assert.Empty(sender.Transitions);

            service.ReleaseForegroundAutoRun();
            sender.ReleaseDummy.Set();
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(
                new[] { (Key.W, false), (Key.LeftShift, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        }
        finally
        {
            sender.ReleaseDummy.Set();
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task AutoRunForeground_SuppressedWHandoffUp_ReleasesPairedConsumers()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureCombinedOverrideForTesting(
                source: Key.W,
                target: Key.F,
                suppressOriginal: true);
            service.ConfigureLauncherLatchForTesting(
                new Profile { Name = "Windows" },
                Key.W);
            await WaitForAsync(() => sender.Transitions.Count == 1);

            var profile = new Profile { Name = "Game", Executable = "game.exe" };
            service.ConfigureForegroundAutoRunHandoffForTesting(profile);

            Assert.True(service.HandleAutoRunForTesting(Key.W, isKeyDown: false, isKeyUp: true));
            await WaitForAsync(() => sender.Transitions.Count == 3);
            Assert.Equal(
                new[] { (Key.F, true), (Key.W, true), (Key.F, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
            Assert.False(service.HandleLauncherForTesting(Key.W, isDown: false));
        }
        finally
        {
            service.ReleaseForegroundAutoRun();
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task AutoRunForeground_FreshWPress_StopsOnlyOnMatchingPhysicalUp()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = new Profile { Name = "Game", Executable = "game.exe" };
            service.ConfigureForegroundAutoRunForTesting(
                profile,
                sprintInjected: true,
                sprintKey: Key.LeftShift);

            Assert.False(service.HandleAutoRunForTesting(Key.W, isKeyDown: true, isKeyUp: false));
            Assert.False(service.HandleAutoRunForTesting(Key.W, isKeyDown: true, isKeyUp: false));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Empty(sender.Transitions);

            Assert.False(service.HandleAutoRunForTesting(Key.W, isKeyDown: false, isKeyUp: true));
            await WaitForAsync(() => sender.Transitions.Count == 2);
            Assert.Equal(
                new[] { (Key.W, false), (Key.LeftShift, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void AutoRunBackground_AttachPolicy_UsesOneForcedReassertOrUnfocusedTarget()
    {
        Assert.False(InputHookService.ShouldAttachBackgroundInput(
            onBackgroundThread: true,
            targetIsForegroundProcess: true,
            targetThread: 22,
            currentThread: 11,
            targetIsHung: false));

        Assert.True(InputHookService.ShouldAttachBackgroundInput(
            onBackgroundThread: true,
            targetIsForegroundProcess: true,
            targetThread: 22,
            currentThread: 11,
            targetIsHung: false,
            forceAttach: true));

        Assert.True(InputHookService.ShouldAttachBackgroundInput(
            onBackgroundThread: true,
            targetIsForegroundProcess: false,
            targetThread: 22,
            currentThread: 11,
            targetIsHung: false));

        Assert.False(InputHookService.ShouldAttachBackgroundInput(
            onBackgroundThread: true,
            targetIsForegroundProcess: false,
            targetThread: 22,
            currentThread: 11,
            targetIsHung: true));
    }

    [Fact]
    public async Task Executor_StaleDownSkipped_UpRemainsUnconditional()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.SetForegroundGenerationsForTesting(active: 1, published: 2);
            Assert.True(service.EnqueueTransitionForTesting(Key.C, isDown: true, foregroundGeneration: 1));
            Assert.True(service.EnqueueTransitionForTesting(Key.C, isDown: false, foregroundGeneration: 1));

            await WaitForAsync(() => sender.Transitions.Count == 1);
            var transition = Assert.Single(sender.Transitions);
            Assert.Equal(Key.C, transition.Key);
            Assert.False(transition.IsDown);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task Executor_FailedDown_StillAttemptsUpAndContinuesDraining()
    {
        var sender = new RecordingInputSender(failFirstDown: true);
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            Assert.True(service.EnqueueTapForTesting(Key.C, durationMs: 1));
            Assert.True(service.EnqueueTransitionForTesting(Key.D, isDown: true));
            Assert.True(service.EnqueueTransitionForTesting(Key.D, isDown: false));

            await WaitForAsync(() => sender.Transitions.Count == 4);
            Assert.Equal(
                new[]
                {
                    (Key.C, true),
                    (Key.C, false),
                    (Key.D, true),
                    (Key.D, false)
                },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task Executor_DummyAcknowledgement_CompletesAfterExecution()
    {
        var sender = new RecordingInputSender(blockDummy: true);
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var acknowledgement = service.EnqueueDummyForTesting();
            Assert.True(sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(acknowledgement.IsCompleted);

            sender.ReleaseDummy.Set();
            Assert.True(await acknowledgement.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            sender.ReleaseDummy.Set();
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task AltMouse_QuickTap_EmitsOnlyTapPair()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateAltMouseProfile(holdThresholdMs: 100);
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);

            Assert.True(service.HandleAltMouseForTesting(sWinShortcuts.Models.MouseButton.Middle, isDown: true));
            Assert.True(service.HandleAltMouseForTesting(sWinShortcuts.Models.MouseButton.Middle, isDown: false));

            await WaitForAsync(() => sender.Transitions.Count == 2);
            await Task.Delay(125);
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Collection(
                sender.Transitions,
                item =>
                {
                    Assert.Equal(Key.A, item.Key);
                    Assert.True(item.IsDown);
                },
                item =>
                {
                    Assert.Equal(Key.A, item.Key);
                    Assert.False(item.IsDown);
                });
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task AltMouse_Hold_EmitsOnlyHoldPair()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateAltMouseProfile(holdThresholdMs: 10);
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);

            Assert.True(service.HandleAltMouseForTesting(sWinShortcuts.Models.MouseButton.Middle, isDown: true));
            await WaitForAsync(() => sender.Transitions.Count == 2);
            Assert.True(service.HandleAltMouseForTesting(sWinShortcuts.Models.MouseButton.Middle, isDown: false));
            await Task.Delay(40);
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Collection(
                sender.Transitions,
                item =>
                {
                    Assert.Equal(Key.B, item.Key);
                    Assert.True(item.IsDown);
                },
                item =>
                {
                    Assert.Equal(Key.B, item.Key);
                    Assert.False(item.IsDown);
                });
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task AltMouse_LiveRebind_CancelsGestureButConsumesRecordedUp()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateAltMouseProfile(holdThresholdMs: 100);
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);

            Assert.True(service.HandleAltMouseForTesting(sWinShortcuts.Models.MouseButton.Middle, isDown: true));
            profile.AltMouse.Bindings = new Dictionary<sWinShortcuts.Models.MouseButton, MouseButtonBinding>
            {
                [sWinShortcuts.Models.MouseButton.Middle] = new()
                {
                    TapKey = Key.C,
                    HoldKey = Key.D
                }
            };
            service.ReconcileProfileSettings(profile, ProfileChangeKind.AltMouse);

            Assert.True(service.HandleAltMouseForTesting(sWinShortcuts.Models.MouseButton.Middle, isDown: false));
            await Task.Delay(150);
            Assert.Empty(sender.Transitions);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task CapsLock_NormalWithoutRemap_PassesPhysicalTransitions()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureActiveProfileForTesting(
                CreateCapsLockProfile(CapsLockMode.Normal),
                foregroundGeneration: 1,
                altPressed: false);

            Assert.False(service.HandleCapsLockForTesting(isDown: true));
            Assert.False(service.HandleCapsLockForTesting(isDown: true));
            Assert.False(service.HandleCapsLockForTesting(isDown: false));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Empty(sender.Transitions);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task CapsLock_NormalRemap_MirrorsDownRepeatsAndUp()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureActiveProfileForTesting(
                CreateCapsLockProfile(CapsLockMode.Normal, remapEnabled: true, Key.Escape),
                foregroundGeneration: 1,
                altPressed: false);

            Assert.True(service.HandleCapsLockForTesting(isDown: true));
            Assert.True(service.HandleCapsLockForTesting(isDown: true));
            Assert.True(service.HandleCapsLockForTesting(isDown: false));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));

            var expected = new[]
            {
                (Key.Escape, true),
                (Key.Escape, true),
                (Key.Escape, false)
            };
            Assert.True(expected.SequenceEqual(
                sender.Transitions.Select(x => (x.Key, x.IsDown))));
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task CapsLock_NormalRemapForceRelease_ReleasesHeldOutputOnce()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureActiveProfileForTesting(
                CreateCapsLockProfile(CapsLockMode.Normal, remapEnabled: true, Key.Escape),
                foregroundGeneration: 1,
                altPressed: false);

            Assert.True(service.HandleCapsLockForTesting(isDown: true));
            await WaitForAsync(() => sender.Transitions.Count == 1);
            service.ForceReleaseCapsLockForTesting();
            Assert.True(service.HandleCapsLockForTesting(isDown: false));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));

            var expected = new[]
            {
                (Key.Escape, true),
                (Key.Escape, false)
            };
            Assert.True(expected.SequenceEqual(
                sender.Transitions.Select(x => (x.Key, x.IsDown))));
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task CapsLock_HardBoundaryWithoutPhysicalUp_AllowsNextFreshPress()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureActiveProfileForTesting(
                CreateCapsLockProfile(CapsLockMode.Normal, remapEnabled: true, Key.Escape),
                foregroundGeneration: 1,
                altPressed: false);

            Assert.True(service.HandleCapsLockForTesting(isDown: true));
            await WaitForAsync(() => sender.Transitions.Count == 1);
            service.ForceReleaseCapsLockForTesting(preservePhysicalPairing: false);

            // The first physical UP was swallowed by the session boundary. This DOWN must still be fresh.
            Assert.True(service.HandleCapsLockForTesting(isDown: true));
            Assert.True(service.HandleCapsLockForTesting(isDown: false));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));

            var expected = new[]
            {
                (Key.Escape, true),
                (Key.Escape, false),
                (Key.Escape, true),
                (Key.Escape, false)
            };
            Assert.True(expected.SequenceEqual(
                sender.Transitions.Select(x => (x.Key, x.IsDown))));
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapsLock_DoubleNormal_TapsOnPhysicalDownAndUp(bool remapEnabled)
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var output = remapEnabled ? Key.Escape : Key.CapsLock;
            service.ConfigureActiveProfileForTesting(
                CreateCapsLockProfile(CapsLockMode.DoubleNormal, remapEnabled, Key.Escape),
                foregroundGeneration: 1,
                altPressed: false);

            Assert.True(service.HandleCapsLockForTesting(isDown: true));
            Assert.True(service.HandleCapsLockForTesting(isDown: true));
            Assert.True(service.HandleCapsLockForTesting(isDown: false));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));

            var expected = new[]
            {
                (output, true),
                (output, false),
                (output, true),
                (output, false)
            };
            Assert.True(expected.SequenceEqual(
                sender.Transitions.Select(x => (x.Key, x.IsDown))));
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task CapsLock_Disabled_SuppressesWithoutOutput()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureActiveProfileForTesting(
                CreateCapsLockProfile(CapsLockMode.Disabled, remapEnabled: true, Key.Escape),
                foregroundGeneration: 1,
                altPressed: false);

            Assert.True(service.HandleCapsLockForTesting(isDown: true));
            Assert.True(service.HandleCapsLockForTesting(isDown: true));
            Assert.True(service.HandleCapsLockForTesting(isDown: false));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Empty(sender.Transitions);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task CapsLock_DoubleNormalForceRelease_CompletesSecondTapOnce()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureActiveProfileForTesting(
                CreateCapsLockProfile(CapsLockMode.DoubleNormal),
                foregroundGeneration: 1,
                altPressed: false);

            Assert.True(service.HandleCapsLockForTesting(isDown: true));
            await WaitForAsync(() => sender.Transitions.Count == 2);
            service.ForceReleaseCapsLockForTesting();
            Assert.True(service.HandleCapsLockForTesting(isDown: false));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));

            var expected = new[]
            {
                (Key.CapsLock, true),
                (Key.CapsLock, false),
                (Key.CapsLock, true),
                (Key.CapsLock, false)
            };
            Assert.True(expected.SequenceEqual(
                sender.Transitions.Select(x => (x.Key, x.IsDown))));
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task CapsLock_DoubleNormalInvalidatedBeforeInitialTap_SkipsBothTaps()
    {
        var sender = new RecordingInputSender(blockDummy: true);
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var blocker = service.EnqueueDummyForTesting();
            Assert.True(sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
            service.ConfigureActiveProfileForTesting(
                CreateCapsLockProfile(CapsLockMode.DoubleNormal),
                foregroundGeneration: 1,
                altPressed: false);

            Assert.True(service.HandleCapsLockForTesting(isDown: true));
            service.ForceReleaseCapsLockForTesting();
            Assert.True(service.HandleCapsLockForTesting(isDown: false));

            sender.ReleaseDummy.Set();
            Assert.True(await blocker.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Empty(sender.Transitions);
        }
        finally
        {
            sender.ReleaseDummy.Set();
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task CapsLock_DoubleNormalFailedInitialDown_SkipsReleaseTap()
    {
        var sender = new RecordingInputSender(failFirstDown: true);
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureActiveProfileForTesting(
                CreateCapsLockProfile(CapsLockMode.DoubleNormal),
                foregroundGeneration: 1,
                altPressed: false);

            Assert.True(service.HandleCapsLockForTesting(isDown: true));
            Assert.True(service.HandleCapsLockForTesting(isDown: false));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(2, sender.Transitions.Count);
            Assert.All(sender.Transitions, item => Assert.Equal(Key.CapsLock, item.Key));
            Assert.Equal(new[] { true, false }, sender.Transitions.Select(x => x.IsDown));
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task CapsLock_DoubleNormalConsecutivePresses_EmitTwoTapsEach()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureActiveProfileForTesting(
                CreateCapsLockProfile(CapsLockMode.DoubleNormal),
                foregroundGeneration: 1,
                altPressed: false);

            for (var press = 0; press < 2; press++)
            {
                Assert.True(service.HandleCapsLockForTesting(isDown: true));
                Assert.True(service.HandleCapsLockForTesting(isDown: false));
            }

            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(8, sender.Transitions.Count);
            Assert.All(sender.Transitions, item => Assert.Equal(Key.CapsLock, item.Key));
            Assert.Equal(
                new[] { true, false, true, false, true, false, true, false },
                sender.Transitions.Select(x => x.IsDown));
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task AutoRun_LiveDisable_ReleasesRecordedMoveAndSprint()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = new Profile { Name = "Game", Executable = "game.exe" };
            profile.AutoRun.IsEnabled = false;
            service.ConfigureForegroundAutoRunForTesting(
                profile,
                sprintInjected: true,
                sprintKey: Key.LeftShift);

            service.ReconcileProfileSettings(profile, ProfileChangeKind.AutoRun);
            await WaitForAsync(() => sender.Transitions.Count == 2);

            Assert.Collection(
                sender.Transitions,
                item =>
                {
                    Assert.Equal(Key.W, item.Key);
                    Assert.False(item.IsDown);
                },
                item =>
                {
                    Assert.Equal(Key.LeftShift, item.Key);
                    Assert.False(item.IsDown);
                });
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task Combined_ForcedRelease_PreservesSuppressionUntilPhysicalUp()
    {
        var sender = new RecordingInputSender(blockDummy: true);
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var gate = service.EnqueueDummyForTesting();
            Assert.True(sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
            service.ConfigureCombinedOverrideForTesting(
                source: Key.E,
                target: Key.F,
                suppressOriginal: true);
            service.ForceReleaseCombinedForTesting();

            // A typematic repeat after the runtime release must inherit the consumed DOWN, and the
            // matching physical UP clears that latch. A later unrelated UP passes through.
            Assert.True(service.HandleCombinedForTesting(Key.E, isDown: true));
            Assert.True(service.HandleCombinedForTesting(Key.E, isDown: false));
            Assert.False(service.HandleCombinedForTesting(Key.E, isDown: false));

            sender.ReleaseDummy.Set();
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(2)));
            await WaitForAsync(() => sender.Transitions.Count == 1);
            var item = Assert.Single(sender.Transitions);
            Assert.Equal(Key.F, item.Key);
            Assert.False(item.IsDown);
        }
        finally
        {
            sender.ReleaseDummy.Set();
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task Combined_AdvancedOff_PreservesPassThroughDecisionUntilPhysicalUp()
    {
        var sender = new RecordingInputSender(blockDummy: true);
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var gate = service.EnqueueDummyForTesting();
            Assert.True(sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
            service.ConfigureCombinedOverrideForTesting(
                source: Key.G,
                target: Key.H,
                suppressOriginal: false);
            service.ForceReleaseUnsuppressedCombinedForTesting();

            Assert.False(service.HandleCombinedForTesting(Key.G, isDown: true));
            Assert.False(service.HandleCombinedForTesting(Key.G, isDown: false));

            sender.ReleaseDummy.Set();
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(2)));
            await WaitForAsync(() => sender.Transitions.Count == 1);
            var item = Assert.Single(sender.Transitions);
            Assert.Equal(Key.H, item.Key);
            Assert.False(item.IsDown);
        }
        finally
        {
            sender.ReleaseDummy.Set();
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task Combined_TwoSourcesShareTarget_ReleasesOnlyOnFinalSourceUp()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureCombinedOverrideForTesting(
                source: Key.E,
                target: Key.F,
                suppressOriginal: true);
            service.ConfigureCombinedOverrideForTesting(
                source: Key.G,
                target: Key.F,
                suppressOriginal: true);

            await WaitForAsync(() => sender.Transitions.Count == 1);
            Assert.True(service.HandleCombinedForTesting(Key.E, isDown: false));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Single(sender.Transitions);

            Assert.True(service.HandleCombinedForTesting(Key.G, isDown: false));
            await WaitForAsync(() => sender.Transitions.Count == 2);

            Assert.Equal(
                new[] { (Key.F, true), (Key.F, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task HoldBreath_DisabledWhileTimerPending_StaleCallbackCannotPress()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = new Profile { Name = "Game", Executable = "game.exe" };
            profile.RightClickHoldBreath.IsEnabled = true;
            profile.RightClickHoldBreath.DelayMilliseconds = 200;
            profile.RightClickHoldBreath.HoldBreathKey = Key.LeftShift;
            service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

            service.HandleHoldBreathRightButtonForTesting(isDown: true);
            profile.RightClickHoldBreath.IsEnabled = false;
            service.ReconcileProfileSettings(profile, ProfileChangeKind.HoldBreath);

            // Simulate an already-dispatched Timer callback that Timer.Change(Infinite) cannot recall.
            service.FireHoldBreathTimerForTesting();
            await Task.Delay(225);
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Empty(sender.Transitions);
        }
        finally
        {
            service.HandleHoldBreathRightButtonForTesting(isDown: false);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task HoldBreath_DisabledAfterDown_ReleasesRecordedKeyExactlyOnce()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = new Profile { Name = "Game", Executable = "game.exe" };
            profile.RightClickHoldBreath.IsEnabled = true;
            profile.RightClickHoldBreath.DelayMilliseconds = 0;
            profile.RightClickHoldBreath.HoldBreathKey = Key.LeftShift;
            profile.RightClickHoldBreath.Mode = HoldBreathMode.Hold;
            service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

            service.HandleHoldBreathRightButtonForTesting(isDown: true);
            await WaitForAsync(() => sender.Transitions.Count == 1);

            profile.RightClickHoldBreath.IsEnabled = false;
            service.ReconcileProfileSettings(profile, ProfileChangeKind.HoldBreath);
            await WaitForAsync(() => sender.Transitions.Count == 2);
            service.HandleHoldBreathRightButtonForTesting(isDown: false);
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(
                new[] { (Key.LeftShift, true), (Key.LeftShift, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Theory]
    [InlineData(100, 0, 100)]
    [InlineData(100, 8.2, 92)]
    [InlineData(100, 99.9, 1)]
    [InlineData(100, 100, 100)]
    [InlineData(100, 300, 100)]
    public void RapidFireSuccessorDelay_CompensatesNormalSendAndResetsAfterOverrun(
        int targetDelayMs,
        double sendElapsedMs,
        int expectedDelayMs)
    {
        Assert.Equal(
            expectedDelayMs,
            InputHookService.CalculateRapidFireSuccessorDelay(targetDelayMs, sendElapsedMs));
    }

    [Fact]
    public async Task RapidFire_DefaultOffAndAdvancedOff_DoNotClick()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateRapidFireProfile();
            service.ConfigureRapidFireForTesting(profile, foregroundGeneration: 1, armed: false);

            service.HandleRapidFireLeftButtonForTesting(isDown: true);
            service.FireRapidFireTimerForTesting();
            service.HandleRapidFireLeftButtonForTesting(isDown: false);

            service.ConfigureRapidFireForTesting(profile, foregroundGeneration: 1);
            service.AdvancedModeEnabled = false;
            service.HandleRapidFireLeftButtonForTesting(isDown: true);
            service.FireRapidFireTimerForTesting();
            service.HandleRapidFireLeftButtonForTesting(isDown: false);

            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(service.RapidFireArmedForTesting);
            Assert.Empty(sender.MouseClickThreadIds);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFireToggle_TypematicRepeatTogglesOnceAndReassignmentDisarms()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureRapidFireForTesting(
                CreateRapidFireProfile(),
                foregroundGeneration: 1,
                armed: false);
            service.SetRapidFireToggleKey(Key.F8);

            service.HandleRapidFireToggleForTesting(Key.F8, isDown: true);
            Assert.True(service.RapidFireArmedForTesting);

            service.HandleRapidFireToggleForTesting(Key.F8, isDown: true);
            Assert.True(service.RapidFireArmedForTesting);

            service.SetRapidFireToggleKey(Key.F9);
            Assert.False(service.RapidFireArmedForTesting);

            service.HandleRapidFireToggleForTesting(Key.F9, isDown: true);
            Assert.True(service.RapidFireArmedForTesting);
            service.HandleRapidFireToggleForTesting(Key.F9, isDown: false);
            service.HandleRapidFireToggleForTesting(Key.F9, isDown: true);
            Assert.False(service.RapidFireArmedForTesting);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_AltLeftWinsAndMatchingUpAllowsNextFreshPress()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureRapidFireForTesting(CreateRapidFireProfile(), foregroundGeneration: 1);

            service.HandleRapidFireLeftButtonForTesting(isDown: true, consumed: true);
            service.FireRapidFireTimerForTesting();
            service.HandleRapidFireLeftButtonForTesting(isDown: false, consumed: true);
            Assert.Empty(sender.MouseClickThreadIds);

            service.HandleRapidFireLeftButtonForTesting(isDown: true);
            service.FireRapidFireTimerForTesting();
            service.HandleRapidFireLeftButtonForTesting(isDown: false);

            Assert.Single(sender.MouseClickThreadIds);
        }
        finally
        {
            service.HandleRapidFireLeftButtonForTesting(isDown: false);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task RapidFire_ClickDoesNotWaitForSharedExecutor()
    {
        var sender = new RecordingInputSender(blockDummy: true);
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var gate = service.EnqueueDummyForTesting();
            Assert.True(sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
            service.ConfigureRapidFireForTesting(CreateRapidFireProfile(), foregroundGeneration: 1);

            service.HandleRapidFireLeftButtonForTesting(isDown: true);
            service.FireRapidFireTimerForTesting();
            service.HandleRapidFireLeftButtonForTesting(isDown: false);
            Assert.Single(sender.MouseClickThreadIds);

            sender.ReleaseDummy.Set();
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            sender.ReleaseDummy.Set();
            service.HandleRapidFireLeftButtonForTesting(isDown: false);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task RapidFire_HotkeyReassignmentDuringClickPreventsSuccessor()
    {
        var sender = new RecordingInputSender(blockMouse: true);
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.SetRapidFireToggleKey(Key.F8);
            service.ConfigureRapidFireForTesting(CreateRapidFireProfile(), foregroundGeneration: 1);
            service.HandleRapidFireLeftButtonForTesting(isDown: true);
            var fireTask = Task.Run(service.FireRapidFireTimerForTesting);
            Assert.True(sender.MouseEntered.Wait(TimeSpan.FromSeconds(2)));
            service.SetRapidFireToggleKey(Key.F9);

            sender.ReleaseMouse.Set();
            await fireTask.WaitAsync(TimeSpan.FromSeconds(2));
            service.FireRapidFireTimerForTesting();
            Assert.False(service.RapidFireArmedForTesting);
            Assert.Single(sender.MouseClickThreadIds);
        }
        finally
        {
            sender.ReleaseMouse.Set();
            service.HandleRapidFireLeftButtonForTesting(isDown: false);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task RapidFire_ProfileSwitchDuringClickPreventsSuccessor()
    {
        var sender = new RecordingInputSender(blockMouse: true);
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureRapidFireForTesting(CreateRapidFireProfile(), foregroundGeneration: 1);
            service.HandleRapidFireLeftButtonForTesting(isDown: true);
            var fireTask = Task.Run(service.FireRapidFireTimerForTesting);
            Assert.True(sender.MouseEntered.Wait(TimeSpan.FromSeconds(2)));
            service.ActivateProfile(CreateRapidFireProfile(), foregroundGeneration: 2);

            sender.ReleaseMouse.Set();
            await fireTask.WaitAsync(TimeSpan.FromSeconds(2));
            service.FireRapidFireTimerForTesting();
            Assert.False(service.RapidFireArmedForTesting);
            Assert.Single(sender.MouseClickThreadIds);
        }
        finally
        {
            sender.ReleaseMouse.Set();
            service.HandleRapidFireLeftButtonForTesting(isDown: false);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task RapidFire_BlockedClickStaysSingleFlightAndReleasePreventsSuccessor()
    {
        var sender = new RecordingInputSender(blockMouse: true);
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureRapidFireForTesting(CreateRapidFireProfile(), foregroundGeneration: 1);
            service.HandleRapidFireLeftButtonForTesting(isDown: true);
            var fireTask = Task.Run(service.FireRapidFireTimerForTesting);

            Assert.True(sender.MouseEntered.Wait(TimeSpan.FromSeconds(2)));
            service.FireRapidFireTimerForTesting();
            Assert.Single(sender.MouseClickThreadIds);

            service.HandleRapidFireLeftButtonForTesting(isDown: false);
            sender.ReleaseMouse.Set();
            await fireTask.WaitAsync(TimeSpan.FromSeconds(2));
            service.FireRapidFireTimerForTesting();
            Assert.Single(sender.MouseClickThreadIds);
        }
        finally
        {
            sender.ReleaseMouse.Set();
            service.HandleRapidFireLeftButtonForTesting(isDown: false);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_ClickCompletionRearmsOneSuccessor()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateRapidFireProfile();
            profile.RapidFire.IntervalMilliseconds = RapidFireSettings.MaxIntervalMilliseconds;
            service.ConfigureRapidFireForTesting(profile, foregroundGeneration: 1);
            service.HandleRapidFireLeftButtonForTesting(isDown: true);

            service.FireRapidFireTimerForTesting();
            service.FireRapidFireTimerForTesting();
            service.HandleRapidFireLeftButtonForTesting(isDown: false);

            Assert.Equal(2, sender.MouseClickThreadIds.Count);
            Assert.All(sender.MouseHoldMilliseconds, hold => Assert.InRange(hold, 10, 20));
        }
        finally
        {
            service.HandleRapidFireLeftButtonForTesting(isDown: false);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_ClickSenderExceptionIsContainedAndStopsPress()
    {
        var sender = new RecordingInputSender(throwMouse: true);
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureRapidFireForTesting(CreateRapidFireProfile(), foregroundGeneration: 1);
            service.HandleRapidFireLeftButtonForTesting(isDown: true);

            service.FireRapidFireTimerForTesting();
            service.FireRapidFireTimerForTesting();

            Assert.Empty(sender.MouseClickThreadIds);
        }
        finally
        {
            service.HandleRapidFireLeftButtonForTesting(isDown: false);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void Launcher_DisabledWhileHeld_StillConsumesAndClearsKeyUp()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        var windowsProfile = new Profile
        {
            Name = ProfileConstants.WindowsProfileName,
            Kind = ProfileKind.Windows,
            Executable = string.Empty,
            IsEnabled = false
        };
        windowsProfile.WindowsLauncher.IsEnabled = false;
        service.ConfigureLauncherLatchForTesting(windowsProfile, Key.NumPad1);

        Assert.True(service.HandleLauncherForTesting(Key.NumPad1, isDown: false));
        Assert.False(service.HandleLauncherForTesting(Key.NumPad1, isDown: false));
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

    private static Profile CreateAltMouseProfile(int holdThresholdMs)
    {
        return new Profile
        {
            Name = "Game",
            Executable = "game.exe",
            AltMouse =
            {
                IsEnabled = true,
                HoldThresholdMilliseconds = holdThresholdMs,
                Bindings = new Dictionary<sWinShortcuts.Models.MouseButton, MouseButtonBinding>
                {
                    [sWinShortcuts.Models.MouseButton.Middle] = new()
                    {
                        TapKey = Key.A,
                        HoldKey = Key.B
                    }
                }
            }
        };
    }

    private static Profile CreateCapsLockProfile(
        CapsLockMode mode,
        bool remapEnabled = false,
        Key? remapTarget = null)
    {
        return new Profile
        {
            Name = "Game",
            Executable = "game.exe",
            CapsLock =
            {
                IsEnabled = true,
                Mode = mode,
                IsRemapEnabled = remapEnabled,
                RemapTarget = remapTarget
            }
        };
    }

    private static Profile CreateRapidFireProfile()
    {
        return new Profile
        {
            Name = "Game",
            Executable = "game.exe",
            RapidFire =
            {
                IsEnabled = true,
                IntervalMilliseconds = RapidFireSettings.MinIntervalMilliseconds,
                JitterMilliseconds = 0
            }
        };
    }

    private sealed class RecordingInputSender(
        bool blockDummy = false,
        bool failFirstDown = false,
        bool blockMouse = false,
        bool throwMouse = false) : IInputSender
    {
        private readonly bool _blockDummy = blockDummy;
        private int _failNextDown = failFirstDown ? 1 : 0;

        public ConcurrentQueue<(Key Key, bool IsDown, int ThreadId)> Transitions { get; } = new();

        public ManualResetEventSlim DummyEntered { get; } = new(false);

        public ManualResetEventSlim ReleaseDummy { get; } = new(false);

        public ConcurrentQueue<int> DummyThreadIds { get; } = new();

        public ConcurrentQueue<int> MouseClickThreadIds { get; } = new();

        public ConcurrentQueue<int> MouseHoldMilliseconds { get; } = new();

        public ManualResetEventSlim MouseEntered { get; } = new(false);

        public ManualResetEventSlim ReleaseMouse { get; } = new(false);

        public bool SendKey(Key key, bool isKeyDown)
        {
            Transitions.Enqueue((key, isKeyDown, Environment.CurrentManagedThreadId));
            if (isKeyDown && Interlocked.Exchange(ref _failNextDown, 0) == 1)
            {
                return false;
            }

            return true;
        }

        public bool SendVirtualKeyTap(int virtualKey)
        {
            return true;
        }

        public bool SendLeftClick(int holdMilliseconds)
        {
            if (throwMouse)
            {
                throw new InvalidOperationException("Synthetic click failure");
            }

            MouseHoldMilliseconds.Enqueue(holdMilliseconds);
            MouseClickThreadIds.Enqueue(Environment.CurrentManagedThreadId);
            MouseEntered.Set();
            return !blockMouse || ReleaseMouse.Wait(TimeSpan.FromSeconds(2));
        }

        public bool SendDummyKey()
        {
            DummyThreadIds.Enqueue(Environment.CurrentManagedThreadId);
            DummyEntered.Set();
            return !_blockDummy || ReleaseDummy.Wait(TimeSpan.FromSeconds(2));
        }
    }
}
