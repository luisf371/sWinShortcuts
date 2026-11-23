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
/// A keybind field: <c>[read-only box][Assign]</c>, plus <c>[Clear]</c> when None is allowed. Assign shows
/// "Capturing keys…" until the user presses and releases a key or chord; the result is applied after the
/// input event returns, through <see cref="Validator"/>. While capturing, the keyboard hook routes physical
/// keys only here (<see cref="CaptureSource"/>); without a hook it falls back to WPF key events, where
/// Windows shell shortcuts such as Win+E can still fire.
/// </summary>
public sealed class KeyCaptureBox : UserControl
{
    public const string CapturingText = "Capturing keys…";
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan MessageDuration = TimeSpan.FromMilliseconds(2500);

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
    private readonly Stopwatch _captureClock = new();
    private DispatcherTimer? _timeoutTimer;
    private DispatcherTimer? _messageTimer;
    private Window? _window;
    private bool _capturing;
    private bool _hookCapture;
    private bool _releasingMouseCapture;
    private bool _resultPosted;
    private int _captureId;
    private int _pendingCaptureId;
    private KeyChordResult _pendingResult;

    public KeyCaptureBox()
    {
        _sink = new CaptureSink(this);
        _applyPendingResult = ApplyPendingResult;

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
            Padding = new Thickness(8, 2, 8, 2)
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
        _accumulator.Begin(GetHeldVirtualKeys());
        _capturing = true;
        _resultPosted = false;
        _captureId++;
        Current = this;
        _hookCapture = CaptureSource?.TryBeginKeyCapture(_sink) == true;

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

    // Test seam for the WPF fallback: the Preview key handlers route here.
    internal void HandleFallbackKey(Key key, bool isDown)
    {
        if (!_capturing || _hookCapture || _resultPosted)
        {
            return;
        }

        var virtualKey = key == Key.None ? 0 : KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            return;
        }

        var status = isDown ? _accumulator.OnKeyDown(virtualKey) : _accumulator.OnKeyUp(virtualKey);
        PostIfFinished(status);
    }

    // Test seam for mouse triggers over the box.
    internal bool HandleMouseButton(ModelMouseButton button, bool isDown)
    {
        if (!_capturing || _resultPosted ||
            (isDown ? !AcceptsButton(button) : !_accumulator.IsMouseButtonDown(button)))
        {
            return false;
        }

        PostIfFinished(isDown ? _accumulator.OnMouseDown(button) : _accumulator.OnMouseUp(button));
        return true;
    }

    internal bool HandleWheel(MouseWheelDirection direction)
    {
        if (!_capturing || _resultPosted || !AcceptsWheel)
        {
            return false;
        }

        PostIfFinished(_accumulator.OnWheel(direction));
        return true;
    }

    private bool AcceptsButton(ModelMouseButton button) =>
        AcceptsMouseButtons && (button != ModelMouseButton.Left || AcceptsLeftButton);

    // Runs inside the keyboard hook callback for hook captures: record the result and post; nothing else.
    private void PostIfFinished(KeyCaptureStatus status)
    {
        if (status != KeyCaptureStatus.Finished || _resultPosted)
        {
            return;
        }

        _resultPosted = true;
        _pendingResult = _accumulator.Result;
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
        EndCapture();

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
        _captureClock.Reset();
        if (_window is not null)
        {
            _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        }

        if (ReferenceEquals(Mouse.Captured, this))
        {
            _releasingMouseCapture = true;
            try
            {
                ReleaseMouseCapture();
            }
            finally
            {
                _releasingMouseCapture = false;
            }
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

    private void OnClearClick(object sender, RoutedEventArgs e)
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

        // Hook captures never see these (the hook consumed them); a key held before Assign may still repeat here.
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

        if (IsOver(_assign, e) || IsOver(_clear, e))
        {
            // The buttons keep working: Cancel cancels, Clear clears.
            return;
        }

        e.Handled = true;
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

        // A press accepted by the field owns its release even if the pointer moves over a command button.
        e.Handled = (ToModelButton(e.ChangedButton) is { } button && HandleMouseButton(button, isDown: false)) ||
                    (!IsOver(_assign, e) && !IsOver(_clear, e));
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
        if (!_capturing || _releasingMouseCapture)
        {
            return;
        }

        // A button inside the box takes capture while pressed; anything else means the user went elsewhere.
        if (Mouse.Captured is Visual captured && IsAncestorOf(captured))
        {
            return;
        }

        CancelCapture();
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
        if (elapsed >= HardTimeout || (!_accumulator.HasStarted && elapsed >= IdleTimeout))
        {
            CancelCapture();
        }
    }

    private void ShowIdle()
    {
        StopMessageTimer();
        _box.Text = _capturing ? CapturingText : Chord.ToDisplayText(NoneText ?? "None");
        if (_capturing)
        {
            return;
        }

        _box.ClearValue(Control.BorderBrushProperty);
        _box.ClearValue(Control.ForegroundProperty);
        _box.ClearValue(Control.FontStyleProperty);
        _assign.Content = "Assign";
    }

    private void ShowCapturing()
    {
        _box.Text = CapturingText;
        _box.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
        _box.SetResourceReference(Control.ForegroundProperty, "ForegroundSecondaryBrush");
        _box.FontStyle = FontStyles.Italic;
        _assign.Content = "Cancel";
    }

    private void ShowMessage(string message)
    {
        _box.Text = message;
        _box.ClearValue(Control.BorderBrushProperty);
        _box.Foreground = MessageBrush;
        _box.FontStyle = FontStyles.Italic;
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

    private static void OnAllowNoneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((KeyCaptureBox)d)._clear.Visibility = e.NewValue is true ? Visibility.Visible : Visibility.Collapsed;

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

    // Hook-thread adapter: only updates the accumulator and posts the finished result.
    private sealed class CaptureSink(KeyCaptureBox owner) : IKeyCaptureSink
    {
        public bool HasStarted => owner._accumulator.HasStarted;

        public KeyCaptureStatus OnKey(int virtualKey, bool isDown)
        {
            if (!owner._capturing || !owner._hookCapture || owner._resultPosted)
            {
                return owner._resultPosted ? KeyCaptureStatus.Finished : KeyCaptureStatus.Pending;
            }

            var status = isDown ? owner._accumulator.OnKeyDown(virtualKey) : owner._accumulator.OnKeyUp(virtualKey);
            owner.PostIfFinished(status);
            return status;
        }
    }
}
