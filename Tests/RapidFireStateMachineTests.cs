using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using sWinShortcuts.Utilities;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class RapidFireStateMachineTests
{
    [Fact]
    public void Timer_LateWakeBeforeFirstDeadline_PreservesScheduledClick()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(RapidFireStateMachine).GetMethod("OnTimerFired", flags)!.Invoke(rapidFire, null);
        var timer = (System.Threading.Timer)typeof(RapidFireStateMachine)
            .GetField("_timer", flags)!.GetValue(rapidFire)!;
        try
        {
            // A hook wake delayed until after worker initialization replaces the one-shot deadline.
            timer.Change(0, Timeout.Infinite);
            Assert.True(sender.MouseEntered.Wait(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        }
    }

    [Fact]
    public void FirstPress_ReleasesPhysicalPressBeforeFirstSyntheticClick()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        try
        {
            rapidFire.FireTimerForTesting();

            // The passed-through physical DOWN is released so the first synthetic DOWN is a real edge.
            Assert.Equal((sWinShortcuts.Models.MouseButton.Left, false, false), Assert.Single(sender.MouseTransitions));
            Assert.Single(sender.MouseClickThreadIds);
        }
        finally
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        }
    }

    [Fact]
    public void FirstPress_FirstClickDeadlineIsAnchoredToPhysicalPress()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        try
        {
            // The hook kick runs the real timer: release after a click hold, then arm the first click.
            Assert.True(SpinWait.SpinUntil(() => !sender.MouseTransitions.IsEmpty, TimeSpan.FromSeconds(2)));
            Assert.True(SpinWait.SpinUntil(() => ArmedDelay(rapidFire) > RapidFireStateMachine.HOLD_MAX_MS,
                TimeSpan.FromSeconds(2)));

            // Time already spent holding the physical press counts toward the first interval.
            var delay = ArmedDelay(rapidFire);
            Assert.InRange(delay, RapidFireStateMachine.RELEASE_GAP_MIN_MS,
                profile.RapidFire.IntervalMilliseconds - RapidFireStateMachine.HOLD_MIN_MS);
        }
        finally
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        }
    }

    [Fact]
    public void FirstPress_TapReleasedBeforeHold_SendsNothing()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);

        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        rapidFire.FireTimerForTesting();

        Assert.Empty(sender.MouseTransitions);
        Assert.Empty(sender.MouseClickThreadIds);
    }

    [Fact]
    public void FirstPress_ReleaseNotDelivered_SendsNoClicksAndIsNotReady()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender { MouseResult = (_, _, _) => false };
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        try
        {
            rapidFire.FireTimerForTesting();

            // The first synthetic DOWN would land on the still-held physical press, so none is sent.
            Assert.Single(sender.MouseTransitions);
            Assert.Empty(sender.MouseClickThreadIds);
            Assert.NotEqual(TIMER_ARMED, TimerState(rapidFire));
            Assert.Equal(0L, OwedUpGeneration(rapidFire));
            Assert.Equal(RapidFireArmStatus.ArmedNotReady, rapidFire.GetStatus());
        }
        finally
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        }
    }

    [Fact]
    public void Click_DownNotDelivered_StopsBurstWithoutOwedRelease()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender { ClickResult = () => LeftClickResult.DownFailed };
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        try
        {
            rapidFire.FireTimerForTesting();

            Assert.Single(sender.MouseClickThreadIds);
            Assert.NotEqual(TIMER_ARMED, TimerState(rapidFire));
            Assert.Equal(0L, OwedUpGeneration(rapidFire));
            Assert.Single(sender.MouseTransitions);
            Assert.Equal(RapidFireArmStatus.ArmedNotReady, rapidFire.GetStatus());
        }
        finally
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        }
    }

    [Fact]
    public void Click_UpNotDeliveredWhileHeld_OwesReleaseUntilPhysicalUp()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender { ClickResult = () => LeftClickResult.UpFailed };
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);

        rapidFire.FireTimerForTesting();

        // Still physically held: the user's own UP will release the button, so no synthetic UP yet.
        Assert.Single(sender.MouseClickThreadIds);
        Assert.NotEqual(TIMER_ARMED, TimerState(rapidFire));
        Assert.NotEqual(0L, OwedUpGeneration(rapidFire));
        Assert.Single(sender.MouseTransitions);

        rapidFire.HandleLeftButton(isDown: false, allowStart: false);

        Assert.Equal(0L, OwedUpGeneration(rapidFire));
        Assert.Single(sender.MouseTransitions);
    }

    [Fact]
    public void Click_UpNotDeliveredAfterPhysicalRelease_SendsOwedRelease()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        sender.ClickResult = () =>
        {
            // The physical UP races the click: nothing later will release our synthetic DOWN.
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
            return LeftClickResult.UpFailed;
        };
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);

        rapidFire.FireTimerForTesting();

        Assert.Equal(
            [(sWinShortcuts.Models.MouseButton.Left, false, false), (sWinShortcuts.Models.MouseButton.Left, false, false)],
            sender.MouseTransitions.ToArray());
        Assert.Equal(0L, OwedUpGeneration(rapidFire));
    }

    [Fact]
    public void Click_UpNotDeliveredThenNewPhysicalPress_OwedReleaseCannotCutNewPress()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        sender.ClickResult = () =>
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
            rapidFire.HandleLeftButton(isDown: true, allowStart: false);
            return LeftClickResult.UpFailed;
        };
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        try
        {
            rapidFire.FireTimerForTesting();

            // Only the initial physical-press release; the new press is left alone.
            Assert.Single(sender.MouseTransitions);
        }
        finally
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        }

        Assert.Equal(0L, OwedUpGeneration(rapidFire));
        Assert.Single(sender.MouseTransitions);
    }

    [Fact]
    public void Click_OwedReleaseNotDelivered_RetainsDebtUntilNextPhysicalClick()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var ups = 0;
        var sender = new RecordingInputSender
        {
            // The initial physical-press release succeeds; the owed cleanup UP fails.
            MouseResult = (_, isDown, _) => isDown || Interlocked.Increment(ref ups) == 1
        };
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        sender.ClickResult = () =>
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
            return LeftClickResult.UpFailed;
        };
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);

        rapidFire.FireTimerForTesting();

        Assert.Equal(2, sender.MouseTransitions.Count);
        Assert.NotEqual(0L, OwedUpGeneration(rapidFire));

        rapidFire.HandleLeftButton(isDown: true, allowStart: false);
        Assert.Equal(0L, OwedUpGeneration(rapidFire));
        rapidFire.HandleLeftButton(isDown: false, allowStart: false);
    }

    [Fact]
    public void DeliveryFailure_RaisesStatusChangeAndNextDeliveredPressRestoresReady()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var fail = true;
        var sender = new RecordingInputSender
        {
            ClickResult = () => Volatile.Read(ref fail) ? LeftClickResult.DownFailed : LeftClickResult.Sent
        };
        var statusChanges = 0;
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = new RapidFireStateMachine(runtime, sender, random, new NullLoggerService(),
            new object(), () => Interlocked.Increment(ref statusChanges));
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);

        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        rapidFire.FireTimerForTesting();
        rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        Assert.Equal(RapidFireArmStatus.ArmedNotReady, rapidFire.GetStatus());
        Assert.Equal(1, Volatile.Read(ref statusChanges));

        Volatile.Write(ref fail, false);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        try
        {
            rapidFire.FireTimerForTesting();
            Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());
            Assert.Equal(2, Volatile.Read(ref statusChanges));
        }
        finally
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        }
    }

    [Fact]
    public void RightButtonGate_RightUpAtLeftPress_SendsNothingEvenIfRightPressedLater()
    {
        var (profile, sender, random, rapidFire, right) = CreateGated(rightHeld: false);
        using var _r = random;
        using var _f = rapidFire;
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        try
        {
            rapidFire.FireTimerForTesting();
            right.Value = true;
            rapidFire.FireTimerForTesting();

            // Ordinary held fire: the physical press is untouched and nothing synthetic is sent.
            Assert.Empty(sender.MouseTransitions);
            Assert.Empty(sender.MouseClickThreadIds);
            Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());
        }
        finally
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        }
    }

    [Fact]
    public void RightButtonGate_RightHeldAtLeftPress_ClicksNormally()
    {
        var (_, sender, random, rapidFire, _) = CreateGated(rightHeld: true);
        using var _r = random;
        using var _f = rapidFire;
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        try
        {
            rapidFire.FireTimerForTesting();

            Assert.Single(sender.MouseTransitions);
            Assert.Single(sender.MouseClickThreadIds);
        }
        finally
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        }
    }

    [Fact]
    public void RightButtonGate_RightReleasedBeforePhysicalRelease_LeavesHoldUntouched()
    {
        var (_, sender, random, rapidFire, right) = CreateGated(rightHeld: true);
        using var _r = random;
        using var _f = rapidFire;
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        try
        {
            right.Value = false;
            rapidFire.HandleRightButtonReleased();
            rapidFire.FireTimerForTesting();

            Assert.Empty(sender.MouseTransitions);
            Assert.Empty(sender.MouseClickThreadIds);
        }
        finally
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        }
    }

    [Fact]
    public void RightButtonGate_RightReleasedAndRepressedBetweenWakes_DoesNotResumeUntilFreshLeftPress()
    {
        var (_, sender, random, rapidFire, right) = CreateGated(rightHeld: true);
        using var _r = random;
        using var _f = rapidFire;
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        rapidFire.FireTimerForTesting();
        Assert.Single(sender.MouseClickThreadIds);

        // UP then DOWN before the next wake: a boolean check alone would let the burst continue.
        right.Value = false;
        rapidFire.HandleRightButtonReleased();
        right.Value = true;
        rapidFire.FireTimerForTesting();
        Assert.Single(sender.MouseClickThreadIds);

        rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        try
        {
            rapidFire.FireTimerForTesting();
            Assert.Equal(2, sender.MouseClickThreadIds.Count);
        }
        finally
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        }
    }

    [Fact]
    public void RightButtonGate_Off_IgnoresRightButton()
    {
        var (profile, sender, random, rapidFire, right) = CreateGated(rightHeld: false);
        using var _r = random;
        using var _f = rapidFire;
        profile.RapidFire.RequireRightButton = false;
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        try
        {
            rapidFire.FireTimerForTesting();
            rapidFire.HandleRightButtonReleased();

            Assert.Single(sender.MouseClickThreadIds);
            Assert.Equal(TIMER_ARMED, TimerState(rapidFire));
        }
        finally
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        }
    }

    private sealed class RightButtonState
    {
        public volatile bool Value;
    }

    private static (Profile Profile, RecordingInputSender Sender, ThreadLocal<Random> Random,
        RapidFireStateMachine RapidFire, RightButtonState Right) CreateGated(bool rightHeld)
    {
        var profile = RapidFireProfile();
        profile.RapidFire.RequireRightButton = true;
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        var random = new ThreadLocal<Random>(() => new Random(1));
        var right = new RightButtonState { Value = rightHeld };
        var rapidFire = new RapidFireStateMachine(runtime, sender, random, new NullLoggerService(),
            new object(), isRightButtonHeld: () => right.Value);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        return (profile, sender, random, rapidFire, right);
    }

    [Theory]
    [InlineData(15, 0, 15)]
    [InlineData(90, 30.2, 60)]
    [InlineData(90, 120, 1)]
    public void CalculatePressRelativeDelay_SubtractsTimeSincePress(int target, double sincePress, int expected)
    {
        Assert.Equal(expected, RapidFireStateMachine.CalculatePressRelativeDelay(target, sincePress));
    }

    [Fact]
    public async Task Timer_NewPressDuringOldJitterCalculation_PreservesNewPressSchedule()
    {
        var profile = RapidFireProfile();
        profile.RapidFire.JitterMilliseconds = 20;
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var oldScheduleEntered = new ManualResetEventSlim();
        using var releaseOldSchedule = new ManualResetEventSlim();
        var hookThread = Environment.CurrentManagedThreadId;
        int blockOnce = 1;
        using var random = new ThreadLocal<Random>(() => new JitterGateRandom(() =>
        {
            if (Environment.CurrentManagedThreadId != hookThread && sender.MouseClickThreadIds.Count == 1 &&
                Interlocked.Exchange(ref blockOnce, 0) != 0)
            {
                oldScheduleEntered.Set();
                Assert.True(releaseOldSchedule.Wait(TimeSpan.FromSeconds(5)));
            }
        }));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        var first = Task.Factory.StartNew(rapidFire.FireTimerForTesting, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(oldScheduleEntered.Wait(TimeSpan.FromSeconds(2)));
            rapidFire.HandleLeftButton(isDown: false, allowStart: true);
            rapidFire.HandleLeftButton(isDown: true, allowStart: true);
            releaseOldSchedule.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(2));

            rapidFire.FireTimerForTesting();
            Assert.Equal(2, sender.MouseClickThreadIds.Count);
        }
        finally
        {
            releaseOldSchedule.Set();
            rapidFire.HandleLeftButton(isDown: false, allowStart: false);
            await first.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timer_NewPressDuringBlockedClick_SerializesAndRechecksRelease(bool releaseNewPress)
    {
        var profile = RapidFireProfile();
        profile.RapidFire.IntervalMilliseconds = RapidFireSettings.MaxIntervalMilliseconds;
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender(blockMouse: true);
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        using var secondStarted = new ManualResetEventSlim();
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        Task? first = null;
        Task? second = null;
        try
        {
            rapidFire.HandleLeftButton(isDown: true, allowStart: true);
            first = Task.Factory.StartNew(rapidFire.FireTimerForTesting, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(sender.MouseEntered.Wait(TimeSpan.FromSeconds(2)));
            rapidFire.HandleLeftButton(isDown: false, allowStart: true);
            rapidFire.HandleLeftButton(isDown: true, allowStart: true);
            second = Task.Factory.StartNew(() =>
            {
                secondStarted.Set();
                rapidFire.FireTimerForTesting();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(2)));
            // The first sender call remains blocked throughout the overlap window.
            await Task.Delay(100);
            Assert.Single(sender.MouseClickThreadIds);
            if (releaseNewPress) rapidFire.HandleLeftButton(isDown: false, allowStart: true);
            sender.ReleaseMouse.Set();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
            rapidFire.HandleLeftButton(isDown: false, allowStart: true);
            Assert.Equal(releaseNewPress ? 1 : 2, sender.MouseClickThreadIds.Count);
        }
        finally
        {
            rapidFire.HandleLeftButton(isDown: false, allowStart: true);
            sender.ReleaseMouse.Set();
            if (first is not null) await first.WaitAsync(TimeSpan.FromSeconds(2));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public void Toggle_TypematicFiresOnceAndReassignmentDisarms()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);

        Assert.False(rapidFire.SetToggleKey(Key.F8));
        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());

        Assert.True(rapidFire.SetToggleKey(Key.F9));
        Assert.Equal(RapidFireArmStatus.Off, rapidFire.GetStatus());
        Assert.False(rapidFire.HandleToggleKey(Vk(Key.F8), isKeyDown: true, isKeyUp: false));
        Assert.True(Toggle(rapidFire, Key.F9));
        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());
    }

    [Fact]
    public void Toggle_HeldAcrossRestartRequiresFreshDown()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.SetToggleKey(Key.F8);

        runtime.SetRunning(false);
        rapidFire.Release(preservePhysicalPairing: false);
        rapidFire.SeedTogglePhysicalState(vk => vk == Vk(Key.F8));
        runtime.SetRunning(true);

        Assert.False(rapidFire.HandleToggleKey(Vk(Key.F8), isKeyDown: true, isKeyUp: false));
        Assert.Equal(RapidFireArmStatus.Off, rapidFire.GetStatus());

        Assert.False(rapidFire.HandleToggleKey(Vk(Key.F8), isKeyDown: false, isKeyUp: true));
        Assert.True(rapidFire.HandleToggleKey(Vk(Key.F8), isKeyDown: true, isKeyUp: false));
        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());
    }

    [Fact]
    public void Toggle_RetargetsEligibleOwnerAndDisarmsStrandedArm()
    {
        var first = RapidFireProfile("First");
        var second = RapidFireProfile("Second");
        var runtime = RunningRuntime(first);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.SetToggleKey(Key.F8);

        Assert.True(Toggle(rapidFire, Key.F8));
        Publish(runtime, second, foregroundGeneration: 2);
        Assert.Equal(RapidFireArmStatus.ArmedNotReady, rapidFire.GetStatus());

        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());

        Publish(runtime, profile: null, foregroundGeneration: 3);
        Assert.Equal(RapidFireArmStatus.ArmedNotReady, rapidFire.GetStatus());
        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.Equal(RapidFireArmStatus.Off, rapidFire.GetStatus());
    }

    [Fact]
    public void ForegroundReleaseAndSameProfileRepublish_PreserveStickyArmAndStatusTransitions()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);

        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        rapidFire.CancelPress();
        rapidFire.FireTimerForTesting();
        Assert.Empty(sender.MouseClickThreadIds);
        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());

        runtime.SetForegroundIdentity(IntPtr.Zero, 0, profile.NormalizedExecutable, foregroundGeneration: 2);
        Assert.Equal(RapidFireArmStatus.ArmedNotReady, rapidFire.GetStatus());
        runtime.SetActiveProfile(profile, foregroundGeneration: 2);
        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());
    }

    [Fact]
    public void StaleTimerAndOwnerProfileEdit_CannotClickAndDisarmOwner()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);

        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        rapidFire.CancelPress();
        rapidFire.FireTimerForTesting();
        Assert.Empty(sender.MouseClickThreadIds);

        rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        profile.RapidFire.IsEnabled = false;
        Assert.True(rapidFire.ReleaseOwnedBy(profile));
        rapidFire.FireTimerForTesting();

        Assert.Empty(sender.MouseClickThreadIds);
        Assert.Equal(RapidFireArmStatus.Off, rapidFire.GetStatus());
    }

    [Fact]
    public void AdvancedModeOff_BlocksPendingClickAndMakesArmUnavailable()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);

        runtime.SetAdvancedMode(false);
        Assert.Equal(RapidFireArmStatus.Off, rapidFire.GetStatus());
        rapidFire.FireTimerForTesting();

        Assert.Empty(sender.MouseClickThreadIds);
        Assert.True(rapidFire.Release(preservePhysicalPairing: true));
    }

    [Fact]
    public async Task DisposalWhileClickBlocked_DoesNotScheduleSuccessor()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender(blockMouse: true);
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        var fire = Task.Run(rapidFire.FireTimerForTesting);

        try
        {
            Assert.True(sender.MouseEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(runtime.TryBeginDispose());
            rapidFire.Dispose();
            sender.ReleaseMouse.Set();
            await fire.WaitAsync(TimeSpan.FromSeconds(2));

            rapidFire.FireTimerForTesting();
            Assert.Single(sender.MouseClickThreadIds);
        }
        finally
        {
            sender.ReleaseMouse.Set();
        }
    }

    [Theory]
    [InlineData(25, 0, 25)]
    [InlineData(25, 24.1, 1)]
    [InlineData(25, 30, 25)]
    public void CalculateSuccessorDelay_CompensatesOnlyWithinInterval(
        int target,
        double elapsed,
        int expected)
    {
        Assert.Equal(expected, RapidFireStateMachine.CalculateSuccessorDelay(target, elapsed));
    }

    [Fact]
    public void Disarm_ToggleOffAndReassignment_LogTheirReasons()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var logger = new NullLoggerService { IsEnabled = true };
        using var rapidFire = Create(runtime, sender, random, logger);

        // Reassignment before anything is armed is a no-op release: nothing to report.
        Assert.False(rapidFire.SetToggleKey(Key.F8));
        Assert.Empty(logger.Messages);

        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.Equal($"Rapid Fire armed for profile: {profile.Name}", Assert.Single(logger.Messages));

        // Toggle-off is a real disarm and carries its reason.
        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.Equal("Rapid Fire disarmed (toggle-off)", logger.Messages[^1]);
        Assert.Single(logger.Messages, m => m == "Rapid Fire disarmed (toggle-off)");

        logger.Messages.Clear();
        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.True(rapidFire.SetToggleKey(Key.F9));
        Assert.Equal("Rapid Fire disarmed (toggle key reassigned)", logger.Messages[^1]);
        Assert.Single(logger.Messages, m => m == "Rapid Fire disarmed (toggle key reassigned)");
    }

    [Fact]
    public void Disarm_OwnerReleaseLogsReason_NoOpReleasesStaySilent()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var logger = new NullLoggerService { IsEnabled = true };
        using var rapidFire = Create(runtime, sender, random, logger);

        // Disarmed machine: neither a direct release nor an owner-scoped release logs.
        Assert.False(rapidFire.Release(preservePhysicalPairing: true));
        Assert.False(rapidFire.ReleaseOwnedBy(profile));
        Assert.Empty(logger.Messages);

        rapidFire.SetToggleKey(Key.F8);
        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.True(rapidFire.ReleaseOwnedBy(profile));
        Assert.Single(logger.Messages, m => m == "Rapid Fire disarmed (owner settings changed/removed)");
    }

    [Fact]
    public void Release_LoggingDisabled_DoesNotAllocate()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, new RecordingInputSender(), random);
        rapidFire.SetToggleKey(Key.F8);

        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.True(rapidFire.Release(preservePhysicalPairing: true, reason: "warmup"));
        Assert.True(Toggle(rapidFire, Key.F8));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var released = rapidFire.Release(preservePhysicalPairing: true, reason: "test");
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(released);
        Assert.Equal(before, after);
    }

    private sealed class JitterGateRandom(Action beforeJitter) : Random(1)
    {
        public override int Next(int maxValue)
        {
            beforeJitter();
            return base.Next(maxValue);
        }
    }

    private static RapidFireStateMachine Create(
        InputRuntimeState runtime,
        IInputSender sender,
        ThreadLocal<Random> random,
        NullLoggerService? logger = null) =>
        new(runtime, sender, random, logger ?? new NullLoggerService(), new object());

    private static Profile RapidFireProfile(string name = "Game")
    {
        return new Profile
        {
            Name = name,
            Executable = $"{name.ToLowerInvariant()}.exe",
            RapidFire =
            {
                IsEnabled = true,
                IntervalMilliseconds = RapidFireSettings.MaxIntervalMilliseconds,
                JitterMilliseconds = 0
            }
        };
    }

    private static InputRuntimeState RunningRuntime(Profile profile)
    {
        var runtime = new InputRuntimeState(FakeAutoRunTransport.MatchingForeground());
        runtime.SetRunning(true);
        runtime.SetAdvancedMode(true);
        Publish(runtime, profile, foregroundGeneration: 1);
        return runtime;
    }

    private static void Publish(
        InputRuntimeState runtime,
        Profile? profile,
        long foregroundGeneration)
    {
        runtime.SetActiveProfile(profile, foregroundGeneration);
        runtime.SetForegroundIdentity(
            (IntPtr)100,
            42,
            profile?.NormalizedExecutable,
            foregroundGeneration);
    }

    private static bool Toggle(RapidFireStateMachine rapidFire, Key key)
    {
        var virtualKey = Vk(key);
        var changed = rapidFire.HandleToggleKey(virtualKey, isKeyDown: true, isKeyUp: false);
        Assert.False(rapidFire.HandleToggleKey(virtualKey, isKeyDown: true, isKeyUp: false));
        Assert.False(rapidFire.HandleToggleKey(virtualKey, isKeyDown: false, isKeyUp: true));
        return changed;
    }

    private static int Vk(Key key) => KeyInteropUtilities.ToVirtualKey(key);

    private const int TIMER_ARMED = 1;

    private static int TimerState(RapidFireStateMachine rapidFire) =>
        (int)typeof(RapidFireStateMachine)
            .GetField("_timerState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(rapidFire)!;

    private static long OwedUpGeneration(RapidFireStateMachine rapidFire) =>
        (long)typeof(RapidFireStateMachine)
            .GetField("_owedUpGeneration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(rapidFire)!;

    private static int ArmedDelay(RapidFireStateMachine rapidFire) =>
        (int)typeof(RapidFireStateMachine)
            .GetField("_armedDelayMs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(rapidFire)!;
}
