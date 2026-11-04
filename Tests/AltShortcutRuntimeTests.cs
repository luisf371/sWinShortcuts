using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;
using AppMouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

// Alt + Shortcut keeps two runtime objects (AltMouse, AltKeyboard) that the editor always gives the same toggle and
// hold threshold. These tests drive the real hook dispatch with a controlled clock to pin that runtime behavior.
public sealed class AltShortcutRuntimeTests
{
    private const int THRESHOLD_MS = 200;
    private static readonly InputTrigger Q = InputTrigger.FromKey(Key.Q);
    private static readonly InputTrigger W = InputTrigger.FromKey(Key.W);
    private static readonly InputTrigger E = InputTrigger.FromKey(Key.E);
    private static readonly InputTrigger Left = InputTrigger.FromMouseButton(AppMouseButton.Left);
    private static readonly InputTrigger Middle = InputTrigger.FromMouseButton(AppMouseButton.Middle);
    private static readonly InputTrigger Right = InputTrigger.FromMouseButton(AppMouseButton.Right);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TriggerMovedBetweenFamilies_DuringPendingPress_CancelsOldRouteAndKeepsItsUp(bool fromKey)
    {
        var (from, to) = fromKey ? (Q, Left) : (Left, Q);
        var profile = CreateProfile();
        Bind(profile, from, Key.A, Key.B);
        using var fixture = new Fixture(profile);
        Assert.True(fixture.Press(from, down: true));

        // The row's trigger moves to the other family: the editor publishes fresh dictionaries with both bits.
        profile.AltMouse.Bindings = new();
        profile.AltKeyboard.Bindings = new();
        Bind(profile, to, Key.A, Key.B);
        fixture.Service.ReconcileProfileSettings(profile, ProfileChangeKind.AltMouse | ProfileChangeKind.AltKeyboard);

        fixture.Advance(THRESHOLD_MS + 50);
        fixture.FireTimer(from);
        Assert.True(fixture.Press(from, down: false));
        Assert.False(fixture.Press(from, down: true));
        Assert.False(fixture.Press(from, down: false));
        Assert.Empty(fixture.Drain());

        // The moved trigger uses the shared threshold: Tap just before it, Hold once at it.
        Assert.True(fixture.Press(to, down: true));
        fixture.Advance(THRESHOLD_MS - 1);
        Assert.True(fixture.Press(to, down: false));
        Assert.True(fixture.Press(to, down: true));
        fixture.Advance(THRESHOLD_MS);
        fixture.FireTimer(to);
        Assert.True(fixture.Press(to, down: false));
        Assert.Equal(new[] { (Key.A, true), (Key.A, false), (Key.B, true), (Key.B, false) }, fixture.Drain());
    }

