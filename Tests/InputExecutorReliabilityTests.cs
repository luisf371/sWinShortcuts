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

        // Release is never absorbed; it clears the edge latch without cancelling Auto-Run.
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
    public void AutoRunPhysicalW_Handoff_ConsumesRepeatAndFocusedReleaseButNextPressCancels()
    {
        var physicallyDown = true;
        var handoffActive = true;

        // A held W repeat remains down and is consumed while the game owns the handoff.
        Assert.False(InputHookService.ApplyAutoRunPhysicalKeyEvent(
            ref physicallyDown,
            isKeyDown: true,
            isKeyUp: false));
        Assert.True(InputHookService.ShouldConsumeAutoRunPhysicalWHandoff(handoffActive, targetFocused: true));

        // The physical UP clears the handoff; Auto-Run transfers to its synthetic W before consuming it.
        Assert.False(InputHookService.ApplyAutoRunPhysicalKeyEvent(
            ref physicallyDown,
            isKeyDown: false,
            isKeyUp: true));
        handoffActive = false;
        Assert.False(InputHookService.ShouldConsumeAutoRunPhysicalWHandoff(handoffActive, targetFocused: true));
        Assert.False(InputHookService.ShouldConsumeAutoRunPhysicalWHandoff(handoffActive: true, targetFocused: false));

        // A genuinely new W press is fresh again and must remain available to cancel Auto-Run.
        Assert.True(InputHookService.ApplyAutoRunPhysicalKeyEvent(
            ref physicallyDown,
            isKeyDown: true,
            isKeyUp: false));
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

    private sealed class RecordingInputSender(
        bool blockDummy = false,
        bool failFirstDown = false) : IInputSender
    {
        private readonly bool _blockDummy = blockDummy;
        private int _failNextDown = failFirstDown ? 1 : 0;

        public ConcurrentQueue<(Key Key, bool IsDown, int ThreadId)> Transitions { get; } = new();

        public ManualResetEventSlim DummyEntered { get; } = new(false);

        public ManualResetEventSlim ReleaseDummy { get; } = new(false);

        public ConcurrentQueue<int> DummyThreadIds { get; } = new();

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

        public bool SendDummyKey()
        {
            DummyThreadIds.Enqueue(Environment.CurrentManagedThreadId);
            DummyEntered.Set();
            return !_blockDummy || ReleaseDummy.Wait(TimeSpan.FromSeconds(2));
        }
    }
}
