using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using sWinShortcuts.Controls;
using sWinShortcuts.Converters;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using Tests.Fakes;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

// KeyCaptureBox.CaptureSource and KeyCaptureBox.Current are process-wide statics.
[CollectionDefinition(nameof(KeyCaptureBoxCollection), DisableParallelization = true)]
public sealed class KeyCaptureBoxCollection { }

[Collection(nameof(KeyCaptureBoxCollection))]
public sealed class KeyCaptureBoxTests : IDisposable
{
    private const int VK_E = 0x45;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;

    public KeyCaptureBoxTests() => KeyCaptureBox.CaptureSource = null;

    public void Dispose() => KeyCaptureBox.CaptureSource = null;

    [Fact]
    public Task Idle_ShowsChordOrNoneText() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(() =>
        {
            var box = new KeyCaptureBox { NoneText = "No shortcut" };
            Assert.Equal("No shortcut", box.DisplayBox.Text);
            Assert.True(box.DisplayBox.IsReadOnly);
            Assert.False(box.DisplayBox.Focusable);
            // Dimmed while idle so it doesn't read as a field you type into; Assign changes it.
            Assert.Equal(KeyCaptureBox.IDLE_OPACITY, box.DisplayBox.Opacity);

            box.Chord = KeyChord.FromKey(Key.E, ModifierKeys.Shift | ModifierKeys.Control);
            Assert.Equal("LCtrl+LShift+E", box.DisplayBox.Text);
            Assert.Equal("Assign", box.AssignButton.Content);
            Assert.Equal(Visibility.Collapsed, box.ClearButton.Visibility);
            return Task.CompletedTask;
        });

    [Fact]
    public Task AssignClick_ShowsCapturingAndBeginsHookCaptureOnce() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(() =>
        {
            var source = new FakeKeyCaptureSource();
            KeyCaptureBox.CaptureSource = source;
            var box = new KeyCaptureBox();

            ClickAssign(box);

            Assert.True(box.IsCapturing);
            Assert.True(box.UsesHookCapture);
            Assert.Same(box, KeyCaptureBox.Current);
            Assert.Equal(KeyCaptureBox.CapturingText, box.DisplayBox.Text);
            Assert.Equal("Cancel", box.AssignButton.Content);
            Assert.Equal(1, box.DisplayBox.Opacity);
            Assert.Equal(1, source.BeginCount);

            ClickAssign(box);
            Assert.False(box.IsCapturing);
            Assert.Null(KeyCaptureBox.Current);
            Assert.Equal("None", box.DisplayBox.Text);
            Assert.Equal("Assign", box.AssignButton.Content);
            Assert.Equal(KeyCaptureBox.IDLE_OPACITY, box.DisplayBox.Opacity);
            Assert.Equal(1, source.BeginCount);
            Assert.Equal(1, source.EndCount);
            return Task.CompletedTask;
        });

    [Fact]
    public Task HookCapture_CtrlE_AppliesOnlyAfterTheDispatcherQueueRuns() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var source = new FakeKeyCaptureSource();
            KeyCaptureBox.CaptureSource = source;
            var model = new ChordModel { Shortcut = KeyChord.FromKey(Key.F5) };
            var box = new KeyCaptureBox { Mode = KeyCaptureMode.Combo };
            box.SetBinding(KeyCaptureBox.ChordProperty, new Binding(nameof(ChordModel.Shortcut)) { Source = model, Mode = BindingMode.TwoWay });
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            ClickAssign(box);

            // E first, then Ctrl: the result still puts the modifier first.
            source.Press(VK_E);
            source.Press(VK_LCONTROL);
            source.Release(VK_E);
            source.Release(VK_LCONTROL);