    [Fact]
    public void ToggleOff_DuringPendingKeyAndMousePresses_CancelsBothAndKeepsTheirUps()
    {
        var profile = CreateProfile();
        Bind(profile, Q, Key.A, Key.B);
        Bind(profile, Left, Key.C, Key.D);
        using var fixture = new Fixture(profile);
        Assert.True(fixture.Press(Q, down: true));
        Assert.True(fixture.Press(Left, down: true));

        // One toggle writes both objects and reports both bits. Turning it back on before the releases must not
        // revive the cancelled presses.
        SetEnabled(fixture, profile, false);
        SetEnabled(fixture, profile, true);
        fixture.Advance(THRESHOLD_MS + 50);
        fixture.FireTimer(Q);
        fixture.FireTimer(Left);
        Assert.True(fixture.Press(Q, down: false));
        Assert.True(fixture.Press(Left, down: false));
        Assert.Empty(fixture.Drain());

        SetEnabled(fixture, profile, false);
        Assert.False(fixture.Press(Q, down: true));
        Assert.False(fixture.Press(Q, down: false));
        Assert.False(fixture.Press(Left, down: true));
        Assert.False(fixture.Press(Left, down: false));
        Assert.Empty(fixture.Drain());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HoldThreshold_ReleasePathTimerAndSingleTargetRows_FollowSharedThreshold(bool keyboard)
    {
        var (tapHold, tapOnly, holdOnly) = keyboard ? (Q, W, E) : (Left, Middle, Right);
        var profile = CreateProfile();
        Bind(profile, tapHold, Key.A, Key.B);
        Bind(profile, tapOnly, Key.C, null);
        Bind(profile, holdOnly, null, Key.D);
        using var fixture = new Fixture(profile);

        // Release path: 199 ms is a tap, 200 ms is a hold when the timer has not claimed the press.
        fixture.Tap(tapHold, THRESHOLD_MS - 1);
        fixture.Tap(tapHold, THRESHOLD_MS);
        Assert.Equal(new[] { (Key.A, true), (Key.A, false), (Key.B, true), (Key.B, false) }, fixture.Drain());

        // The timer sends Hold once; a repeated callback and the release send nothing more.
        Assert.True(fixture.Press(tapHold, down: true));
        fixture.Advance(THRESHOLD_MS);
        fixture.FireTimer(tapHold);
        fixture.FireTimer(tapHold);
        Assert.True(fixture.Press(tapHold, down: false));
        Assert.Equal(new[] { (Key.B, true), (Key.B, false) }, fixture.Drain());

        // A Tap-only row taps on a long press; a Hold-only row released early is consumed and sends nothing.
        fixture.Tap(tapOnly, THRESHOLD_MS * 5);
        fixture.Tap(holdOnly, THRESHOLD_MS / 2);
        Assert.Equal(new[] { (Key.C, true), (Key.C, false) }, fixture.Drain());
    }

    [Fact]
    public void ScriptedSequence_SameSettings_ProduceSameOutput()
    {
        static Profile Build()
        {
            var profile = CreateProfile();
            Bind(profile, Q, Key.A, Key.B);
            Bind(profile, Left, Key.C, Key.D);
            profile.AltMouse.WheelUpKey = Key.E;
            profile.AltMouse.WheelDownKey = Key.F;
            return profile;
        }

        var first = RunScriptedAltSequence(Build());

        Assert.Equal(new[]
        {
            "LeftAlt down: passed", "Q down: consumed", "Q up: consumed", "Q down: consumed", "Q up: consumed",
            "Left down: consumed", "Left up: consumed", "Left down: consumed", "Left up: consumed",
            "Wheel up: consumed", "Wheel down: consumed",
            "sent A down", "sent A up", "sent B down", "sent B up", "sent C down", "sent C up",
            "sent D down", "sent D up", "sent E down", "sent E up", "sent F down", "sent F up"
        }, first);
        Assert.Equal(first, RunScriptedAltSequence(Build()));
    }

    // Fixed fake-clock script for format-equivalence checks: physical LeftAlt held; Key.Q trigger quick (50 ms) and
    // long (1000 ms, timer fired) presses; Mouse Left trigger quick and long presses; one Wheel Up and one Wheel Down
    // notch. Returns each hook decision, then every key transition the executor sent.
    internal static IReadOnlyList<string> RunScriptedAltSequence(Profile profile)
    {
        using var fixture = new Fixture(profile, altDown: false);
        var log = new List<string> { $"LeftAlt down: {Decision(fixture.Alt(true))}" };
        foreach (var trigger in new[] { Q, Left })
        {
            var name = trigger.Kind == InputTriggerKind.KeyboardKey ? trigger.Key.ToString() : trigger.MouseButton.ToString();
            foreach (var heldMs in new[] { 50, 1000 })
            {
                log.Add($"{name} down: {Decision(fixture.Press(trigger, down: true))}");
                fixture.Advance(heldMs);
                if (heldMs > 50) fixture.FireTimer(trigger);
                log.Add($"{name} up: {Decision(fixture.Press(trigger, down: false))}");
            }
        }

        log.Add($"Wheel up: {Decision(fixture.Wheel(NativeMethods.WHEEL_DELTA))}");
        log.Add($"Wheel down: {Decision(fixture.Wheel(-NativeMethods.WHEEL_DELTA))}");
        log.AddRange(fixture.Drain().Select(item => $"sent {item.Key} {(item.IsDown ? "down" : "up")}"));
        return log;
    }

    private static string Decision(bool consumed) => consumed ? "consumed" : "passed";

    private static Profile CreateProfile() => new()
    {
        Name = "Game",
        Executable = "game.exe",
        AltMouse = { IsEnabled = true, HoldThresholdMilliseconds = THRESHOLD_MS },
        AltKeyboard = { IsEnabled = true, HoldThresholdMilliseconds = THRESHOLD_MS }
    };

    // Publishes a fresh dictionary, as the editor does, so a live dictionary is never mutated.
    private static void Bind(Profile profile, InputTrigger trigger, Key? tap, Key? hold)
    {
        if (trigger.Kind == InputTriggerKind.KeyboardKey)
        {
            profile.AltKeyboard.Bindings = new(profile.AltKeyboard.Bindings) { [trigger.Key] = new() { TapKey = tap, HoldKey = hold } };
        }
        else
        {
            profile.AltMouse.Bindings = new(profile.AltMouse.Bindings) { [trigger.MouseButton] = new() { TapKey = tap, HoldKey = hold } };
        }
    }

    private static void SetEnabled(Fixture fixture, Profile profile, bool enabled)
    {
        profile.AltMouse.IsEnabled = profile.AltKeyboard.IsEnabled = enabled;
        fixture.Service.ReconcileProfileSettings(profile, ProfileChangeKind.AltMouse | ProfileChangeKind.AltKeyboard);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RecordingInputSender _sender = new();
        private readonly int _hookThread = Environment.CurrentManagedThreadId;
        private int _sentCount;
        private long _now = Stopwatch.Frequency;
        private bool _leftAltDown;

        internal Fixture(Profile profile, bool altDown = true)
        {
            // The fake time is visible only on the test (hook) thread. The real hold timers fire on pool threads,
            // read time 0 and never claim a press, so each test chooses when the timer callback runs (FireTimer).
            // Windows' key state follows the physical LeftAlt this fixture dispatches.
            Service = new InputHookService(new NullLoggerService(), _sender,
                () => Environment.CurrentManagedThreadId == _hookThread ? Volatile.Read(ref _now) : 0,
                vk => vk == 0xA4 && Volatile.Read(ref _leftAltDown), FakeAutoRunTransport.MatchingForeground());
            Service.StartInputExecutorForTesting();
            Service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: false);
            if (altDown) Assert.False(Alt(true));
        }

        internal InputHookService Service { get; }

        internal bool Alt(bool down)
        {
            var consumed = Service.DispatchDecodedKeyboardEvent(0xA4, down, !down);
            // Windows updates its key state only after the hook has seen the event.
            Volatile.Write(ref _leftAltDown, down);
            return consumed;
        }

        internal void Advance(int milliseconds) => _now += milliseconds * Stopwatch.Frequency / 1000;

        internal bool Press(InputTrigger trigger, bool down) => trigger.Kind switch
        {
            InputTriggerKind.KeyboardKey => Service.DispatchDecodedKeyboardEvent(KeyInterop.VirtualKeyFromKey(trigger.Key), down, !down),
            InputTriggerKind.MouseButton => Service.DispatchDecodedMouseEvent((trigger.MouseButton, down) switch
            {
                (AppMouseButton.Left, true) => NativeMethods.WM_LBUTTONDOWN,
                (AppMouseButton.Left, false) => NativeMethods.WM_LBUTTONUP,
                (AppMouseButton.Middle, true) => NativeMethods.WM_MBUTTONDOWN,
                (AppMouseButton.Middle, false) => NativeMethods.WM_MBUTTONUP,
                (AppMouseButton.Right, true) => NativeMethods.WM_RBUTTONDOWN,
                (AppMouseButton.Right, false) => NativeMethods.WM_RBUTTONUP,
                _ => throw new ArgumentOutOfRangeException(nameof(trigger))
            }, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(trigger))
        };

        // A consumed press held for exactly `milliseconds`, released without running the timer.
        internal void Tap(InputTrigger trigger, int milliseconds)
        {
            Assert.True(Press(trigger, down: true));
            Advance(milliseconds);
            Assert.True(Press(trigger, down: false));
        }

        internal bool Wheel(int delta) =>
            Service.DispatchDecodedMouseEvent(NativeMethods.WM_MOUSEWHEEL, unchecked((uint)(ushort)(short)delta << 16));

        // Runs the trigger's hold-timer callback on the test thread, where it reads the fake time.
        internal void FireTimer(InputTrigger trigger)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var keyboard = trigger.Kind == InputTriggerKind.KeyboardKey;
            var gestures = Service.GetGesturesForTesting();
            var states = (IDictionary)typeof(GestureChordStateMachine)
                .GetField(keyboard ? "_keyboardStates" : "_mouseStates", flags)!.GetValue(gestures)!;
            object key = keyboard ? trigger.Key : trigger.MouseButton;
            typeof(GestureChordStateMachine)
                .GetMethod(keyboard ? "OnKeyboardTimerFired" : "OnMouseTimerFired", flags)!
                .Invoke(gestures, [key, states[key]]);
        }

        // Waits for the executor and returns the key transitions sent since the previous drain.
        internal (Key Key, bool IsDown)[] Drain()
        {
            // Synchronous on purpose: an await could resume on another thread, which would not see the fake time.
            Assert.True(Service.EnqueueDummyForTesting().Wait(TimeSpan.FromSeconds(3)));
            var sent = _sender.Transitions.Skip(_sentCount).Select(item => (item.Key, item.IsDown)).ToArray();
            _sentCount += sent.Length;
            return sent;
        }

        public void Dispose()
        {
            Service.StopInputExecutorForTesting();
            Service.Dispose();
        }
    }
}
