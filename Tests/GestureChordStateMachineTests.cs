using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class GestureChordStateMachineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AltGesture_DelayedHoldCallback_CannotClaimNextPress(bool keyboard)
    {
        var profile = CreateProfile();
        profile.AltMouse.IsEnabled = true;
        profile.AltMouse.HoldThresholdMilliseconds = 60_000;
        profile.AltMouse.Bindings[sWinShortcuts.Models.MouseButton.Middle] = new() { TapKey = Key.A, HoldKey = Key.B };
        profile.AltKeyboard.IsEnabled = true;
        profile.AltKeyboard.HoldThresholdMilliseconds = 60_000;
        profile.AltKeyboard.Bindings[Key.Q] = new() { TapKey = Key.A, HoldKey = Key.B };
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        long tick = 1;
        int callbackThread = 0;
        using var machine = new GestureChordStateMachine(ConfigureRuntime(profile), queue, random,
            new NullLoggerService(), () => false, () =>
            {
                var capturedTick = Volatile.Read(ref tick);
                if (Environment.CurrentManagedThreadId == Volatile.Read(ref callbackThread))
                {
                    callbackEntered.Set();
                    Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(5)));
                }
                return capturedTick;
            });
        machine.SeedAltPressedForTesting(true);

        bool Handle(bool down) => keyboard
            ? machine.HandleAltKeyboard(KeyInterop.VirtualKeyFromKey(Key.Q), down, !down)
            : machine.HandleAltMouse(down ? NativeMethods.WM_MBUTTONDOWN : NativeMethods.WM_MBUTTONUP, 0);

        Assert.True(Handle(true));
        Volatile.Write(ref tick, 61 * Stopwatch.Frequency);
        var timerTask = Task.Factory.StartNew(() =>
        {
            Volatile.Write(ref callbackThread, Environment.CurrentManagedThreadId);
            FireGestureTimer(machine, keyboard);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(Handle(false));
            Assert.True(Handle(true));
        }
        finally
        {
            releaseCallback.Set();
            await timerTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.True(Handle(false));

        Assert.Equal(new[] { (Key.B, true), (Key.B, false), (Key.A, true), (Key.A, false) },
            queue.Commands.Select(command => (command.Key, command.IsDown)).ToArray());
        Assert.All(queue.Commands.Where(command => command.IsDown), command => Assert.True(machine.CanExecute(command)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AltGesture_QueuedCallbackAfterTapOnlyRebind_DoesNotConsumeTap(bool keyboard)
    {
        var profile = CreateProfile();
        profile.AltMouse.IsEnabled = true;
        profile.AltMouse.HoldThresholdMilliseconds = 60_000;
        profile.AltMouse.Bindings[sWinShortcuts.Models.MouseButton.Middle] = new() { TapKey = Key.A, HoldKey = Key.B };
        profile.AltKeyboard.IsEnabled = true;
        profile.AltKeyboard.HoldThresholdMilliseconds = 60_000;
        profile.AltKeyboard.Bindings[Key.Q] = new() { TapKey = Key.A, HoldKey = Key.B };
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        long tick = 1;
        using var machine = new GestureChordStateMachine(ConfigureRuntime(profile), queue, random,
            new NullLoggerService(), () => false, () => tick);
        machine.SeedAltPressedForTesting(true);
        bool Handle(bool down) => keyboard
            ? machine.HandleAltKeyboard(KeyInterop.VirtualKeyFromKey(Key.Q), down, !down)
            : machine.HandleAltMouse(down ? NativeMethods.WM_MBUTTONDOWN : NativeMethods.WM_MBUTTONUP, 0);

        Assert.True(Handle(true));
        profile.AltMouse.Bindings[sWinShortcuts.Models.MouseButton.Middle].HoldKey = null;
        profile.AltKeyboard.Bindings[Key.Q].HoldKey = null;
        if (keyboard) machine.ReleaseAltKeyboard();
        else machine.ReleaseAltMouse();
        Assert.True(Handle(false));
        Assert.Empty(queue.Commands);

        Assert.True(Handle(true));
        tick = 61 * Stopwatch.Frequency;
        FireGestureTimer(machine, keyboard);
        Assert.True(Handle(false));
        Assert.Equal(new[] { (Key.A, true), (Key.A, false) },
            queue.Commands.Select(command => (command.Key, command.IsDown)).ToArray());
    }

    private static void FireGestureTimer(GestureChordStateMachine machine, bool keyboard)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var states = (IDictionary)typeof(GestureChordStateMachine)
            .GetField(keyboard ? "_keyboardStates" : "_mouseStates", flags)!.GetValue(machine)!;
        object trigger = keyboard ? Key.Q : sWinShortcuts.Models.MouseButton.Middle;
        typeof(GestureChordStateMachine)
            .GetMethod(keyboard ? "OnKeyboardTimerFired" : "OnMouseTimerFired", flags)!
            .Invoke(machine, [trigger, states[trigger]]);
    }

    [Fact]
    public void Wheel_MinimumSignedDelta_BoundsAdmissionAndRetainsFraction()
    {
        var profile = CreateProfile();
        profile.AltMouse.IsEnabled = true;
        profile.AltMouse.WheelDownKey = Key.Q;
        var runtime = ConfigureRuntime(profile);
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        long clock = 100;
        using var machine = new GestureChordStateMachine(runtime, queue, random,
            new NullLoggerService(), () => false, () => ++clock);
        machine.SeedAltPressedForTesting(true);

        Assert.True(machine.HandleWheel(NativeMethods.WM_MOUSEWHEEL, WheelData(short.MinValue)));
        Assert.Equal(4, queue.Commands.Count);
        Assert.All(queue.Commands, command =>
        {
            Assert.Equal(Key.Q, command.Key);
            Assert.Equal(InputCommandKind.WheelTap, command.Kind);
            Assert.Equal(101, command.CreatedTick);
            Assert.InRange(command.DelayBeforeMs, 31, 53);
        });
        while (queue.Commands.TryDequeue(out var command)) machine.OnCompleted(command);

        // 32768 % 120 == 8. Excess whole notches were dropped, only eight units remain.
        Assert.True(machine.HandleWheel(NativeMethods.WM_MOUSEWHEEL, WheelData(-112)));
        Assert.Single(queue.Commands);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void Wheel_RejectedAdmission_RefundsAndClearsRemainder(int acceptedBeforeFailure, bool throws)
    {
        var profile = CreateProfile();
        profile.AltMouse.IsEnabled = true;
        profile.AltMouse.WheelUpKey = Key.E;
        var queue = new WheelAdmissionQueue { AcceptLimit = acceptedBeforeFailure, ThrowOnRejection = throws };
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var machine = new GestureChordStateMachine(ConfigureRuntime(profile), queue, random,
            new NullLoggerService(), () => false);
        machine.SeedAltPressedForTesting(true);

        Assert.Equal(acceptedBeforeFailure != 0, machine.HandleWheel(NativeMethods.WM_MOUSEWHEEL, WheelData(270)));
        Assert.Equal(acceptedBeforeFailure, queue.Commands.Count);
        foreach (var command in queue.Commands) machine.OnCompleted(command);
        queue.Commands.Clear();
        queue.AcceptLimit = int.MaxValue;
        Assert.True(machine.HandleWheel(NativeMethods.WM_MOUSEWHEEL, WheelData(90)));
        Assert.Empty(queue.Commands);
        Assert.True(machine.HandleWheel(NativeMethods.WM_MOUSEWHEEL, WheelData(390)));
        Assert.Equal(4, queue.Commands.Count);
    }

    [Fact]
    public void Wheel_CompletionDuringEnqueue_RefundsExactlyOnce()
    {
        var profile = CreateProfile();
        profile.AltMouse.IsEnabled = true;
        profile.AltMouse.WheelUpKey = Key.E;
        var queue = new WheelAdmissionQueue { CompleteInline = true };
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var machine = new GestureChordStateMachine(ConfigureRuntime(profile), queue, random,
            new NullLoggerService(), () => false);
        machine.SeedAltPressedForTesting(true);
        for (var i = 0; i < 10; i++)
            Assert.True(machine.HandleWheel(NativeMethods.WM_MOUSEWHEEL, WheelData(240)));
        Assert.Equal(20, queue.Commands.Count);

        queue.Commands.Clear();
        queue.CompleteInline = false;
        Assert.True(machine.HandleWheel(NativeMethods.WM_MOUSEWHEEL, WheelData(960)));
        Assert.True(machine.HandleWheel(NativeMethods.WM_MOUSEWHEEL, WheelData(960)));
        Assert.Equal(4, queue.Commands.Count);
    }

    private static uint WheelData(int delta) => unchecked((uint)(ushort)(short)delta << 16);

    private sealed class WheelAdmissionQueue : IInputQueue
    {
        internal List<InputCommand> Commands { get; } = [];
        internal int AcceptLimit = int.MaxValue;
        internal bool ThrowOnRejection;
        internal bool CompleteInline;

        public bool Enqueue(in InputCommand command)
        {
            if (Commands.Count >= AcceptLimit)
            {
                if (ThrowOnRejection) throw new InvalidOperationException("Admission rejected");
                return false;
            }
            Commands.Add(command);
            if (CompleteInline) command.Guard?.OnCompleted(command);
            return true;
        }

        public bool EnqueuePair(in InputCommand down, in InputCommand up) => throw new NotSupportedException();
    }

    [Fact]
    public void AltGestures_AdvancedModeDisabled_GuardedCommandsRemainExecutable()
    {
        var profile = CreateProfile();
        profile.AltMouse.IsEnabled = true;
        profile.AltMouse.Bindings = new Dictionary<sWinShortcuts.Models.MouseButton, MouseButtonBinding>
        {
            [sWinShortcuts.Models.MouseButton.Middle] = new() { TapKey = Key.A }
        };
        profile.AltKeyboard.IsEnabled = true;
        profile.AltKeyboard.Bindings = new Dictionary<Key, AltKeyboardBinding>
        {
            [Key.Q] = new() { TapKey = Key.B }
        };
        var runtime = ConfigureRuntime(profile);
        runtime.SetAdvancedMode(false);
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var machine = new GestureChordStateMachine(
            runtime,
            queue,
            random,
            new NullLoggerService(),
            () => false);
        machine.SeedAltPressedForTesting(true);

        Assert.True(machine.HandleAltMouse(NativeMethods.WM_MBUTTONDOWN, 0));
        Assert.True(machine.HandleAltMouse(NativeMethods.WM_MBUTTONUP, 0));
        var q = KeyInterop.VirtualKeyFromKey(Key.Q);
        Assert.True(machine.HandleAltKeyboard(q, isKeyDown: true, isKeyUp: false));
        Assert.True(machine.HandleAltKeyboard(q, isKeyDown: false, isKeyUp: true));

        var guardedDowns = queue.Commands.Where(command => command.IsDown).ToArray();
        Assert.Equal(2, guardedDowns.Length);
        Assert.All(guardedDowns, command => Assert.True(machine.CanExecute(command)));
    }

    [Fact]
    public void AltMouse_TapAndHold_OwnTheirPairs()
    {
        var profile = CreateProfile();
        profile.AltMouse.IsEnabled = true;
        profile.AltMouse.HoldThresholdMilliseconds = 60_000;
        profile.AltMouse.Bindings = new Dictionary<sWinShortcuts.Models.MouseButton, MouseButtonBinding>
        {
            [sWinShortcuts.Models.MouseButton.Middle] = new() { TapKey = Key.A, HoldKey = Key.B }
        };
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var machine = CreateMachine(profile, queue, random);

        Assert.True(machine.HandleAltMouse(NativeMethods.WM_MBUTTONDOWN, 0));
        Assert.True(machine.HandleAltMouse(NativeMethods.WM_MBUTTONUP, 0));

        profile.AltMouse.HoldThresholdMilliseconds = 10;
        Assert.True(machine.HandleAltMouse(NativeMethods.WM_MBUTTONDOWN, 0));
        Assert.True(SpinWait.SpinUntil(() => queue.Commands.Count == 4, TimeSpan.FromSeconds(2)));
        Assert.True(machine.HandleAltMouse(NativeMethods.WM_MBUTTONUP, 0));

        Assert.Equal(
            new[] { (Key.A, true), (Key.A, false), (Key.B, true), (Key.B, false) },
            queue.Commands.Select(command => (command.Key, command.IsDown)).ToArray());
        Assert.All(queue.Commands.Where(command => command.IsDown), command => Assert.Same(machine, command.Guard));
    }

    [Fact]
    public void AltKeyboard_PanicCancelsActionAndOwnsMatchingUp()
    {
        var profile = CreateProfile();
        profile.AltKeyboard.IsEnabled = true;
        profile.AltKeyboard.HoldThresholdMilliseconds = 60_000;
        profile.AltKeyboard.Bindings = new Dictionary<Key, AltKeyboardBinding>
        {
            [Key.Q] = new() { TapKey = Key.A, HoldKey = Key.B }
        };
        profile.RightClickHoldBreath.IsEnabled = true;
        profile.RightClickHoldBreath.DelayMilliseconds = 60_000;
        profile.RightClickHoldBreath.PanicTrigger = InputTrigger.FromKey(Key.Q);
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var machine = CreateMachine(profile, queue, random, rightButtonPressed: true);
        var q = KeyInterop.VirtualKeyFromKey(Key.Q);

        machine.HandleRightButtonDown(rightButtonPressed: true);
        Assert.True(machine.HandleAltKeyboard(q, isKeyDown: true, isKeyUp: false));
        Assert.True(machine.HandlePanicKey(q, isKeyDown: true, isKeyUp: false, rightButtonPressed: true));
        machine.HandleAltKeyboardPanicOverride(q, isKeyDown: true, isKeyUp: false);
        Assert.True(machine.HandlePanicKey(q, isKeyDown: true, isKeyUp: false, rightButtonPressed: true));
        Assert.True(machine.HandlePanicKey(q, isKeyDown: false, isKeyUp: true, rightButtonPressed: true));
        machine.HandleAltKeyboardPanicOverride(q, isKeyDown: false, isKeyUp: true);
        Assert.Empty(queue.Commands);

        Assert.True(machine.HandleAltKeyboard(q, isKeyDown: true, isKeyUp: false));
        Assert.True(machine.HandleAltKeyboard(q, isKeyDown: false, isKeyUp: true));
        Assert.Equal(
            new[] { (Key.A, true), (Key.A, false) },
            queue.Commands.Select(command => (command.Key, command.IsDown)).ToArray());
    }

    [Fact]
    public void AltKeyboard_CancelAfterDownAccepted_KeepsCancellationScopedToPress()
    {
        var profile = CreateProfile();
        profile.AltKeyboard.IsEnabled = true;
        profile.AltKeyboard.HoldThresholdMilliseconds = 10;
        profile.AltKeyboard.Bindings = new Dictionary<Key, AltKeyboardBinding>
        {
            [Key.Q] = new() { HoldKey = Key.B }
        };
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var machine = CreateMachine(profile, queue, random);
        var q = KeyInterop.VirtualKeyFromKey(Key.Q);

        Assert.True(machine.HandleAltKeyboard(q, isKeyDown: true, isKeyUp: false));
        Assert.True(SpinWait.SpinUntil(() => queue.Commands.Count == 2, TimeSpan.FromSeconds(2)));
        var commands = queue.Commands.ToArray();
        var acknowledgement = Assert.IsAssignableFrom<InputCommandAcknowledgement>(commands[0].Acknowledgement);
        Assert.True(machine.CanExecute(commands[0]));
        acknowledgement.MarkDownSent();

        machine.HandleAltKeyboardPanicOverride(q, isKeyDown: true, isKeyUp: false);

        Assert.False(machine.CanExecute(commands[0]));
        Assert.False(machine.CanExecute(commands[0]));
        Assert.Same(acknowledgement, commands[1].Acknowledgement);
        Assert.True(commands[1].RequireAcknowledgement);
        Assert.True(acknowledgement.DownSent);
    }

    [Fact]
    public void HoldBreath_RightButtonUpQueuesReleaseAfterDown()
    {
        var profile = CreateProfile();
        profile.RightClickHoldBreath.IsEnabled = true;
        profile.RightClickHoldBreath.DelayMilliseconds = 0;
        profile.RightClickHoldBreath.HoldBreathKey = Key.LeftShift;
        profile.RightClickHoldBreath.Mode = HoldBreathMode.Hold;
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var machine = CreateMachine(profile, queue, random, rightButtonPressed: true);

        machine.HandleRightButtonDown(rightButtonPressed: true);
        machine.HandleRightButtonUp();

        var commands = queue.Commands.ToArray();
        Assert.Equal(new[] { true, false }, commands.Select(command => command.IsDown).ToArray());
        Assert.Equal(new[] { Key.LeftShift, Key.LeftShift }, commands.Select(command => command.Key).ToArray());
        Assert.Same(machine, commands[0].Guard);
        Assert.Null(commands[1].Guard);
    }

    [Fact]
    public async Task DisposeComponentWhileTimerArmIsInFlight_DoesNotThrowOrEnqueue()
    {
        var randomEntered = new ManualResetEventSlim();
        var releaseRandom = new ManualResetEventSlim();
        using var random = new ThreadLocal<Random>(() =>
        {
            randomEntered.Set();
            releaseRandom.Wait(TimeSpan.FromSeconds(2));
            return new Random(1);
        });
        var profile = CreateProfile();
        profile.RightClickHoldBreath.IsEnabled = true;
        profile.RightClickHoldBreath.DelayMilliseconds = 100;
        var runtime = ConfigureRuntime(profile);
        var queue = new RecordingInputQueue();
        var machine = new GestureChordStateMachine(
            runtime,
            queue,
            random,
            new NullLoggerService(),
            () => true);
        var arm = Task.Run(() => Record.Exception(() => machine.HandleRightButtonDown(rightButtonPressed: true)));

        try
        {
            Assert.True(randomEntered.Wait(TimeSpan.FromSeconds(2)));
            machine.Dispose();
        }
        finally
        {
            releaseRandom.Set();
        }

        Assert.Null(await arm.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Null(Record.Exception(machine.HandleRightButtonUp));
        Assert.Empty(queue.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObserveAlt_LeftAltReleasedWhileRightAltHeld_LatchFollowsPhysicalRightAlt(bool rightAltPhysical)
    {
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var windowsKeys = new bool[256];
        using var machine = CreateAltLatchMachine(queue, random, new NullLoggerService(), vk => windowsKeys[vk]);
        // Physical RightAlt, or this app holding it (hold-breath bound to RightAlt): Windows reports it held either way.
        if (rightAltPhysical) machine.ObserveAlt(0xA5, isKeyDown: true, isKeyUp: false);
        else machine.ObserveInjectedAlt(0xA5, isKeyDown: true);
        windowsKeys[0xA5] = true;

        machine.ObserveAlt(0xA4, isKeyDown: true, isKeyUp: false);
        windowsKeys[0xA4] = true;
        machine.ObserveAlt(0xA4, isKeyDown: false, isKeyUp: true);
        windowsKeys[0xA4] = false;

        Assert.Equal(rightAltPhysical, machine.AltPressed);
        Assert.Equal(rightAltPhysical,
            machine.HandleAltKeyboard(KeyInterop.VirtualKeyFromKey(Key.Q), isKeyDown: true, isKeyUp: false));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AltTrigger_WindowsReportsAltUp_PassesThroughAndLogsMissedRelease(bool keyboard, bool windowsAltDown)
    {
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var logger = new NullLoggerService { IsEnabled = true };
        using var machine = CreateAltLatchMachine(queue, random, logger, vk => windowsAltDown && vk == 0xA4);
        // The press reached the hook. When Windows reports Alt up, its release did not (secure desktop, another hook).
        machine.ObserveAlt(0xA4, isKeyDown: true, isKeyUp: false);

        var consumed = keyboard
            ? machine.HandleAltKeyboard(KeyInterop.VirtualKeyFromKey(Key.Q), isKeyDown: true, isKeyUp: false)
            : machine.HandleAltMouse(NativeMethods.WM_MBUTTONDOWN, 0);

        Assert.Equal(windowsAltDown, consumed);
        Assert.Equal(windowsAltDown, machine.AltPressed);
        Assert.Equal(!windowsAltDown, logger.Messages.Any(message => message.StartsWith(
            $"Alt latch: cleared LeftAlt at {(keyboard ? "Q" : "Middle")};", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdoptHeldAltFromWindows_AppHeldAlt_IsNeverAdopted(bool appHolds)
    {
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var machine = CreateAltLatchMachine(queue, random, new NullLoggerService(), vk => vk == 0xA5);
        if (appHolds) machine.ObserveInjectedAlt(0xA5, isKeyDown: true);

        machine.AdoptHeldAltFromWindows(vk => vk == 0xA5);

        Assert.Equal(!appHolds, machine.AltPressed);
    }

    [Fact]
    public void AltMouseGesture_DebugLog_RecordsDecisionAndElapsedTime()
    {
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var logger = new NullLoggerService { IsEnabled = true };
        long tick = 0;
        var profile = CreateAltLatchProfile();
        using var machine = new GestureChordStateMachine(ConfigureRuntime(profile), queue, random, logger, () => false,
            () => Volatile.Read(ref tick), _ => true);
        machine.ObserveAlt(0xA4, isKeyDown: true, isKeyUp: false);

        Assert.True(machine.HandleAltMouse(NativeMethods.WM_MBUTTONDOWN, 0));
        Volatile.Write(ref tick, Stopwatch.Frequency / 10);
        Assert.True(machine.HandleAltMouse(NativeMethods.WM_MBUTTONUP, 0));

        Assert.Contains("Alt latch: LeftAlt down (held=LeftAlt)", logger.Messages);
        Assert.Contains("Alt+Mouse: Middle → A (tap: released at 100 ms; hold threshold 150 ms)", logger.Messages);
    }

    [Fact]
    public void AltLatch_HotPathWithLoggingDisabled_DoesNotAllocate()
    {
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var machine = CreateAltLatchMachine(queue, random, new NullLoggerService(), _ => false);
        var q = KeyInterop.VirtualKeyFromKey(Key.Q);
        void Cycle()
        {
            machine.ObserveInjectedAlt(0xA5, isKeyDown: true);
            // Windows reports Alt up throughout, so every trigger clears a missed release and passes through.
            machine.ObserveAlt(0xA4, isKeyDown: true, isKeyUp: false);
            machine.HandleAltKeyboard(q, isKeyDown: true, isKeyUp: false);
            machine.HandleAltKeyboard(q, isKeyDown: false, isKeyUp: true);
            machine.ObserveAlt(0xA4, isKeyDown: true, isKeyUp: false);
            machine.HandleAltMouse(NativeMethods.WM_MBUTTONDOWN, 0);
            machine.HandleAltMouse(NativeMethods.WM_MBUTTONUP, 0);
            machine.ObserveAlt(0xA4, isKeyDown: true, isKeyUp: false);
            machine.HandleWheel(NativeMethods.WM_MOUSEWHEEL, WheelData(120));
            machine.ObserveAlt(0xA4, isKeyDown: false, isKeyUp: true);
            machine.ObserveInjectedAlt(0xA5, isKeyDown: false);
            machine.AdoptHeldAltFromWindows(_ => false);
        }
        Cycle();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 256; index++) Cycle();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Empty(queue.Commands);
    }

    private static GestureChordStateMachine CreateAltLatchMachine(
        RecordingInputQueue queue,
        ThreadLocal<Random> random,
        NullLoggerService logger,
        Func<int, bool> isWindowsKeyDown) =>
        new(ConfigureRuntime(CreateAltLatchProfile()), queue, random, logger, () => false,
            isWindowsKeyDown: isWindowsKeyDown);

    private static Profile CreateAltLatchProfile()
    {
        var profile = CreateProfile();
        profile.AltMouse.IsEnabled = true;
        profile.AltMouse.HoldThresholdMilliseconds = 150;
        profile.AltMouse.Bindings[sWinShortcuts.Models.MouseButton.Middle] = new() { TapKey = Key.A, HoldKey = Key.B };
        profile.AltKeyboard.IsEnabled = true;
        profile.AltKeyboard.Bindings[Key.Q] = new() { TapKey = Key.A };
        return profile;
    }

    private static GestureChordStateMachine CreateMachine(
        Profile profile,
        RecordingInputQueue queue,
        ThreadLocal<Random> random,
        bool rightButtonPressed = false)
    {
        var runtime = ConfigureRuntime(profile);
        var machine = new GestureChordStateMachine(
            runtime,
            queue,
            random,
            new NullLoggerService(),
            () => rightButtonPressed);
        machine.SeedAltPressedForTesting(true);
        return machine;
    }

    private static InputRuntimeState ConfigureRuntime(Profile profile)
    {
        var runtime = new InputRuntimeState();
        runtime.SetAdvancedMode(true);
        runtime.SetActiveProfile(profile, 1);
        runtime.SetForegroundIdentity((IntPtr)1, 1, profile.NormalizedExecutable, 1);
        runtime.SetRunning(true);
        return runtime;
    }

    private static Profile CreateProfile() => new()
    {
        Name = "Game",
        Executable = "game.exe"
    };
}