            Assert.Equal(KeyChord.FromKey(Key.F5), box.Chord);
            Assert.Equal(KeyChord.FromKey(Key.F5), model.Shortcut);
            Assert.Equal(0, source.EndCount);

            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Equal(KeyChord.FromKey(Key.E, ModifierKeys.Control), box.Chord);
            Assert.Equal(KeyChord.FromKey(Key.E, ModifierKeys.Control), model.Shortcut);
            Assert.Equal("LCtrl+E", box.DisplayBox.Text);
            Assert.Equal(1, source.EndCount);
            Assert.False(box.IsCapturing);
            Assert.Null(KeyCaptureBox.Current);
        });

    [Fact]
    public Task SingleKeyMode_ModifierChord_IsRejectedAfterTheQueueRuns() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var source = new FakeKeyCaptureSource();
            KeyCaptureBox.CaptureSource = source;
            var box = new KeyCaptureBox { Chord = KeyChord.FromKey(Key.F5) };
            ClickAssign(box);

            source.Press(VK_LCONTROL);
            source.Press(VK_E);
            source.Release(VK_E);
            source.Release(VK_LCONTROL);
            Assert.Equal(KeyCaptureBox.CapturingText, box.DisplayBox.Text);

            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Equal(KeyChord.FromKey(Key.F5), box.Chord);
            Assert.Equal("Press one key without modifiers", box.DisplayBox.Text);
            Assert.False(box.IsCapturing);
            Assert.Equal(1, source.EndCount);
        });

    [Fact]
    public Task Validator_Rejection_KeepsOldValueAndShowsMessage() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var source = new FakeKeyCaptureSource();
            KeyCaptureBox.CaptureSource = source;
            var seen = new List<KeyChord>();
            var box = new KeyCaptureBox
            {
                Chord = KeyChord.FromKey(Key.F5),
                Validator = chord =>
                {
                    seen.Add(chord);
                    return "Another row already uses E";
                }
            };
            ClickAssign(box);

            source.Press(VK_E);
            source.Release(VK_E);
            Assert.Empty(seen);

            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Equal(new[] { KeyChord.FromKey(Key.E) }, seen);
            Assert.Equal(KeyChord.FromKey(Key.F5), box.Chord);
            Assert.Equal("Another row already uses E", box.DisplayBox.Text);
        });

    [Fact]
    public Task Cancel_AfterResultPosted_AppliesNothing() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var source = new FakeKeyCaptureSource();
            KeyCaptureBox.CaptureSource = source;
            var box = new KeyCaptureBox { Chord = KeyChord.FromKey(Key.F5) };
            ClickAssign(box);
            source.Press(VK_E);
            source.Release(VK_E);

            box.CancelCapture();
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Equal(KeyChord.FromKey(Key.F5), box.Chord);
            Assert.Equal("F5", box.DisplayBox.Text);
            Assert.Equal("Assign", box.AssignButton.Content);
            Assert.Equal(1, source.EndCount);
        });

    [Fact]
    public Task Fallback_NoCaptureSource_KeyEventsDriveTheSameResult() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var model = new KeyModel { Key = Key.F5 };
            var box = new KeyCaptureBox();
            box.SetBinding(KeyCaptureBox.ChordProperty, new Binding(nameof(KeyModel.Key))
            {
                Source = model,
                Mode = BindingMode.TwoWay,
                Converter = new KeyChordConverter()
            });
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal("F5", box.DisplayBox.Text);

            box.BeginCapture();
            Assert.False(box.UsesHookCapture);
            box.HandleFallbackKey(Key.F13, isDown: true);
            box.HandleFallbackKey(Key.F13, isDown: true);
            box.HandleFallbackKey(Key.F13, isDown: false);
            Assert.Equal(Key.F5, model.Key);

            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Equal(Key.F13, model.Key);
            Assert.Equal(KeyChord.FromKey(Key.F13), box.Chord);
        });

    [Fact]
    public Task RefusedHookCapture_FallsBackToKeyEvents() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var source = new FakeKeyCaptureSource { Allow = false };
            KeyCaptureBox.CaptureSource = source;
            var box = new KeyCaptureBox { Mode = KeyCaptureMode.Combo };

            box.BeginCapture();
            Assert.Equal(1, source.BeginCount);
            Assert.False(box.UsesHookCapture);
            box.HandleFallbackKey(Key.LeftCtrl, isDown: true);
            box.HandleFallbackKey(Key.LeftAlt, isDown: true);
            box.HandleFallbackKey(Key.E, isDown: true);
            box.HandleFallbackKey(Key.LeftAlt, isDown: false);
            box.HandleFallbackKey(Key.E, isDown: false);
            box.HandleFallbackKey(Key.LeftCtrl, isDown: false);
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Equal(KeyChord.FromKey(Key.E, ModifierKeys.Control | ModifierKeys.Alt), box.Chord);
            Assert.Equal(0, source.EndCount);
        });

    [Fact]
    public Task HookCapture_KeysTheHookLetThrough_StillCount() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            // The hook can end the gate on its own (e.g. a watchdog reinstall). Keys then reach WPF, and the box
            // keeps capturing instead of showing "Capturing keys…" while ignoring them.
            var source = new FakeKeyCaptureSource();
            KeyCaptureBox.CaptureSource = source;
            var box = new KeyCaptureBox { Chord = KeyChord.FromKey(Key.F5) };
            box.BeginCapture();
            Assert.True(box.UsesHookCapture);

            box.HandleFallbackKey(Key.E, isDown: true);
            box.HandleFallbackKey(Key.E, isDown: false);
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.False(box.IsCapturing);
            Assert.Equal(KeyChord.FromKey(Key.E), box.Chord);
        });

    [Fact]
    public Task Capture_ShowsTheKeysPressedSoFar() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var source = new FakeKeyCaptureSource();
            KeyCaptureBox.CaptureSource = source;
            var box = new KeyCaptureBox { Mode = KeyCaptureMode.Combo };
            ClickAssign(box);

            // Each step shows what releasing now would assign, with the side kept throughout.
            source.Press(VK_RCONTROL);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal("RCtrl", box.DisplayBox.Text);

            source.Press(VK_E);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal("RCtrl+E", box.DisplayBox.Text);
            Assert.True(box.IsCapturing);

            source.Release(VK_E);
            source.Release(VK_RCONTROL);
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.False(box.IsCapturing);
            Assert.Equal(KeyChord.FromKey(Key.E, ModifierKeys.Control, rightModifiers: ModifierKeys.Control), box.Chord);
            Assert.Equal("RCtrl+E", box.DisplayBox.Text);
        });

    [Fact]
    public Task EscTap_CancelsAndIsNeverAssigned() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var source = new FakeKeyCaptureSource();
            KeyCaptureBox.CaptureSource = source;
            var box = new KeyCaptureBox { AllowNone = true, Chord = KeyChord.FromKey(Key.F5) };
            ClickAssign(box);

            Assert.Equal(KeyCaptureStatus.Pending, source.Press(VK_ESCAPE));
            // The release ends the hook capture, so the gate stops swallowing keys right away.
            Assert.Equal(KeyCaptureStatus.Finished, source.Release(VK_ESCAPE));
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.False(box.IsCapturing);
            Assert.Equal(KeyChord.FromKey(Key.F5), box.Chord);
            Assert.Equal("F5", box.DisplayBox.Text);
        });

    [Fact]
    public Task EscHeldWhileAnotherKeyIsTapped_KeepsSwallowingAndCancelsOnRelease() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var source = new FakeKeyCaptureSource();
            KeyCaptureBox.CaptureSource = source;
            var box = new KeyCaptureBox { AllowNone = true, Chord = KeyChord.FromKey(Key.F5) };
            ClickAssign(box);

            Assert.Equal(KeyCaptureStatus.Pending, source.Press(VK_ESCAPE));
            Assert.Equal(KeyCaptureStatus.Pending, source.Press(VK_E));
            // E's release would finish the chord, but the held Esc keeps the gate swallowing, so its
            // auto-repeat can't reach the window's Cancel button.
            Assert.Equal(KeyCaptureStatus.Pending, source.Release(VK_E));
            Assert.Equal(KeyCaptureStatus.Pending, source.Press(VK_ESCAPE));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.True(box.IsCapturing);

            Assert.Equal(KeyCaptureStatus.Finished, source.Release(VK_ESCAPE));
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.False(box.IsCapturing);
            Assert.Equal(KeyChord.FromKey(Key.F5), box.Chord);
        });

    [Fact]
    public Task EscHold_OnlyCancels() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var box = new KeyCaptureBox { AllowNone = true, Chord = KeyChord.FromKey(Key.F5) };
            box.BeginCapture();

            box.HandleFallbackKey(Key.Escape, isDown: true);
            await Dispatcher.Yield(DispatcherPriority.Background);
            box.HandleFallbackKey(Key.Escape, isDown: true);
            Assert.Equal(KeyCaptureBox.CapturingText, box.DisplayBox.Text);
            box.HandleFallbackKey(Key.Escape, isDown: false);
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.False(box.IsCapturing);
            Assert.Equal(KeyChord.FromKey(Key.F5), box.Chord);
        });

    [Fact]
    public Task PressOnCancel_WhileCapturing_CancelsAndIsConsumed() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var box = new KeyCaptureBox { Chord = KeyChord.FromKey(Key.F5) };
            box.Measure(new Size(300, 40));
            box.Arrange(new Rect(0, 0, 300, 40));
            box.BeginCapture();

            // Handled on press, so the Button never takes capture or clicks. Its capture release used to cancel
            // and its Click then started a new capture.
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            {
                RoutedEvent = Mouse.PreviewMouseDownEvent
            };
            Assert.True(new Rect(box.AssignButton.RenderSize).Contains(press.GetPosition(box.AssignButton)));
            box.AssignButton.RaiseEvent(press);
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.True(press.Handled);
            Assert.False(box.IsCapturing);
            Assert.Equal("F5", box.DisplayBox.Text);
            Assert.Equal("Assign", box.AssignButton.Content);
        });

    [Fact]
    public Task AllowNone_ShowsClear_AndClearSetsNone() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var model = new KeyModel { Key = Key.F8 };
            var box = new KeyCaptureBox { AllowNone = true };
            box.SetBinding(KeyCaptureBox.ChordProperty, new Binding(nameof(KeyModel.Key))
            {
                Source = model,
                Mode = BindingMode.TwoWay,
                Converter = new KeyChordConverter()
            });
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Assert.Equal("F8", box.DisplayBox.Text);
            Assert.Equal(Visibility.Visible, box.ClearButton.Visibility);

            box.ClearButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal(KeyChord.None, box.Chord);
            Assert.Equal(Key.None, model.Key);
            Assert.Equal("None", box.DisplayBox.Text);
            box.AllowNone = false;
            Assert.Equal(Visibility.Collapsed, box.ClearButton.Visibility);
        });

    [Fact]
    public Task MouseButtons_CapturedOnlyWhenAccepted() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var box = new KeyCaptureBox { AcceptsMouseButtons = true };
            box.BeginCapture();

            Assert.False(box.HandleMouseButton(MouseButton.Left, isDown: true));
            Assert.True(box.HandleMouseButton(MouseButton.XButton1, isDown: true));
            Assert.True(box.HandleMouseButton(MouseButton.XButton1, isDown: false));
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Equal(KeyChord.FromTrigger(InputTrigger.FromMouseButton(MouseButton.XButton1)), box.Chord);
            Assert.Equal("Mouse Button 4", box.DisplayBox.Text);

            box.AcceptsLeftButton = true;
            box.BeginCapture();
            Assert.True(box.HandleMouseButton(MouseButton.Left, isDown: true));
            Assert.True(box.HandleMouseButton(MouseButton.Left, isDown: false));
            await Dispatcher.Yield(DispatcherPriority.Background);
            Assert.Equal(KeyChord.FromTrigger(InputTrigger.FromMouseButton(MouseButton.Left)), box.Chord);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MouseRelease_OverCommandButton_CompletesOwnedPress(bool overClear) =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var box = new KeyCaptureBox { AllowNone = true, AcceptsMouseButtons = true };
            box.Measure(new Size(300, 40));
            box.Arrange(new Rect(0, 0, 300, 40));
            box.BeginCapture();
            Assert.True(box.HandleMouseButton(MouseButton.XButton1, isDown: true));

            var button = overClear ? box.ClearButton : box.AssignButton;
            var release = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.XButton1)
            {
                RoutedEvent = Mouse.PreviewMouseUpEvent
            };
            Assert.True(new Rect(button.RenderSize).Contains(release.GetPosition(button)));
            button.RaiseEvent(release);
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.True(release.Handled);
            Assert.Equal(KeyChord.FromTrigger(InputTrigger.FromMouseButton(MouseButton.XButton1)), box.Chord);
        });

    [Fact]
    public Task MouseButtons_NotAccepted_AreIgnored() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(() =>
        {
            var box = new KeyCaptureBox();
            box.BeginCapture();

            Assert.False(box.HandleMouseButton(MouseButton.Middle, isDown: true));
            Assert.False(box.HandleWheel(MouseWheelDirection.Up));
            box.CancelCapture();
            return Task.CompletedTask;
        });

    [Fact]
    public Task Wheel_WhenAccepted_FinishesOnOneNotch() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
        {
            var box = new KeyCaptureBox { AcceptsWheel = true };
            box.BeginCapture();

            Assert.True(box.HandleWheel(MouseWheelDirection.Down));
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Equal("Wheel Down", box.DisplayBox.Text);
            Assert.False(box.IsCapturing);
        });

    [Fact]
    public Task SecondBox_StartingCapture_CancelsTheFirst() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(() =>
        {
            var source = new FakeKeyCaptureSource();
            KeyCaptureBox.CaptureSource = source;
            var first = new KeyCaptureBox();
            var second = new KeyCaptureBox();

            first.BeginCapture();
            second.BeginCapture();

            Assert.False(first.IsCapturing);
            Assert.True(second.IsCapturing);
            Assert.Same(second, KeyCaptureBox.Current);
            Assert.Equal("None", first.DisplayBox.Text);
            Assert.Equal(2, source.BeginCount);
            Assert.Equal(1, source.EndCount);

            second.CancelCapture();
            Assert.Null(KeyCaptureBox.Current);
            return Task.CompletedTask;
        });

    [Fact]
    public Task Disabling_CancelsCapture() =>
        MacroRecordingLifetimeTests.RunOnStaAsync(() =>
        {
            var box = new KeyCaptureBox();
            box.BeginCapture();

            box.IsEnabled = false;

            Assert.False(box.IsCapturing);
            Assert.Null(KeyCaptureBox.Current);
            box.BeginCapture();
            Assert.False(box.IsCapturing);
            return Task.CompletedTask;
        });

    private static void ClickAssign(KeyCaptureBox box) =>
        box.AssignButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    // Public: WPF bindings resolve properties on public types.
    public sealed class ChordModel
    {
        public KeyChord Shortcut { get; set; }
    }

    public sealed class KeyModel
    {
        public Key Key { get; set; }
    }
}
