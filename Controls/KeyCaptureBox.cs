using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using sWinShortcuts.Utilities;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ModelMouseButton = sWinShortcuts.Models.MouseButton;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace sWinShortcuts.Controls;

public enum KeyCaptureMode
{
    /// <summary>One key or trigger; a capture with modifiers is rejected.</summary>
    SingleKey,

    /// <summary>Ctrl/Alt/Shift/Win prefixes plus one key or trigger.</summary>
    Combo
}

/// <summary>
/// A keybind field: <c>[read-only box][Assign]</c>, plus <c>[Clear]</c> when None is allowed. The idle box is
/// dimmed like a disabled field, since only Assign changes it. Assign shows "Capturing keys…", then the keys
/// pressed so far, until the user releases the key or chord; the result is applied after the input event
/// returns, through <see cref="Validator"/>. Esc is assigned only when <see cref="AcceptsEscape"/> is true;
/// otherwise releasing it cancels, and the Clear button sets a field that allows None. While capturing, the keyboard hook routes
/// physical keys only here (<see cref="CaptureSource"/>); without a hook it falls back to WPF key events, where
/// Windows shell shortcuts such as Win+E can still fire.
/// </summary>
public sealed class KeyCaptureBox : UserControl
{
    public const string CapturingText = "Capturing keys…";
    private const string AssignToolTip = "Click, then press the new key. Esc cancels.";
    private const string AssignEscapeToolTip = "Click, then press the new key, Esc included. Click Cancel or outside the box to cancel.";
    internal static readonly TimeSpan MessageDuration = TimeSpan.FromMilliseconds(2500);
    // Close to WPF's own disabled-TextBox look (0.56) while keeping the value text above 4.5:1 contrast.
    internal const double IDLE_OPACITY = 0.6;
    private const int VK_ESCAPE = 0x1B;

