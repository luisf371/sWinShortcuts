using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class InputExecutorReliabilityTests
{
    [Fact]
    public async Task Executor_TapAndTransitions_EmitFifoOnOneWorker()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        Assert.True(AutoRunStateMachine.ApplyPhysicalKeyEvent(
            ref physicallyDown,
            isKeyDown: true,
            isKeyUp: false));

        // Activation preserves the hook-owned state. A typematic repeat from the held press is not fresh.
        Assert.False(AutoRunStateMachine.ApplyPhysicalKeyEvent(
            ref physicallyDown,
            isKeyDown: true,
            isKeyUp: false));

        // Release clears the physical edge latch. The active handoff handler separately decides whether
        // the target-visible UP is suppressed; this pure helper only owns physical-state bookkeeping.
        Assert.False(AutoRunStateMachine.ApplyPhysicalKeyEvent(
            ref physicallyDown,
            isKeyDown: false,
            isKeyUp: true));

        // A genuinely new physical press after that release is fresh and therefore cancels Auto-Run.
        Assert.True(AutoRunStateMachine.ApplyPhysicalKeyEvent(
            ref physicallyDown,
            isKeyDown: true,
            isKeyUp: false));
    }

    [Fact]
    public void AutoRunTriggerModifier_NoneAllowsSingleKeyTrigger()
    {
        var runtime = new InputRuntimeState();
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var autoRun = new AutoRunStateMachine(runtime, queue, random, new NullLoggerService());

        Assert.True(autoRun.IsTriggerModifierDown(ModifierKeys.None));
    }

    [Fact]
    public async Task AutoRunForeground_PhysicalWHeldAtActivation_SuppressesReleaseThenStartsScriptedHold()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        Assert.False(AutoRunStateMachine.ShouldAttachBackgroundInput(
            onBackgroundThread: true,
            targetIsForegroundProcess: true,
            targetThread: 22,
            currentThread: 11,
            targetIsHung: false));

        Assert.True(AutoRunStateMachine.ShouldAttachBackgroundInput(
            onBackgroundThread: true,
            targetIsForegroundProcess: true,
            targetThread: 22,
            currentThread: 11,
            targetIsHung: false,
            forceAttach: true));

        Assert.True(AutoRunStateMachine.ShouldAttachBackgroundInput(
            onBackgroundThread: true,
            targetIsForegroundProcess: false,
            targetThread: 22,
            currentThread: 11,
            targetIsHung: false));

        Assert.False(AutoRunStateMachine.ShouldAttachBackgroundInput(
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
    public async Task AltKeyboard_QuickTap_EmitsOnlyTapPair()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateAltKeyboardProfile(holdThresholdMs: 100);
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);

            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));
            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: false));

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
    public async Task AltKeyboard_Hold_EmitsOnlyHoldPair()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateAltKeyboardProfile(holdThresholdMs: 10);
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);

            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));
            await WaitForAsync(() => sender.Transitions.Count == 2);
            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: false));
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
    public async Task AltKeyboard_LiveRebind_CancelsGestureButConsumesRecordedUp()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateAltKeyboardProfile(holdThresholdMs: 100);
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);

            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));
            profile.AltKeyboard.Bindings = new Dictionary<Key, AltKeyboardBinding>
            {
                [Key.Q] = new()
                {
                    TapKey = Key.C,
                    HoldKey = Key.D
                }
            };
            service.ReconcileProfileSettings(profile, ProfileChangeKind.AltKeyboard);

            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: false));
            await Task.Delay(150);
            Assert.Empty(sender.Transitions);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task AltKeyboard_AutoRepeat_SuppressesOwnedRepeatsAndIgnoresForeignOnes()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateAltKeyboardProfile(holdThresholdMs: 100);
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);

            // Consumed press: every typematic repeat and the UP itself are owned (suppressed), and the
            // repeats never start a second gesture — exactly ONE tap pair comes out.
            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));
            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));
            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));
            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: false));

            await WaitForAsync(() => sender.Transitions.Count == 2);
            await Task.Delay(125);

            // Unbound press: DOWN, repeats, and UP all fall through untouched — nothing suppressed,
            // nothing injected, and a repeat must not start a gesture even with Alt held.
            Assert.False(service.HandleAltKeyboardForTesting(Key.R, isDown: true));
            Assert.False(service.HandleAltKeyboardForTesting(Key.R, isDown: true));
            Assert.False(service.HandleAltKeyboardForTesting(Key.R, isDown: false));

            // A fresh press of the same trigger key after its UP starts a new gesture.
            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));
            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: false));

            await WaitForAsync(() => sender.Transitions.Count == 4);
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(4, sender.Transitions.Count);
            Assert.All(sender.Transitions, item => Assert.Equal(Key.A, item.Key));
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task AltKeyboard_PanicOverride_CancelsGestureWithoutFiringAndClearsLatches()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateAltKeyboardProfile(holdThresholdMs: 20);
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);

            // Alt+Keyboard owns the press; then hold-breath panic starts consuming the same key's
            // events before HandleAltKeyboard could see them.
            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));
            service.HandleAltKeyboardPanicOverrideForTesting(Key.Q, isDown: true);

            // The orphaned hold timer must NOT fire past the threshold.
            await Task.Delay(80);
            Assert.Empty(sender.Transitions);

            // Panic owns the UP too; the latches clear so the next fresh press is not swallowed.
            service.HandleAltKeyboardPanicOverrideForTesting(Key.Q, isDown: false);
            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));
            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: false));

            await WaitForAsync(() => sender.Transitions.Count == 2);
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Collection(
                sender.Transitions,
                item => Assert.Equal(Key.A, item.Key),
                item => Assert.Equal(Key.A, item.Key));
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task AltKeyboard_PanicOverride_InvalidatesHoldActionAlreadyQueued()
    {
        var sender = new RecordingInputSender(blockDummy: true);
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            // Block the shared injector so a fired hold action sits QUEUED instead of sent.
            var gate = service.EnqueueDummyForTesting();
            Assert.True(sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));

            var profile = CreateAltKeyboardProfile(holdThresholdMs: 10);
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);
            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));

            // Hold timer fires while the injector is still blocked: the mapped pair queues behind it.
            await Task.Delay(120);
            Assert.DoesNotContain(sender.Transitions, t => t.Key == Key.B);

            // A suppressing panic takes over the key BEFORE the queued action drains: the press is
            // cancelled, so NEITHER half of the mapped pair may ever send (an ownerless synthetic
            // UP would be an unmatched release event).
            service.HandleAltKeyboardPanicOverrideForTesting(Key.Q, isDown: true);
            sender.ReleaseDummy.Set();
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(2)));

            await Task.Delay(50);
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.DoesNotContain(sender.Transitions, t => t.Key == Key.B);
        }
        finally
        {
            sender.ReleaseDummy.Set();
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task AltKeyboard_CancelAfterDownSent_StillSendsPairedUp()
    {
        var sender = new RecordingInputSender(blockDummy: true);
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var gate = service.EnqueueDummyForTesting();
            Assert.True(sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));

            var profile = CreateAltKeyboardProfile(holdThresholdMs: 10);
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);
            Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));

            // Hold timer queues the mapped pair behind the blocked injector, then let it drain.
            await Task.Delay(120);
            sender.ReleaseDummy.Set();
            Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(2)));

            // Cancel strictly AFTER the DOWN reached SendInput: the already-sent DOWN must not
            // strand its mapped key, so the paired UP still drains.
            await WaitForAsync(() => sender.Transitions.Any(t => t.Key == Key.B && t.IsDown));
            service.HandleAltKeyboardPanicOverrideForTesting(Key.Q, isDown: true);
            await WaitForAsync(() => sender.Transitions.Any(t => t.Key == Key.B && !t.IsDown));

            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(2, sender.Transitions.Count(t => t.Key == Key.B));
        }
        finally
        {
            sender.ReleaseDummy.Set();
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void AltKeyboard_PhysicalStateRederive_ReconcilesTypematicLatches()
    {
        using var service = new InputFeatureHarness(new NullLoggerService(), new RecordingInputSender());
        var profile = CreateAltKeyboardProfile(holdThresholdMs: 100);
        service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);

        // Key still physically held across the boundary (e.g. watchdog reinstall): the press stays
        // owned — repeats and the UP keep being consumed.
        Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));
        service.RederiveAltKeyboardPhysicalStateForTesting(_ => true);
        Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));
        Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: false));

        // Key released while unobserved (its UP was lost in the hook-swap window): re-derive must
        // drop the ownership latch so the stray UP passes through and the next press is fresh.
        Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));
        service.RederiveAltKeyboardPhysicalStateForTesting(_ => false);
        Assert.False(service.HandleAltKeyboardForTesting(Key.Q, isDown: false));
        Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: true));
        Assert.True(service.HandleAltKeyboardForTesting(Key.Q, isDown: false));
    }

    [Fact]
    public async Task CapsLock_NormalWithoutRemap_PassesPhysicalTransitions()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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

    [Fact]
    public async Task EarlyCancel_MasterOff_PassesThroughWithoutCancelling()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateEarlyCancelProfile(delayMs: 200, suppressEarlyCancel: false);
            service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

            service.HandleHoldBreathRightButtonForTesting(isDown: true);
            Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
            Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

            // The disabled presses must not have secretly cancelled anything: enabling the master
            // (model-only, no reconcile) lets the very next press cancel the still-pending action.
            profile.RightClickHoldBreath.SuppressEarlyCancelInput = true;
            Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
            Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

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
    public void EarlyCancel_SuccessfulPendingCancel_ConsumesPressPair()
    {
        using var service = new InputFeatureHarness(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();
        var profile = CreateEarlyCancelProfile(delayMs: 200, suppressEarlyCancel: true);
        service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

        service.HandleHoldBreathRightButtonForTesting(isDown: true);

        // The cancelling press owns its typematic repeats and its UP.
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

        // The cancelled arm can no longer fire (stale timer elapse is a no-op).
        service.FireHoldBreathTimerForTesting();
    }

    [Fact]
    public void EarlyCancel_SecondPressSameAimCycle_PassesThrough()
    {
        using var service = new InputFeatureHarness(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();
        var profile = CreateEarlyCancelProfile(delayMs: 200, suppressEarlyCancel: true);
        service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

        service.HandleHoldBreathRightButtonForTesting(isDown: true);
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

        // Nothing left to cancel in this aim cycle: later presses keep their native function.
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));
    }

    [Fact]
    public void EarlyCancel_NewAimCycle_CancelsAgain()
    {
        using var service = new InputFeatureHarness(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();
        var profile = CreateEarlyCancelProfile(delayMs: 200, suppressEarlyCancel: true);
        service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

        service.HandleHoldBreathRightButtonForTesting(isDown: true);
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

        // Re-aim (RMB up clears the re-arm veto, RMB down arms again): the first press cancels again.
        service.HandleHoldBreathRightButtonForTesting(isDown: false);
        service.HandleHoldBreathRightButtonForTesting(isDown: true);
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));
    }

    [Fact]
    public async Task EarlyCancel_ImmediateHoldMode_CancelsOwnedKey()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateEarlyCancelProfile(delayMs: 0, suppressEarlyCancel: true);
            profile.RightClickHoldBreath.Mode = HoldBreathMode.Hold;
            service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

            service.HandleHoldBreathRightButtonForTesting(isDown: true);
            await WaitForAsync(() => sender.Transitions.Count == 1); // hold-breath key DOWN sent

            // The owned Hold-mode key is cancellable: exactly one paired release follows.
            Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
            await WaitForAsync(() => sender.Transitions.Count == 2);
            Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

            // Second press in the same aim cycle passes through.
            Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
            Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(
                new[] { (Key.LeftShift, true), (Key.LeftShift, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        }
        finally
        {
            service.HandleHoldBreathRightButtonForTesting(isDown: false);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task EarlyCancel_ToggleModeAfterTap_PassesThrough()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateEarlyCancelProfile(delayMs: 0, suppressEarlyCancel: true);
            profile.RightClickHoldBreath.Mode = HoldBreathMode.Toggle;
            service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

            service.HandleHoldBreathRightButtonForTesting(isDown: true);
            await WaitForAsync(() => sender.Transitions.Count == 2); // self-paired tap completed

            // The committed Toggle tap owns nothing: a later press has nothing to cancel.
            Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
            Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));
        }
        finally
        {
            service.HandleHoldBreathRightButtonForTesting(isDown: false);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void EarlyCancel_MouseTrigger_XButton_CancelsThenPassesThrough()
    {
        using var service = new InputFeatureHarness(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();
        var profile = CreateEarlyCancelProfile(delayMs: 200, suppressEarlyCancel: true);
        profile.RightClickHoldBreath.PanicTrigger =
            InputTrigger.FromMouseButton(sWinShortcuts.Models.MouseButton.XButton1);
        service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

        service.HandleHoldBreathRightButtonForTesting(isDown: true);
        Assert.True(service.HandleHoldBreathPanicMouseForTesting(
            sWinShortcuts.Models.MouseButton.XButton1, isDown: true));
        Assert.True(service.HandleHoldBreathPanicMouseForTesting(
            sWinShortcuts.Models.MouseButton.XButton1, isDown: false));

        Assert.False(service.HandleHoldBreathPanicMouseForTesting(
            sWinShortcuts.Models.MouseButton.XButton1, isDown: true));
        Assert.False(service.HandleHoldBreathPanicMouseForTesting(
            sWinShortcuts.Models.MouseButton.XButton1, isDown: false));
    }

    [Fact]
    public void EarlyCancel_MidPressUncheck_OwnedPairStaysConsumed()
    {
        using var service = new InputFeatureHarness(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();
        var profile = CreateEarlyCancelProfile(delayMs: 200, suppressEarlyCancel: true);
        service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

        service.HandleHoldBreathRightButtonForTesting(isDown: true);
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));

        // The DOWN was consumed, so its repeats + UP stay consumed even if the master is switched
        // off mid-press — passing an unmatched UP through would break input pairing.
        profile.RightClickHoldBreath.SuppressEarlyCancelInput = false;
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

        // A FRESH press under the disabled master passes through.
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));
    }

    [Fact]
    public void EarlyCancel_RepeatOfPreAimPress_NeverStartsAPanic()
    {
        using var service = new InputFeatureHarness(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();
        var profile = CreateEarlyCancelProfile(delayMs: 200, suppressEarlyCancel: true);
        service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

        // The trigger key goes down BEFORE aiming: its DOWN passes through natively (no RMB yet).
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));

        // Aim (pending hold-breath). The typematic REPEAT of that already-native press must not
        // start a panic — cancelling from a repeat would eat the UP while the app still saw the
        // original native DOWN — and its UP must pass through too.
        service.HandleHoldBreathRightButtonForTesting(isDown: true);
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

        // Nothing was cancelled: a genuinely fresh press still cancels the pending action.
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));
    }

    [Fact]
    public void EarlyCancel_EligibilityFlipsMidHold_RepeatStaysNative()
    {
        using var service = new InputFeatureHarness(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();
        var profile = CreateEarlyCancelProfile(delayMs: 200, suppressEarlyCancel: true);

        // Hold Breath disabled: the trigger's DOWN passes through natively (and must be latched as
        // physically down even though the feature is ineligible).
        profile.RightClickHoldBreath.IsEnabled = false;
        service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));

        // Feature enabled mid-hold, then aim: the typematic repeat of that native press must stay
        // native, and so must its UP — no panic from a repeat, no swallowed UP.
        profile.RightClickHoldBreath.IsEnabled = true;
        service.HandleHoldBreathRightButtonForTesting(isDown: true);
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

        // The pending action was not cancelled: a genuinely fresh press still cancels it.
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));
    }

    [Fact]
    public void EarlyCancel_RebindWhileNewTriggerHeld_RepeatStaysNative()
    {
        using var service = new InputFeatureHarness(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();
        var profile = CreateEarlyCancelProfile(delayMs: 200, suppressEarlyCancel: true);
        profile.RightClickHoldBreath.PanicTrigger = InputTrigger.FromKey(Key.E);
        service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

        // Q is not the trigger: its DOWN passes through unlatched.
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));

        // Live rebind E -> Q while Q is still held (the dispatcher re-derivation reads the live
        // trigger against the physical key state).
        profile.RightClickHoldBreath.PanicTrigger = InputTrigger.FromKey(Key.Q);
        service.ReconcileProfileSettings(profile, ProfileChangeKind.HoldBreath);
        service.RederivePanicTriggerPhysicalStateForTesting(_ => true);

        service.HandleHoldBreathRightButtonForTesting(isDown: true);
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));
    }

    [Fact]
    public void EarlyCancel_ConsumedOldTriggerUp_DoesNotClearNewTriggerLatch()
    {
        using var service = new InputFeatureHarness(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();
        var profile = CreateEarlyCancelProfile(delayMs: 200, suppressEarlyCancel: true);
        service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

        // Aim cycle 1: Q cancels and stays held (consumed owner = Q, fresh-edge latch = Q).
        service.HandleHoldBreathRightButtonForTesting(isDown: true);
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));

        // Rebind to E mid-press; E goes down natively (veto blocks a second cancel) and owns the
        // fresh-edge latch now.
        profile.RightClickHoldBreath.PanicTrigger = InputTrigger.FromKey(Key.E);
        service.ReconcileProfileSettings(profile, ProfileChangeKind.HoldBreath);
        service.RederivePanicTriggerPhysicalStateForTesting(
            vk => sWinShortcuts.Utilities.KeyInteropUtilities.ToVirtualKey(Key.E) == vk);
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.E, isDown: true));

        // Releasing the OLD consumed Q must not clear E's latch.
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

        // New aim cycle: E's repeat (still the same physical press) stays native; a fresh E press
        // cancels the re-armed action.
        service.HandleHoldBreathRightButtonForTesting(isDown: false);
        service.HandleHoldBreathRightButtonForTesting(isDown: true);
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.E, isDown: true));
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.E, isDown: false));
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.E, isDown: true));
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.E, isDown: false));
    }

    [Fact]
    public void EarlyCancel_DerivationPendingWindow_TriggerPressStaysNative()
    {
        using var service = new InputFeatureHarness(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();
        var profile = CreateEarlyCancelProfile(delayMs: 200, suppressEarlyCancel: true);
        service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

        // Simulate the async re-derivation fence: a fresh trigger press during the window is
        // edge-latched but stays native — the latch baseline is not yet trustworthy.
        var ticket = service.PanicDerivationBeginForTesting();
        Assert.True(service.PanicTriggerDerivationPendingForTesting);
        service.HandleHoldBreathRightButtonForTesting(isDown: true);
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

        // The pending action was never cancelled; once the fence retires, a fresh press cancels it.
        service.PanicDerivationRetireForTesting(ticket);
        Assert.False(service.PanicTriggerDerivationPendingForTesting);
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));
    }

    [Fact]
    public void EarlyCancel_OverlappingDerivations_StaleTicketCannotClearNewerFence()
    {
        using var service = new InputFeatureHarness(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();
        var profile = CreateEarlyCancelProfile(delayMs: 200, suppressEarlyCancel: true);
        service.ConfigureHoldBreathForTesting(profile, foregroundGeneration: 1);

        // Two overlapping derivation requests: retiring the OLDER ticket must leave the newer
        // request's fence standing (an older dispatcher closure must not unfence a newer request).
        var older = service.PanicDerivationBeginForTesting();
        var newer = service.PanicDerivationBeginForTesting();
        service.PanicDerivationRetireForTesting(older);
        Assert.True(service.PanicTriggerDerivationPendingForTesting);

        service.HandleHoldBreathRightButtonForTesting(isDown: true);
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.False(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));

        // Retiring the current ticket unfences; a fresh press cancels.
        service.PanicDerivationRetireForTesting(newer);
        Assert.False(service.PanicTriggerDerivationPendingForTesting);
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: true));
        Assert.True(service.HandleHoldBreathPanicKeyForTesting(Key.Q, isDown: false));
    }

    private static Profile CreateEarlyCancelProfile(int delayMs, bool suppressEarlyCancel)
    {
        var profile = new Profile { Name = "Game", Executable = "game.exe" };
        profile.RightClickHoldBreath.IsEnabled = true;
        profile.RightClickHoldBreath.DelayMilliseconds = delayMs;
        profile.RightClickHoldBreath.HoldBreathKey = Key.LeftShift;
        profile.RightClickHoldBreath.PanicTrigger = InputTrigger.FromKey(Key.Q);
        profile.RightClickHoldBreath.SuppressEarlyCancelInput = suppressEarlyCancel;
        return profile;
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
            RapidFireStateMachine.CalculateSuccessorDelay(targetDelayMs, sendElapsedMs));
    }

    [Fact]
    public async Task RapidFire_DefaultOffAndAdvancedOff_DoNotClick()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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
    public void RapidFire_ArmAndFireTiming_AreReportedInDebugLog()
    {
        var sender = new RecordingInputSender();
        var logger = new NullLoggerService { IsEnabled = true };
        using var service = new InputFeatureHarness(logger, sender);
        service.StartInputExecutorForTesting();

        try
        {
            // MaxInterval + jitter=0 makes the armed delay exact AND keeps the REAL successor timer's due
            // time (~250 ms) far beyond the synchronous test window (same discipline as
            // RapidFire_ClickCompletionRearmsOneSuccessor — a 25 ms successor could race the test thread).
            var profile = CreateRapidFireProfile();
            profile.RapidFire.IntervalMilliseconds = RapidFireSettings.MaxIntervalMilliseconds;
            service.ConfigureRapidFireForTesting(profile, foregroundGeneration: 1);

            service.HandleRapidFireLeftButtonForTesting(isDown: true);
            service.FireRapidFireTimerForTesting();
            service.HandleRapidFireLeftButtonForTesting(isDown: false);

            // ARMED: the first synthetic click is due one full interval after the physical press (the
            // deliberate no-immediate-click rule) — the line must state it with the resolved delay.
            var armed = Assert.Single(logger.Messages, m => m.StartsWith("Rapid Fire armed:"));
            Assert.Contains($"first synthetic click due in {RapidFireSettings.MaxIntervalMilliseconds} ms", armed);
            Assert.Contains($"interval={RapidFireSettings.MaxIntervalMilliseconds}", armed);
            Assert.Contains("jitter=0", armed);

            // FIRED: the timer's actual elapsed vs the delay it was armed for.
            var fired = Assert.Single(logger.Messages, m => m.StartsWith("Rapid Fire timer fired:"));
            Assert.Contains("elapsed=", fired);
            Assert.Contains($"armed delay={RapidFireSettings.MaxIntervalMilliseconds} ms", fired);
        }
        finally
        {
            service.HandleRapidFireLeftButtonForTesting(isDown: false);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_ProfileSwitch_PreservesArmForOwnerAndCancelsBurst()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var owner = CreateRapidFireProfile("Owner", RapidFireSettings.MaxIntervalMilliseconds);
            var other = CreateRapidFireProfile("Other", RapidFireSettings.MaxIntervalMilliseconds);
            ArmRapidFireViaToggle(service, owner, foregroundGeneration: 1);
            Assert.Equal(RapidFireArmStatus.Ready, service.GetRapidFireArmStatus());

            // Burst in flight: exactly one synthetic click lands.
            service.HandleRapidFireLeftButtonForTesting(isDown: true);
            service.FireRapidFireTimerForTesting();
            Assert.Single(sender.MouseClickThreadIds);

            // Focus leaves to another capable app: the press is cancelled (no successor), the arm is kept.
            SwitchRapidFireForeground(service, other, foregroundGeneration: 2, executable: "other.exe");
            service.FireRapidFireTimerForTesting();
            service.HandleRapidFireLeftButtonForTesting(isDown: false);
            Assert.Single(sender.MouseClickThreadIds);
            Assert.Equal(RapidFireArmStatus.ArmedNotReady, service.GetRapidFireArmStatus());

            // Focus returns to the owner: armed again with NO re-toggle, and a fresh press clicks.
            SwitchRapidFireForeground(service, owner, foregroundGeneration: 3, executable: "owner.exe");
            Assert.Equal(RapidFireArmStatus.Ready, service.GetRapidFireArmStatus());
            service.HandleRapidFireLeftButtonForTesting(isDown: true);
            service.FireRapidFireTimerForTesting();
            Assert.Equal(2, sender.MouseClickThreadIds.Count);
        }
        finally
        {
            service.HandleRapidFireLeftButtonForTesting(isDown: false);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_SameProfileRepublish_GrayThenReadyEventsFire()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateRapidFireProfile();
            ArmRapidFireViaToggle(service, profile, foregroundGeneration: 1);

            var armRaises = 0;
            var profileChanges = 0;
            service.RapidFireArmChanged += (_, _) => armRaises++;
            service.ActiveProfileChanged += (_, _) => profileChanges++;

            // Same-exe refocus: the watcher republishes a new generation ahead of the worker's
            // activation — armed, but not ready yet.
            service.SetForegroundIdentity(new IntPtr(0x101), 42u, "game.exe", 2);
            Assert.Equal(RapidFireArmStatus.ArmedNotReady, service.GetRapidFireArmStatus());
            Assert.Equal(1, armRaises);

            // Same-instance activation catch-up settles the generation. The raise is the wedge fix:
            // previously this branch bumped the generation silently and the status could stay
            // gray forever even though the arm was ready again.
            service.ActivateProfile(profile, 2);
            Assert.Equal(RapidFireArmStatus.Ready, service.GetRapidFireArmStatus());
            Assert.Equal(2, armRaises);
            Assert.Equal(0, profileChanges);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_ToggleInOtherCapableApp_RetargetsArm()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var first = CreateRapidFireProfile("First");
            var second = CreateRapidFireProfile("Second");
            service.ConfigureActiveProfileForTesting(first, foregroundGeneration: 1, altPressed: false);
            service.AdvancedModeEnabled = true;
            service.SetRapidFireToggleKey(Key.F8);
            PressRapidFireToggle(service, Key.F8);
            Assert.Equal(RapidFireArmStatus.Ready, service.GetRapidFireArmStatus());

            SwitchRapidFireForeground(service, second, foregroundGeneration: 2, executable: "second.exe");
            Assert.Equal(RapidFireArmStatus.ArmedNotReady, service.GetRapidFireArmStatus());

            // Toggle in the second capable app: the single owner re-targets.
            PressRapidFireToggle(service, Key.F8);
            Assert.Equal(RapidFireArmStatus.Ready, service.GetRapidFireArmStatus());

            // ...and toggling there disarms.
            PressRapidFireToggle(service, Key.F8);
            Assert.Equal(RapidFireArmStatus.Off, service.GetRapidFireArmStatus());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_ToggleOnDesktop_DisarmsStrandedArm()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var owner = CreateRapidFireProfile();
            ArmRapidFireViaToggle(service, owner, foregroundGeneration: 1);
            Assert.Equal(RapidFireArmStatus.Ready, service.GetRapidFireArmStatus());

            // Quitting the game while armed: stranded (not ready), with no eligible context to
            // retarget from — the toggle key is the primary off-switch for exactly this state.
            SwitchRapidFireForeground(service, profile: null, foregroundGeneration: 2, executable: "desktop");
            Assert.Equal(RapidFireArmStatus.ArmedNotReady, service.GetRapidFireArmStatus());

            var armRaises = 0;
            service.RapidFireArmChanged += (_, _) => armRaises++;
            PressRapidFireToggle(service, Key.F8);
            Assert.Equal(RapidFireArmStatus.Off, service.GetRapidFireArmStatus());
            Assert.Equal(1, armRaises);

            // A disarmed arm never silently resumes when the owner is refocused later.
            SwitchRapidFireForeground(service, owner, foregroundGeneration: 3, executable: "owner.exe");
            Assert.Equal(RapidFireArmStatus.Off, service.GetRapidFireArmStatus());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_ToggleInRfIneligibleProfile_DisarmsStrandedArm()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var owner = CreateRapidFireProfile();
            var ineligible = new Profile { Name = "NoRapidFire", Executable = "norf.exe" };
            ArmRapidFireViaToggle(service, owner, foregroundGeneration: 1);

            SwitchRapidFireForeground(service, ineligible, foregroundGeneration: 2, executable: "norf.exe");
            Assert.Equal(RapidFireArmStatus.ArmedNotReady, service.GetRapidFireArmStatus());

            // Settled, enabled, but Rapid Fire disabled: same stranded-arm escape hatch.
            PressRapidFireToggle(service, Key.F8);
            Assert.Equal(RapidFireArmStatus.Off, service.GetRapidFireArmStatus());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_ToggleDuringGenerationMismatch_FailsClosed()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var owner = CreateRapidFireProfile();
            var other = CreateRapidFireProfile("Other");
            ArmRapidFireViaToggle(service, owner, foregroundGeneration: 1);

            var armRaises = 0;
            service.RapidFireArmChanged += (_, _) => armRaises++;

            // Publication ahead of activation: the toggle must fail closed (no re-arm, no disarm).
            service.SetForegroundIdentity(new IntPtr(0x201), 43u, "other.exe", 2);
            Assert.Equal(RapidFireArmStatus.ArmedNotReady, service.GetRapidFireArmStatus());
            PressRapidFireToggle(service, Key.F8);
            Assert.Equal(RapidFireArmStatus.ArmedNotReady, service.GetRapidFireArmStatus());
            Assert.Equal(1, armRaises); // only the identity-publish raise

            // Once activation catches up, retargeting works normally.
            service.ActivateProfile(other, 2);
            PressRapidFireToggle(service, Key.F8);
            Assert.Equal(RapidFireArmStatus.Ready, service.GetRapidFireArmStatus());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_RemovedOwnerProfile_DisarmsStickyArm()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var owner = CreateRapidFireProfile();
            ArmRapidFireViaToggle(service, owner, foregroundGeneration: 1);
            SwitchRapidFireForeground(service, CreateRapidFireProfile("Other"), foregroundGeneration: 2, executable: "other.exe");
            Assert.Equal(RapidFireArmStatus.ArmedNotReady, service.GetRapidFireArmStatus());

            service.ReconcileProfileSettings(owner, ProfileChangeKind.Removed);
            Assert.Equal(RapidFireArmStatus.Off, service.GetRapidFireArmStatus());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_MasterDisabledOwner_DisarmsStickyArm()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var owner = CreateRapidFireProfile();
            var other = CreateRapidFireProfile("Other");
            ArmRapidFireViaToggle(service, owner, foregroundGeneration: 1);
            SwitchRapidFireForeground(service, other, foregroundGeneration: 2, executable: "other.exe");

            owner.IsEnabled = false;
            service.ReconcileProfileSettings(owner, ProfileChangeKind.Master);
            Assert.Equal(RapidFireArmStatus.Off, service.GetRapidFireArmStatus());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_IdentityEditOfOwner_DisarmsStickyArm()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var owner = CreateRapidFireProfile();
            ArmRapidFireViaToggle(service, owner, foregroundGeneration: 1);
            SwitchRapidFireForeground(service, CreateRapidFireProfile("Other"), foregroundGeneration: 2, executable: "other.exe");

            // Changing the executable changes what "its own app" means — the owner is invalidated.
            service.ReconcileProfileSettings(owner, ProfileChangeKind.Identity);
            Assert.Equal(RapidFireArmStatus.Off, service.GetRapidFireArmStatus());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_ActiveOwnerHardDeactivation_DisarmsAndRaises()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var owner = CreateRapidFireProfile();
            ArmRapidFireViaToggle(service, owner, foregroundGeneration: 1);
            Assert.Equal(RapidFireArmStatus.Ready, service.GetRapidFireArmStatus());

            var armRaises = 0;
            service.RapidFireArmChanged += (_, _) => armRaises++;

            // Exercises the hard-deactivate branch (owner still ACTIVE) through the in-lock arm
            // preservation and the post-lock ReleaseRapidFireOwnedBy handoff.
            service.ReconcileProfileSettings(owner, ProfileChangeKind.Removed);
            Assert.Equal(RapidFireArmStatus.Off, service.GetRapidFireArmStatus());
            Assert.Equal(1, armRaises);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_ForeignOwnerSurvivesActiveHardDeactivation()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var owner = CreateRapidFireProfile();
            var other = CreateRapidFireProfile("Other");
            ArmRapidFireViaToggle(service, owner, foregroundGeneration: 1);
            SwitchRapidFireForeground(service, other, foregroundGeneration: 2, executable: "other.exe");

            // Hard-deactivating the ACTIVE profile must not touch a FOREIGN arm.
            service.ReconcileProfileSettings(other, ProfileChangeKind.Removed);
            Assert.Equal(RapidFireArmStatus.ArmedNotReady, service.GetRapidFireArmStatus());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_RapidFireEditOfNonOwnerProfile_KeepsArm()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var owner = CreateRapidFireProfile();
            var other = CreateRapidFireProfile("Other"); // RF-ENABLED and active
            ArmRapidFireViaToggle(service, owner, foregroundGeneration: 1);
            SwitchRapidFireForeground(service, other, foregroundGeneration: 2, executable: "other.exe");

            // An RF-config edit of the ACTIVE profile only releases state the EDITED profile owns.
            service.ReconcileProfileSettings(other, ProfileChangeKind.RapidFire);
            Assert.Equal(RapidFireArmStatus.ArmedNotReady, service.GetRapidFireArmStatus());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_AdvancedModeOff_DisarmsStickyArm()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            ArmRapidFireViaToggle(service, CreateRapidFireProfile(), foregroundGeneration: 1);
            Assert.Equal(RapidFireArmStatus.Ready, service.GetRapidFireArmStatus());

            service.AdvancedModeEnabled = false;
            Assert.Equal(RapidFireArmStatus.Off, service.GetRapidFireArmStatus());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_ReleaseForegroundState_PreservesArm()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var owner = CreateRapidFireProfile();
            ArmRapidFireViaToggle(service, owner, foregroundGeneration: 1);

            service.HandleRapidFireLeftButtonForTesting(isDown: true);
            service.ReleaseForegroundState();

            // The PRESS is cancelled — a stale timer callback cannot click...
            service.FireRapidFireTimerForTesting();
            Assert.Empty(sender.MouseClickThreadIds);

            // ...but the ARM survives (separate call-site argument from Activate/Deactivate).
            Assert.Equal(RapidFireArmStatus.Ready, service.GetRapidFireArmStatus());
        }
        finally
        {
            service.HandleRapidFireLeftButtonForTesting(isDown: false);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFire_Stop_DisarmsStickyArm()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            ArmRapidFireViaToggle(service, CreateRapidFireProfile(), foregroundGeneration: 1);
            Assert.Equal(RapidFireArmStatus.Ready, service.GetRapidFireArmStatus());

            var armRaises = 0;
            service.RapidFireArmChanged += (_, _) => armRaises++;

            // Stop's default-parameter safety contract: full release (the arm does not survive a
            // stopped hook service). The raise is delivered after _profileLock closes.
            service.Stop();
            Assert.Equal(RapidFireArmStatus.Off, service.GetRapidFireArmStatus());
            Assert.Equal(1, armRaises);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void Launcher_DisabledWhileHeld_StillConsumesAndClearsKeyUp()
    {
        var sender = new RecordingInputSender();
        using var service = new InputFeatureHarness(new NullLoggerService(), sender);
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

    private static Profile CreateAltKeyboardProfile(int holdThresholdMs)
    {
        return new Profile
        {
            Name = "Game",
            Executable = "game.exe",
            AltKeyboard =
            {
                IsEnabled = true,
                HoldThresholdMilliseconds = holdThresholdMs,
                Bindings = new Dictionary<Key, AltKeyboardBinding>
                {
                    [Key.Q] = new()
                    {
                        TapKey = Key.A,
                        HoldKey = Key.B
                    }
                }
            }
        };
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

    private static Profile CreateRapidFireProfile(
        string name = "Game",
        int intervalMs = RapidFireSettings.MinIntervalMilliseconds)
    {
        return new Profile
        {
            Name = name,
            Executable = $"{name.ToLowerInvariant()}.exe",
            RapidFire =
            {
                IsEnabled = true,
                IntervalMilliseconds = intervalMs,
                JitterMilliseconds = 0
            }
        };
    }

    // The Rapid Fire toggle latches on key-down and only re-arms after the key-up — a full
    // physical press is always down THEN up.
    private static void PressRapidFireToggle(InputFeatureHarness service, Key key)
    {
        service.HandleRapidFireToggleForTesting(key, isDown: true);
        service.HandleRapidFireToggleForTesting(key, isDown: false);
    }

    // Arms via the REAL toggle path (not ConfigureRapidFireForTesting): settled active profile,
    // Advanced Mode on, key assigned, one physical toggle press.
    private static void ArmRapidFireViaToggle(InputFeatureHarness service, Profile profile, long foregroundGeneration)
    {
        service.ConfigureActiveProfileForTesting(profile, foregroundGeneration, altPressed: false);
        service.AdvancedModeEnabled = true;
        service.SetRapidFireToggleKey(Key.F8);
        PressRapidFireToggle(service, Key.F8);
    }

    // Foreground switch in PRODUCTION order: the watcher publishes identity (new generation)
    // BEFORE the worker activates/deactivates — the status dot depends on that publication raising
    // ahead of activation.
    private static void SwitchRapidFireForeground(
        InputFeatureHarness service,
        Profile? profile,
        long foregroundGeneration,
        string executable)
    {
        service.SetForegroundIdentity(
            new IntPtr(0x100 + (int)foregroundGeneration),
            1000u + (uint)foregroundGeneration,
            executable,
            foregroundGeneration);
        if (profile is null)
        {
            service.DeactivateProfile(foregroundGeneration);
        }
        else
        {
            service.ActivateProfile(profile, foregroundGeneration);
        }
    }

}
