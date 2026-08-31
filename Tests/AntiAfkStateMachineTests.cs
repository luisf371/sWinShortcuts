using System.Diagnostics;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class AntiAfkStateMachineTests
{
    [Fact]
    public void Tick_ForcedPostDenied_LogsAccessDeniedAndStopsRipple()
    {
        long timestamp = 0;
        uint tick = 0;
        var logger = new NullLoggerService { IsEnabled = true };
        var (machine, _, transport, _, profile, runtime) = CreateMachine(
            () => timestamp,
            () => tick,
            logger);
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Forced;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            transport.FailNextPost(error: 5);
            tick = 60_000;

            machine.Tick();

            Assert.Single(transport.Posts);
            Assert.Contains(
                "Anti-AFK background target captured: hwnd=0x64 pid=7",
                logger.Messages);
            Assert.Contains(
                "Anti-AFK background post failed: Win32 error 5 (access denied; run sWinShortcuts at the target's integrity level)",
                logger.Messages);

            transport.ProcessIds[(IntPtr)100] = 9;
            machine.Tick();

            Assert.Contains(
                "Anti-AFK background target invalid: hwnd=0x64 expected-pid=7",
                logger.Messages);
        }
    }

    [Fact]
    public void Tick_AtExactInterval_EnqueuesWasdSequence()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, queue, _, _, _, _) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            machine.Tick();

            var command = Assert.Single(queue.Commands);
            Assert.Equal(InputCommandKind.Sequence, command.Kind);
            Assert.Equal(new[] { Key.W, Key.A, Key.S, Key.D }, command.Sequence!.Select(step => step.Key));
        }
    }

    [Fact]
    public void Tick_TickCountWrap_UsesUnsignedElapsedTime()
    {
        long timestamp = 0;
        uint tick = uint.MaxValue - 30_000;
        var (machine, queue, _, _, _, _) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            timestamp = Stopwatch.Frequency * 60;
            tick = unchecked(tick + 60_000);

            machine.Tick();

            Assert.Single(queue.Commands);
        }
    }

    [Fact]
    public async Task Tick_WhileTickInFlight_DoesNotReenter()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, queue, transport, _, _, _) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;
            transport.BlockForegroundReads = true;
            var first = Task.Run(machine.Tick);
            try
            {
                Assert.True(transport.ForegroundEntered.Wait(TimeSpan.FromSeconds(2)));

                machine.Tick();

                Assert.Equal(1, transport.ForegroundCallCount);
            }
            finally
            {
                transport.ReleaseForeground.Set();
                await first.WaitAsync(TimeSpan.FromSeconds(2));
            }

            Assert.Single(queue.Commands);
        }
    }

    [Fact]
    public async Task Tick_AutoRunActivatesBeforeFinalArbitration_DoesNotEnqueueSequence()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, queue, transport, autoRun, profile, _) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;
            profile.AutoRun.IsEnabled = true;
            profile.AutoRun.TriggerKey = Key.R;
            profile.AutoRun.TriggerModifier = ModifierKeys.None;
            transport.BlockForegroundReads = true;
            var antiAfkTick = Task.Run(machine.Tick);
            try
            {
                Assert.True(transport.ForegroundEntered.Wait(TimeSpan.FromSeconds(2)));
                Assert.True(ActivateAutoRun(autoRun, profile));
            }
            finally
            {
                transport.ReleaseForeground.Set();
                await antiAfkTick.WaitAsync(TimeSpan.FromSeconds(2));
            }

            Assert.DoesNotContain(queue.Commands, command => command.Kind == InputCommandKind.Sequence);
            autoRun.Release(includeBackground: true);
        }
    }

    [Fact]
    public void SequenceGuard_ForegroundChangesAfterFirstDown_ReleasesCurrentStepAndAbortsRest()
    {
        long timestamp = 0;
        uint tick = 0;
        var runtime = new InputRuntimeState();
        var profile = CreateProfile();
        var transport = CreateTransport();
        ConfigureRuntime(runtime, profile);
        var sender = new RecordingInputSender(blockFirstDown: true);
        var random = new ThreadLocal<Random>(() => new Random(1));
        var logger = new NullLoggerService();
        using var executor = new InputExecutor(runtime, sender, logger);
        var autoRun = new AutoRunStateMachine(runtime, executor, random, logger, transport);
        using var machine = new AntiAfkStateMachine(
            runtime,
            autoRun,
            random,
            logger,
            transport,
            () => timestamp,
            () => tick);
        executor.Start();

        try
        {
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;
            machine.Tick();
            Assert.True(sender.DownEntered.Wait(TimeSpan.FromSeconds(2)));

            runtime.SetForegroundIdentity((IntPtr)200, 9, profile.NormalizedExecutable, 2);
            sender.ReleaseDown.Set();
            Assert.True(SpinWait.SpinUntil(() => sender.Transitions.Count == 2, TimeSpan.FromSeconds(2)));

            runtime.SetRunning(false);
            Assert.True(executor.StopAndDrain());
            Assert.Equal(
                new[] { (Key.W, true), (Key.W, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        }
        finally
        {
            sender.ReleaseDown.Set();
            runtime.SetRunning(false);
            executor.StopAndDrain();
        }
    }

    // ==================== Background / Forced send modes ====================

    [Fact]
    public void Tick_BackgroundMode_ProfileDeactivated_PostsWasdDirectlyToGameWindow()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, queue, transport, _, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            machine.Tick();

            // W/A/S/D DOWN+UP pairs posted to the captured game window — never SendInput.
            AssertWasdRipple(transport.Posts.ToArray(), expectedWindow: (IntPtr)100);
            Assert.Empty(queue.Commands);
        }
    }

    [Fact]
    public void Tick_BackgroundMode_SameProcessChildExists_PostsToChildWindow()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, _, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Background;
            transport.ChildWindow = (IntPtr)101;
            transport.ProcessIds[transport.ChildWindow] = 7;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            machine.Tick();

            AssertWasdRipple(transport.Posts.ToArray(), expectedWindow: transport.ChildWindow);
        }
    }

    [Fact]
    public void Tick_BackgroundMode_KeyboardStillActive_DoesNotPost()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, queue, transport, _, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            // Keyboard idle is 0 (timestamp never advanced); only the cadence clock moved.
            tick = 60_000;

            machine.Tick();

            Assert.Empty(transport.Posts);
            Assert.Empty(queue.Commands);
        }
    }

    [Fact]
    public void Tick_ForcedMode_KeyboardActive_PostsOnTimer()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, queue, transport, _, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Forced;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            tick = 60_000;

            machine.Tick();

            // Forced skips the keyboard-idle gate: the ripple fires on the timer regardless.
            AssertWasdRipple(transport.Posts.ToArray(), expectedWindow: (IntPtr)100);
            Assert.Empty(queue.Commands);
        }
    }

    [Fact]
    public void Tick_ForegroundMode_GameUnfocused_NeitherPostsNorEnqueues()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, queue, transport, _, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            machine.Tick();

            // Browser-leak regression guard: Foreground mode with the game unfocused must stay
            // completely silent — no window posts and no SendInput sequence.
            Assert.Empty(transport.Posts);
            Assert.Empty(queue.Commands);
        }
    }

    [Fact]
    public void Tick_ForegroundToBackgroundLiveSwitch_FiresWithoutRefocus()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, _, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            // Capture while Foreground is selected, then deactivate, THEN flip the mode — the exact
            // live-edit path behind ProfileChangeKind.AntiAfk, which must NOT release the target.
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            profile.AntiAfk.SendMode = AntiAfkSendMode.Background;
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            machine.Tick();

            AssertWasdRipple(transport.Posts.ToArray(), expectedWindow: (IntPtr)100);
        }
    }

    [Fact]
    public void Tick_ReleaseOwnedByRemovedProfile_StopsBackgroundPosting()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, _, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            // What the hard-deactivation branch now does for a removed active profile.
            machine.ReleaseOwnedBy(profile);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            machine.Tick();
            machine.Tick();

            Assert.Empty(transport.Posts);
        }
    }

    [Fact]
    public void Tick_BackgroundTargetPidMismatch_ClearsTarget_UntilRecaptured()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, _, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            // HWND reused by another process: PID revalidation fails before the first DOWN.
            transport.ProcessIds[(IntPtr)100] = 9;
            machine.Tick();
            Assert.Empty(transport.Posts);

            // The target was cleared: reactivating the profile alone does not bring posting back.
            runtime.SetActiveProfileReference(profile);
            machine.Tick();
            Assert.Empty(transport.Posts);

            // A fresh activation capture restores it.
            transport.ProcessIds[(IntPtr)100] = 7;
            machine.CaptureForegroundTarget(profile);
            machine.Tick();
            AssertWasdRipple(transport.Posts.ToArray(), expectedWindow: (IntPtr)100);
        }
    }

    [Fact]
    public void Tick_AutoRunActive_BackgroundMode_DoesNotPost()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, autoRun, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            autoRun.ConfigureForegroundForTesting(profile, sprintInjected: false, sprintKey: Key.None);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            machine.Tick();

            Assert.Empty(transport.Posts);
        }
    }

    [Fact]
    public async Task Tick_BackgroundTargetInvalidatedMidSequence_PairsStartedKeyAndAbortsRest()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, _, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            var posts = await RunRippleUntilFirstDownThen(
                machine,
                transport,
                () => transport.ProcessIds[(IntPtr)100] = 9);

            Assert.Equal(2, posts.Length);
            Assert.Equal((IntPtr)100, posts[0].Window);
            Assert.Equal((uint)NativeMethods.WM_KEYDOWN, posts[0].Message);
            Assert.Equal(KeyInterop.VirtualKeyFromKey(Key.W), posts[0].VirtualKey);
            // The started key still gets its UP; W/A/S/D's remaining steps abort.
            Assert.Equal((uint)NativeMethods.WM_KEYUP, posts[1].Message);
            Assert.Equal(posts[0].VirtualKey, posts[1].VirtualKey);
        }
    }

    [Fact]
    public async Task Tick_DisabledMidRipple_PairsStartedKeyAbortsRestAndReleasesTapLatch()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, queue, transport, autoRun, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            var posts = await RunRippleUntilFirstDownThen(
                machine,
                transport,
                () => profile.AntiAfk.IsEnabled = false);

            Assert.Equal(2, posts.Length);
            Assert.Equal((uint)NativeMethods.WM_KEYDOWN, posts[0].Message);
            Assert.Equal((uint)NativeMethods.WM_KEYUP, posts[1].Message);

            // The tap latch was released on every path: Auto-Run can activate afterwards.
            runtime.SetActiveProfileReference(profile);
            transport.ForegroundWindow = (IntPtr)100;
            profile.AutoRun.IsEnabled = true;
            profile.AutoRun.TriggerKey = Key.R;
            profile.AutoRun.TriggerModifier = ModifierKeys.None;
            Assert.True(ActivateAutoRun(autoRun, profile));
            autoRun.Release(includeBackground: true);
            Assert.DoesNotContain(queue.Commands, c => c.Kind == InputCommandKind.Sequence);
        }
    }

    [Fact]
    public async Task Tick_OwnerSupersededMidRipple_PairsStartedKeyAndAbortsRest()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, _, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        var other = CreateOtherProfile();
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            // Another game profile becomes active mid-ripple: the per-step ownership veto fires
            // even though the retained target itself was never replaced.
            var posts = await RunRippleUntilFirstDownThen(
                machine,
                transport,
                () => runtime.SetActiveProfileReference(other));

            Assert.Equal(2, posts.Length);
            Assert.Equal((uint)NativeMethods.WM_KEYDOWN, posts[0].Message);
            Assert.Equal((uint)NativeMethods.WM_KEYUP, posts[1].Message);
        }
    }

    [Fact]
    public async Task Tick_ModeSwitchedToForegroundMidRipple_PairsStartedKeyAndAbortsRest()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, _, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            var posts = await RunRippleUntilFirstDownThen(
                machine,
                transport,
                () => profile.AntiAfk.SendMode = AntiAfkSendMode.Foreground);

            Assert.Equal(2, posts.Length);
            Assert.Equal((uint)NativeMethods.WM_KEYDOWN, posts[0].Message);
            Assert.Equal((uint)NativeMethods.WM_KEYUP, posts[1].Message);
        }
    }

    [Fact]
    public async Task Tick_DisabledDuringFinalTargetValidation_DoesNotPost()
    {
        uint tick = 0;
        var (machine, _, transport, _, profile, runtime) = CreateMachine(
            () => Stopwatch.Frequency * 60,
            () => tick);
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Forced;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            tick = 60_000;
            // First read is the pre-arbitration target check; block the second, post-arbitration
            // validation so the live-state check must still run afterward.
            transport.BlockProcessReadNumber = 2;

            var ripple = Task.Run(machine.Tick);
            try
            {
                Assert.True(transport.ProcessReadEntered.Wait(TimeSpan.FromSeconds(2)));
                profile.AntiAfk.IsEnabled = false;
            }
            finally
            {
                transport.ReleaseProcessRead.Set();
            }

            await ripple.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(transport.Posts);
        }
    }

    [Fact]
    public void Tick_TargetSwappedBetweenGatingAndDispatch_DoesNotPostUnderWrongIntervalSemantics()
    {
        long timestamp = 0;
        uint tick = 0;
        Action? onTimestamp = null;
        var (machine, _, transport, _, profile, runtime) = CreateMachine(
            () => { onTimestamp?.Invoke(); return timestamp; },
            () => tick);
        var profileB = CreateOtherProfile();
        using (machine)
        {
            // A Forced tick passes its gates while the keyboard is ACTIVE (Forced skips the idle
            // gate). The injected keyboard-idle clock read is the deterministic seam between the
            // tick's gating reads and its dispatch: racing exactly that window, game B focuses —
            // its profile is published and its window captured, swapping the single retained slot
            // to a Background owner whose idle gate was never evaluated. Nothing may be posted.
            profile.AntiAfk.SendMode = AntiAfkSendMode.Forced;
            machine.CaptureForegroundTarget(profile);
            DeactivateAndUnfocus(runtime, transport);
            tick = 60_000;

            onTimestamp = () =>
            {
                onTimestamp = null;
                transport.ProcessIds[(IntPtr)200] = 9;
                runtime.SetForegroundIdentity((IntPtr)200, 9, profileB.NormalizedExecutable, 2);
                runtime.SetActiveProfile(profileB, 2);
                machine.CaptureForegroundTarget(profileB);
            };
            machine.Tick();

            Assert.Empty(transport.Posts);
        }
    }

    [Fact]
    public async Task Tick_ForegroundGenerationAdvancedMidRipple_PairsStartedKeyAndAbortsRest()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, _, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            // The game is focused and active at generation 1, so the ripple starts normally.
            profile.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profile);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            // Mid-ripple the foreground identity moves to another app at a FRESH generation while
            // the activation worker has not published the new active profile yet: ActiveProfile
            // still names the game, so neither the ownership veto nor the mode checks can abort —
            // only the per-step generation guard can.
            var posts = await RunRippleUntilFirstDownThen(
                machine,
                transport,
                () => runtime.SetForegroundIdentity((IntPtr)999, 42, "browser.exe", 2));

            Assert.Equal(2, posts.Length);
            Assert.Equal((uint)NativeMethods.WM_KEYDOWN, posts[0].Message);
            Assert.Equal((uint)NativeMethods.WM_KEYUP, posts[1].Message);
        }
    }

    [Fact]
    public void ReleaseOwnedBy_StaleOwner_DoesNotEraseNewerCapture()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, _, profileA, runtime) = CreateMachine(() => timestamp, () => tick);
        var profileB = CreateOtherProfile();
        using (machine)
        {
            profileA.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profileA);

            // B takes focus and captures: the single slot now belongs to B.
            transport.ProcessIds[(IntPtr)200] = 9;
            runtime.SetForegroundIdentity((IntPtr)200, 9, profileB.NormalizedExecutable, 2);
            runtime.SetActiveProfile(profileB, 2);
            machine.CaptureForegroundTarget(profileB);

            // A stale owner's release must not erase the newer capture (CAS semantics).
            machine.ReleaseOwnedBy(profileA);

            runtime.SetActiveProfileReference(null);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;
            machine.Tick();

            var posts = transport.Posts.ToArray();
            AssertWasdRipple(posts, expectedWindow: (IntPtr)200);
            Assert.DoesNotContain(posts, p => p.Window == (IntPtr)100);
        }
    }

    [Fact]
    public void CaptureForegroundTarget_FailedCaptureForOwnerItself_KeepsOwnTarget()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, _, profile, runtime) = CreateMachine(() => timestamp, () => tick);
        using (machine)
        {
            profile.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profile);

            // A newer published identity naming a DIFFERENT executable at a settled generation:
            // capture fails validation, but the profile's own target must survive.
            runtime.SetForegroundIdentity((IntPtr)100, 7, "another.exe", 2);
            runtime.SetActiveProfileGeneration(2);
            machine.CaptureForegroundTarget(profile);

            DeactivateAndUnfocus(runtime, transport);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;
            machine.Tick();

            AssertWasdRipple(transport.Posts.ToArray(), expectedWindow: (IntPtr)100);
        }
    }

    [Fact]
    public void CaptureForegroundTarget_FailedCaptureForNewlyFocusedProfile_ClearsForeignTarget()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, _, profileA, runtime) = CreateMachine(() => timestamp, () => tick);
        var profileB = CreateOtherProfile();
        using (machine)
        {
            profileA.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profileA);

            // B settles as active at the SAME current generation, but the published identity still
            // names A's window/game, so B's capture fails on the executable check.
            runtime.SetActiveProfile(profileB, 1);
            machine.CaptureForegroundTarget(profileB);

            // B unfocused: ActiveProfile is null so the ownership gate PASSES — only the
            // foreign-owner clear can keep A's window silent.
            runtime.SetActiveProfileReference(null);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;
            machine.Tick();

            Assert.Empty(transport.Posts);
        }
    }

    [Fact]
    public void Tick_NewProfilePublishedBeforeCapture_BackgroundTargetStaysSilent()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, _, profileA, runtime) = CreateMachine(() => timestamp, () => tick);
        var profileB = CreateOtherProfile();
        using (machine)
        {
            profileA.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profileA);

            // The publish->capture window: B is settled as active at a fresh CURRENT generation
            // with NO capture call at all. Generations match so the tick demonstrably ran past its
            // early generation gate — the ownership gate is what keeps A's target silent.
            transport.ProcessIds[(IntPtr)200] = 9;
            runtime.SetForegroundIdentity((IntPtr)200, 9, profileB.NormalizedExecutable, 2);
            runtime.SetActiveProfile(profileB, 2);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            machine.Tick();

            Assert.Empty(transport.Posts);
        }
    }

    [Fact]
    public void Tick_ValidCaptureForNewProfile_TargetsNewWindowOnly()
    {
        long timestamp = 0;
        uint tick = 0;
        var (machine, _, transport, _, profileA, runtime) = CreateMachine(() => timestamp, () => tick);
        var profileB = CreateOtherProfile();
        using (machine)
        {
            profileA.AntiAfk.SendMode = AntiAfkSendMode.Background;
            machine.CaptureForegroundTarget(profileA);

            transport.ProcessIds[(IntPtr)200] = 9;
            runtime.SetForegroundIdentity((IntPtr)200, 9, profileB.NormalizedExecutable, 2);
            runtime.SetActiveProfile(profileB, 2);
            machine.CaptureForegroundTarget(profileB);
            timestamp = Stopwatch.Frequency * 60;
            tick = 60_000;

            machine.Tick();

            var posts = transport.Posts.ToArray();
            AssertWasdRipple(posts, expectedWindow: (IntPtr)200);
            Assert.DoesNotContain(posts, p => p.Window == (IntPtr)100);
        }
    }

    [Fact]
    public void AutoRunActivate_WhileAntiAfkTapStepInFlight_FailsClosedUntilReleased()
    {
        var runtime = new InputRuntimeState();
        var profile = CreateProfile();
        profile.AutoRun.IsEnabled = true;
        profile.AutoRun.TriggerKey = Key.R;
        profile.AutoRun.TriggerModifier = ModifierKeys.None;
        var transport = CreateTransport();
        ConfigureRuntime(runtime, profile);
        var queue = new RecordingInputQueue();
        var autoRun = new AutoRunStateMachine(
            runtime,
            queue,
            new ThreadLocal<Random>(() => new Random(1)),
            new NullLoggerService(),
            transport);

        Assert.True(autoRun.TryBeginAntiAfkTap());
        Assert.False(ActivateAutoRun(autoRun, profile));

        autoRun.EndAntiAfkTap();
        autoRun.ClearTriggerLatches();
        Assert.True(ActivateAutoRun(autoRun, profile));
        autoRun.Release(includeBackground: true);
    }

    private static bool ActivateAutoRun(AutoRunStateMachine machine, Profile profile)
    {
        var vk = KeyInterop.VirtualKeyFromKey(profile.AutoRun.TriggerKey);
        var physical = machine.ObservePhysicalEvent(vk, isKeyDown: true, isKeyUp: false);
        return machine.Handle(vk, isKeyDown: true, isKeyUp: false, physical);
    }

    private static (AntiAfkStateMachine Machine, RecordingInputQueue Queue,
        FakeAutoRunTransport Transport, AutoRunStateMachine AutoRun, Profile Profile,
        InputRuntimeState Runtime) CreateMachine(
        Func<long> timestamp,
        Func<uint> tickCount,
        ILoggerService? logger = null)
    {
        var runtime = new InputRuntimeState();
        var profile = CreateProfile();
        var transport = CreateTransport();
        ConfigureRuntime(runtime, profile);
        var queue = new RecordingInputQueue();
        var random = new ThreadLocal<Random>(() => new Random(1));
        logger ??= new NullLoggerService();
        var autoRun = new AutoRunStateMachine(runtime, queue, random, logger, transport);
        var machine = new AntiAfkStateMachine(
            runtime,
            autoRun,
            random,
            logger,
            transport,
            timestamp,
            tickCount);
        return (machine, queue, transport, autoRun, profile, runtime);
    }

    private static Profile CreateOtherProfile() => new()
    {
        Name = "Other",
        Executable = "other.exe",
        AntiAfk =
        {
            IsEnabled = true,
            IntervalMinutes = 1,
            SendMode = AntiAfkSendMode.Background
        }
    };

    private static Profile CreateProfile() => new()
    {
        Name = "Game",
        Executable = "game.exe",
        AntiAfk =
        {
            IsEnabled = true,
            IntervalMinutes = 1
        }
    };

    private static FakeAutoRunTransport CreateTransport()
    {
        var transport = new FakeAutoRunTransport();
        transport.ProcessIds[(IntPtr)100] = 7;
        return transport;
    }

    private static void ConfigureRuntime(InputRuntimeState runtime, Profile profile)
    {
        runtime.SetAdvancedMode(true);
        runtime.SetActiveProfile(profile, 1);
        runtime.SetForegroundIdentity((IntPtr)100, 7, profile.NormalizedExecutable, 1);
        runtime.SetRunning(true);
    }

    // The normal Background case: the game was focused once (activation-time capture), the user
    // alt-tabbed to another app (profile deactivated, a foreign window is foreground), so only the
    // retained target still names the game. Generations stay settled, so the tick's early
    // generation gate cannot exit first.
    private static void DeactivateAndUnfocus(InputRuntimeState runtime, FakeAutoRunTransport transport)
    {
        runtime.SetActiveProfileReference(null);
        transport.ForegroundWindow = (IntPtr)999;
    }

    private static void AssertWasdRipple(
        (IntPtr Window, uint Message, int VirtualKey, int ThreadId)[] posts,
        IntPtr expectedWindow)
    {
        var expectedKeys = new[] { Key.W, Key.A, Key.S, Key.D };
        Assert.Equal(8, posts.Length);
        for (var i = 0; i < expectedKeys.Length; i++)
        {
            var down = posts[i * 2];
            var up = posts[i * 2 + 1];
            Assert.Equal(expectedWindow, down.Window);
            Assert.Equal(expectedWindow, up.Window);
            Assert.Equal((uint)NativeMethods.WM_KEYDOWN, down.Message);
            Assert.Equal((uint)NativeMethods.WM_KEYUP, up.Message);
            Assert.Equal(KeyInterop.VirtualKeyFromKey(expectedKeys[i]), down.VirtualKey);
            Assert.Equal(down.VirtualKey, up.VirtualKey);
        }
    }

    // Runs the ripple on a pool task, waits until its FIRST DOWN is observable, applies the
    // mid-flight mutation, then waits for the ripple to finish and returns the recorded posts.
    private static async Task<(IntPtr Window, uint Message, int VirtualKey, int ThreadId)[]>
        RunRippleUntilFirstDownThen(
            AntiAfkStateMachine machine,
            FakeAutoRunTransport transport,
            Action mutation)
    {
        var ripple = Task.Run(machine.Tick);
        Assert.True(
            SpinWait.SpinUntil(
                () => transport.Posts.Any(p => p.Message == (uint)NativeMethods.WM_KEYDOWN),
                TimeSpan.FromSeconds(2)),
            "the ripple never posted its first DOWN");
        mutation();
        await ripple.WaitAsync(TimeSpan.FromSeconds(5));
        return transport.Posts.ToArray();
    }
}