    private static readonly Brush MessageBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xAA, 0x00));

    public static readonly DependencyProperty ChordProperty = DependencyProperty.Register(
        nameof(Chord), typeof(KeyChord), typeof(KeyCaptureBox),
        new FrameworkPropertyMetadata(KeyChord.None, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnDisplayPropertyChanged));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(KeyCaptureMode), typeof(KeyCaptureBox), new PropertyMetadata(KeyCaptureMode.SingleKey));

    public static readonly DependencyProperty AcceptsMouseButtonsProperty = DependencyProperty.Register(
        nameof(AcceptsMouseButtons), typeof(bool), typeof(KeyCaptureBox), new PropertyMetadata(false));

    public static readonly DependencyProperty AcceptsLeftButtonProperty = DependencyProperty.Register(
        nameof(AcceptsLeftButton), typeof(bool), typeof(KeyCaptureBox), new PropertyMetadata(false));

    public static readonly DependencyProperty AcceptsWheelProperty = DependencyProperty.Register(
        nameof(AcceptsWheel), typeof(bool), typeof(KeyCaptureBox), new PropertyMetadata(false));

    public static readonly DependencyProperty AcceptsEscapeProperty = DependencyProperty.Register(
        nameof(AcceptsEscape), typeof(bool), typeof(KeyCaptureBox), new PropertyMetadata(false, OnAcceptsEscapeChanged));

    public static readonly DependencyProperty AllowNoneProperty = DependencyProperty.Register(
        nameof(AllowNone), typeof(bool), typeof(KeyCaptureBox), new PropertyMetadata(false, OnAllowNoneChanged));

    public static readonly DependencyProperty NoneTextProperty = DependencyProperty.Register(
        nameof(NoneText), typeof(string), typeof(KeyCaptureBox), new PropertyMetadata("None", OnDisplayPropertyChanged));

    public static readonly DependencyProperty ValidatorProperty = DependencyProperty.Register(
        nameof(Validator), typeof(Func<KeyChord, string>), typeof(KeyCaptureBox), new PropertyMetadata(null));

    public static readonly DependencyProperty AutomationNameProperty = DependencyProperty.Register(
        nameof(AutomationName), typeof(string), typeof(KeyCaptureBox), new PropertyMetadata(string.Empty, OnAutomationNameChanged));

    public static readonly DependencyProperty BoxStyleProperty = DependencyProperty.Register(
        nameof(BoxStyle), typeof(Style), typeof(KeyCaptureBox), new PropertyMetadata(null, OnPartStyleChanged));

    public static readonly DependencyProperty ButtonStyleProperty = DependencyProperty.Register(
        nameof(ButtonStyle), typeof(Style), typeof(KeyCaptureBox), new PropertyMetadata(null, OnPartStyleChanged));

    private readonly TextBox _box;
    private readonly Button _assign;
    private readonly Button _clear;
    private readonly KeyChordAccumulator _accumulator = new();
    private readonly CaptureSink _sink;
    private readonly Action _applyPendingResult;
    private readonly Action _showProgress;
    private readonly Stopwatch _captureClock = new();
    private DispatcherTimer? _timeoutTimer;
    private DispatcherTimer? _messageTimer;
    private Window? _window;
    private bool _capturing;
    private bool _hookCapture;
    private bool _resultPosted;
    private bool _progressPosted;
    // A held Esc cancels on its release, whatever else is pressed meanwhile.
    private bool _escapeHeld;
    // AcceptsEscape copied at capture start: the hook callback runs off the UI thread and can't read the property.
    private bool _escapeIsKey;
    private bool _pendingCancel;
    private int _captureId;
    private int _pendingCaptureId;
    private KeyChordResult _pendingResult;

    public KeyCaptureBox()
    {
        _sink = new CaptureSink(this);
        _applyPendingResult = ApplyPendingResult;
        _showProgress = ShowProgress;

        _box = new TextBox
        {
            IsReadOnly = true,
            Focusable = false,
            IsTabStop = false,
            Cursor = Cursors.Arrow,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        ContextMenuService.SetIsEnabled(_box, false);
        AutomationProperties.SetLiveSetting(_box, AutomationLiveSetting.Polite);

        _assign = new Button
        {
            Content = "Assign",
            MinWidth = 64,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            ToolTip = AssignToolTip
        };
        _assign.Click += OnAssignClick;

        _clear = new Button
        {
            Content = "✕",
            MinWidth = 28,
            Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(6, 2, 6, 2),
            ToolTip = "Clear",
            Visibility = Visibility.Collapsed
        };
        _clear.Click += OnClearClick;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_assign, 1);
        Grid.SetColumn(_clear, 2);
        grid.Children.Add(_box);
        grid.Children.Add(_assign);
        grid.Children.Add(_clear);
        Content = grid;

        IsTabStop = false;
        Focusable = false;
        ApplyAutomationNames(string.Empty);
        ShowIdle();

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
        PreviewMouseDown += OnPreviewMouseDown;
        PreviewMouseUp += OnPreviewMouseUp;
        PreviewMouseWheel += OnPreviewMouseWheel;
        LostMouseCapture += OnLostMouseCapture;
        Unloaded += (_, _) => CancelCapture();
        IsEnabledChanged += (_, e) =>
        {
            if (e.NewValue is false) CancelCapture();
        };
        DataContextChanged += (_, _) => CancelCapture();
    }

    /// <summary>
    /// The keyboard-hook capture route, assigned by <c>App.OnStartup</c>. Null (tests, design time, or a hook
    /// that refuses the capture) uses the WPF key-event fallback.
    /// </summary>
    public static IKeyCaptureSource? CaptureSource { get; set; }

    // Per UI thread: boxes on different dispatchers (STA test threads) never cancel each other across threads.
    [ThreadStatic]
    private static KeyCaptureBox? t_current;

    /// <summary>The one box capturing on this UI thread; starting another capture cancels it.</summary>
    public static KeyCaptureBox? Current
    {
        get => t_current;
        private set => t_current = value;
    }

    public KeyChord Chord
    {
        get => (KeyChord)GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    public KeyCaptureMode Mode
    {
        get => (KeyCaptureMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>Middle, Right, X1 and X2 pressed over the box can be captured; Left needs <see cref="AcceptsLeftButton"/>.</summary>
    public bool AcceptsMouseButtons
    {
        get => (bool)GetValue(AcceptsMouseButtonsProperty);
        set => SetValue(AcceptsMouseButtonsProperty, value);
    }

    public bool AcceptsLeftButton
    {
        get => (bool)GetValue(AcceptsLeftButtonProperty);
        set => SetValue(AcceptsLeftButtonProperty, value);
    }

    public bool AcceptsWheel
    {
        get => (bool)GetValue(AcceptsWheelProperty);
        set => SetValue(AcceptsWheelProperty, value);
    }

    /// <summary>
    /// Esc is captured like any other key instead of cancelling. Only macro step keys use it; every other field
    /// keeps Esc as cancel, so Esc can't be assigned there.
    /// </summary>
    public bool AcceptsEscape
    {
        get => (bool)GetValue(AcceptsEscapeProperty);
        set => SetValue(AcceptsEscapeProperty, value);
    }

    /// <summary>Shows the Clear button, which sets the chord to None.</summary>
    public bool AllowNone
    {
        get => (bool)GetValue(AllowNoneProperty);
        set => SetValue(AllowNoneProperty, value);
    }

    public string NoneText
    {
        get => (string)GetValue(NoneTextProperty);
        set => SetValue(NoneTextProperty, value);
    }

    /// <summary>Field rule: null accepts the captured chord; otherwise the text explains the rejection.</summary>
    public Func<KeyChord, string?>? Validator
    {
        get => (Func<KeyChord, string?>?)GetValue(ValidatorProperty);
        set => SetValue(ValidatorProperty, value);
    }

    public string AutomationName
    {
        get => (string)GetValue(AutomationNameProperty);
        set => SetValue(AutomationNameProperty, value);
    }

    public Style? BoxStyle
    {
        get => (Style?)GetValue(BoxStyleProperty);
        set => SetValue(BoxStyleProperty, value);
    }

    public Style? ButtonStyle
    {
        get => (Style?)GetValue(ButtonStyleProperty);
        set => SetValue(ButtonStyleProperty, value);
    }

    internal TextBox DisplayBox => _box;
    internal Button AssignButton => _assign;
    internal Button ClearButton => _clear;
    internal bool IsCapturing => _capturing;
    internal bool UsesHookCapture => _hookCapture;

    /// <summary>Starts a capture, as the Assign button does.</summary>
    public void BeginCapture()
    {
        if (_capturing || !IsEnabled)
        {
            return;
        }

        if (Current is { } previous && !ReferenceEquals(previous, this))
        {
            previous.CancelCapture();
        }

        StopMessageTimer();
        _capturing = true;
        _resultPosted = false;
        _escapeIsKey = AcceptsEscape;
        _captureId++;
        Current = this;
        _hookCapture = CaptureSource?.TryBeginKeyCapture(_sink) == true;
        // The hook filters keys held before Assign by itself; only the WPF fallback needs this snapshot.
        _accumulator.Begin(_hookCapture ? ReadOnlySpan<int>.Empty : GetHeldVirtualKeys());

        // Keyboard focus stays inside the box, so the WPF fallback receives keys and Space/Enter can't click Cancel.
        _assign.Focus();
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.Deactivated += OnWindowDeactivated;
        }

        Mouse.Capture(this, CaptureMode.SubTree);
        _captureClock.Restart();
        _timeoutTimer ??= CreateTimer(TimeSpan.FromSeconds(1), OnTimeoutTick);
        _timeoutTimer.Start();
        ShowCapturing();
    }

    /// <summary>Ends a running capture and keeps the current value.</summary>
    public void CancelCapture()
    {
        if (!_capturing)
        {
            return;
        }

        EndCapture();
        ShowIdle();
    }

    // WPF key events: the whole capture in the fallback, and in a hook capture only what the hook let through
    // (a held key's release, or every key once the hook ended the gate), so no edge is counted twice.
    // Also the test seam for both.
    internal void HandleFallbackKey(Key key, bool isDown)
    {
        if (!_capturing || _resultPosted)
        {
            return;
        }

        var virtualKey = key == Key.None ? 0 : KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey != 0)
        {
            HandleKeyEdge(virtualKey, isDown);
        }
    }

    // Both key routes land here; for hook captures this runs inside the keyboard hook callback.
    private KeyCaptureStatus HandleKeyEdge(int virtualKey, bool isDown)
    {
        if (virtualKey == VK_ESCAPE && !_escapeIsKey)
        {
            return HandleEscape(isDown);
        }

        OnCaptureEdge(isDown ? _accumulator.OnKeyDown(virtualKey) : _accumulator.OnKeyUp(virtualKey));
        // The gate keeps swallowing keys until a result is posted, so a held Esc stays out of the window.
        return _resultPosted ? KeyCaptureStatus.Finished : KeyCaptureStatus.Pending;
    }

    // Esc is never assigned and only cancels. Its release decides, so the gate swallows Esc until then and its
    // auto-repeat can't reach the window (IsCancel).
    private KeyCaptureStatus HandleEscape(bool isDown)
    {
        if (isDown)
        {
            _escapeHeld = true;
            return KeyCaptureStatus.Pending;
        }

        if (!_escapeHeld)
        {
            // Pressed before the capture began.
            return KeyCaptureStatus.Pending;
        }

        PostResult(default, cancel: true);
        return KeyCaptureStatus.Finished;
    }

    // Esc held counts as input, so neither timeout drops a capture while the user decides.
    private bool HasInput => _accumulator.HasStarted || _escapeHeld;

    // Test seam for mouse triggers over the box.
    internal bool HandleMouseButton(ModelMouseButton button, bool isDown)
    {
        if (!_capturing || _resultPosted ||
            (isDown ? !AcceptsButton(button) : !_accumulator.IsMouseButtonDown(button)))
        {
            return false;
        }

        OnCaptureEdge(isDown ? _accumulator.OnMouseDown(button) : _accumulator.OnMouseUp(button));
        return true;
    }

    internal bool HandleWheel(MouseWheelDirection direction)
    {
        if (!_capturing || _resultPosted || !AcceptsWheel)
        {
            return false;
        }

        OnCaptureEdge(_accumulator.OnWheel(direction));
        return true;
    }

    private bool AcceptsButton(ModelMouseButton button) =>
        AcceptsMouseButtons && (button != ModelMouseButton.Left || AcceptsLeftButton);

    // Runs inside the keyboard hook callback for hook captures: record and post only, never touch the UI here.
    private void OnCaptureEdge(KeyCaptureStatus status)
    {
        if (_resultPosted)
        {
            return;
        }

        if (status == KeyCaptureStatus.Finished)
        {
            // With Esc held, its release cancels instead.
            if (!_escapeHeld)
            {
                PostResult(_accumulator.Result, cancel: false);
            }
        }
        else if (!_progressPosted && _accumulator.HasStarted)
        {
            // One refresh at a time; it reads the latest state, so auto-repeat can't flood the queue.
            _progressPosted = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, _showProgress);
        }
    }

    private void PostResult(KeyChordResult result, bool cancel)
    {
        _resultPosted = true;
        _pendingResult = result;
        _pendingCancel = cancel;
        _pendingCaptureId = _captureId;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, _applyPendingResult);
    }

    private void ApplyPendingResult()
    {
        // A capture cancelled (or restarted) after its result was posted drops that result.
        if (!_capturing || _pendingCaptureId != _captureId)
        {
            return;
        }

        var result = _pendingResult;
        var cancel = _pendingCancel;
        EndCapture();
        if (cancel)
        {
            ShowIdle();
            return;
        }

        var message = GetRejection(result);
        if (message is not null)
        {
            ShowMessage(message);
            return;
        }

        SetCurrentValue(ChordProperty, result.Chord);
        ShowIdle();
    }

    private string? GetRejection(KeyChordResult result)
    {
        if (!result.IsCompleted)
        {
            return result.Rejection == KeyChordRejection.MultipleKeys ? "Press only one key" : "That key can't be assigned";
        }

        var chord = result.Chord;
        if (Mode == KeyCaptureMode.SingleKey && chord.Modifiers != ModifierKeys.None)
        {
            return "Press one key without modifiers";
        }

        return Validator?.Invoke(chord);
    }

    private void EndCapture()
    {
        _capturing = false;
        _captureId++;
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }

        if (_hookCapture)
        {
            CaptureSource?.EndKeyCapture(_sink);
            _hookCapture = false;
        }

        _timeoutTimer?.Stop();
        _escapeHeld = false;
        _captureClock.Reset();
        if (_window is not null)
        {
            _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        }

        if (ReferenceEquals(Mouse.Captured, this))
        {
            // Raises LostMouseCapture with _capturing already false, so OnLostMouseCapture ignores it.
            ReleaseMouseCapture();
        }
    }

    private void OnAssignClick(object sender, RoutedEventArgs e)
    {
        if (_capturing)
        {
            CancelCapture();
        }
        else
        {
            BeginCapture();
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => ClearChord();

    private void ClearChord()
    {
        CancelCapture();
        StopMessageTimer();
        SetCurrentValue(ChordProperty, KeyChord.None);
        ShowIdle();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        // Keys the hook swallowed never arrive here.
        e.Handled = true;
        HandleFallbackKey(GetRealKey(e), isDown: true);
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        e.Handled = true;
        HandleFallbackKey(GetRealKey(e), isDown: false);
    }

    private static Key GetRealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key
    };

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        e.Handled = true;
        var overAssign = IsOver(_assign, e);
        if (overAssign || IsOver(_clear, e))
        {
            // Act on the press: a Button releases mouse capture before raising Click, so letting Cancel click
            // normally would cancel on the capture loss and then start a new capture on the Click.
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                if (overAssign)
                {
                    CancelCapture();
                }
                else
                {
                    ClearChord();
                }
            }

            return;
        }

        if (!IsOver(this, e))
        {
            CancelCapture();
            return;
        }

        if (ToModelButton(e.ChangedButton) is { } button)
        {
            HandleMouseButton(button, isDown: true);
        }
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        // The command buttons act on press while capturing, so every release belongs to the capture, even one
        // that ends over a button.
        e.Handled = true;
        if (ToModelButton(e.ChangedButton) is { } button)
        {
            HandleMouseButton(button, isDown: false);
        }
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_capturing)
        {
            return;
        }

        e.Handled = true;
        if (e.Delta != 0 && IsOver(this, e))
        {
            HandleWheel(e.Delta > 0 ? MouseWheelDirection.Up : MouseWheelDirection.Down);
        }
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        // Every press inside the box is handled while capturing, so no child takes capture: a loss means the
        // user went elsewhere.
        if (_capturing)
        {
            CancelCapture();
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e) => CancelCapture();

    private void OnTimeoutTick(object? sender, EventArgs e)
    {
        if (!_capturing)
        {
            _timeoutTimer?.Stop();
            return;
        }

        var elapsed = _captureClock.Elapsed;
        if (elapsed >= KeyCaptureGate.HardLimit || (!HasInput && elapsed >= KeyCaptureGate.IdleLimit))
        {
            CancelCapture();
        }
    }

    private void ShowIdle()
    {
        StopMessageTimer();
        _box.Text = Chord.ToDisplayText(NoneText ?? "None");
        _box.ClearValue(Control.BorderBrushProperty);
        _box.ClearValue(Control.ForegroundProperty);
        _box.ClearValue(Control.FontStyleProperty);
        _box.Opacity = IDLE_OPACITY;
        _assign.Content = "Assign";
    }

    private void ShowCapturing()
    {
        _box.Text = CapturingText;
        _box.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
        _box.SetResourceReference(Control.ForegroundProperty, "ForegroundSecondaryBrush");
        _box.FontStyle = FontStyles.Italic;
        _box.Opacity = 1;
        _assign.Content = "Cancel";
    }

    private void ShowProgress()
    {
        _progressPosted = false;
        var chord = _accumulator.Progress;
        // A held Esc will cancel, so the keys pressed with it are not shown as the result.
        if (!_capturing || _resultPosted || _escapeHeld || chord.IsEmpty)
        {
            return;
        }

        // The keys pressed so far read as real input, not as the placeholder.
        _box.Text = chord.ToDisplayText();
        _box.ClearValue(Control.ForegroundProperty);
        _box.ClearValue(Control.FontStyleProperty);
    }

    private void ShowMessage(string message)
    {
        _box.Text = message;
        _box.ClearValue(Control.BorderBrushProperty);
        _box.Foreground = MessageBrush;
        _box.FontStyle = FontStyles.Italic;
        _box.Opacity = 1;
        _assign.Content = "Assign";
        _messageTimer ??= CreateTimer(MessageDuration, (_, _) => ShowIdle());
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    private void StopMessageTimer() => _messageTimer?.Stop();

    private DispatcherTimer CreateTimer(TimeSpan interval, EventHandler tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = interval };
        timer.Tick += tick;
        return timer;
    }

    private void ApplyAutomationNames(string name)
    {
        var label = string.IsNullOrWhiteSpace(name) ? "key" : name;
        AutomationProperties.SetName(this, name);
        AutomationProperties.SetName(_box, label);
        AutomationProperties.SetName(_assign, $"Assign {label}");
        AutomationProperties.SetName(_clear, $"Clear {label}");
    }

    private static int[] GetHeldVirtualKeys()
    {
        // Keys already down when Assign is clicked: their release must not count as part of the chord.
        Span<int> held = stackalloc int[256];
        var count = 0;
        for (var vk = 1; vk < 256; vk++)
        {
            var key = KeyInterop.KeyFromVirtualKey(vk);
            if (key != Key.None && Keyboard.IsKeyDown(key))
            {
                held[count++] = vk;
            }
        }

        return held[..count].ToArray();
    }

    private static bool IsOver(UIElement element, MouseEventArgs e)
    {
        if (element.Visibility != Visibility.Visible)
        {
            return false;
        }

        var position = e.GetPosition(element);
        return new Rect(element.RenderSize).Contains(position);
    }

    private static ModelMouseButton? ToModelButton(System.Windows.Input.MouseButton button) => button switch
    {
        System.Windows.Input.MouseButton.Left => ModelMouseButton.Left,
        System.Windows.Input.MouseButton.Right => ModelMouseButton.Right,
        System.Windows.Input.MouseButton.Middle => ModelMouseButton.Middle,
        System.Windows.Input.MouseButton.XButton1 => ModelMouseButton.XButton1,
        System.Windows.Input.MouseButton.XButton2 => ModelMouseButton.XButton2,
        _ => null
    };

    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void OnDisplayPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (KeyCaptureBox)d;
        if (!box._capturing && box._messageTimer?.IsEnabled != true)
        {
            box.ShowIdle();
        }
    }

    private static void OnAllowNoneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (KeyCaptureBox)d;
        box._clear.Visibility = e.NewValue is true ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnAcceptsEscapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KeyCaptureBox)d)._assign.ToolTip = e.NewValue is true ? AssignEscapeToolTip : AssignToolTip;

    private static void OnAutomationNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KeyCaptureBox)d).ApplyAutomationNames(e.NewValue as string ?? string.Empty);

    private static void OnPartStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (KeyCaptureBox)d;
        if (e.Property == BoxStyleProperty)
        {
            SetStyle(box._box, e.NewValue as Style);
        }
        else
        {
            SetStyle(box._assign, e.NewValue as Style);
            SetStyle(box._clear, e.NewValue as Style);
        }
    }

    private static void SetStyle(FrameworkElement element, Style? style)
    {
        if (style is null)
        {
            element.ClearValue(StyleProperty);
        }
        else
        {
            element.Style = style;
        }
    }

    // Hook-thread adapter: only updates the capture state and posts UI work.
    private sealed class CaptureSink(KeyCaptureBox owner) : IKeyCaptureSink
    {
        public bool HasStarted => owner.HasInput;

        public KeyCaptureStatus OnKey(int virtualKey, bool isDown)
        {
            if (!owner._capturing || !owner._hookCapture || owner._resultPosted)
            {
                return owner._resultPosted ? KeyCaptureStatus.Finished : KeyCaptureStatus.Pending;
            }

            return owner.HandleKeyEdge(virtualKey, isDown);
        }
    }
}
