using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using sWinShortcuts.Converters;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.ViewModels;

public sealed class MacroStepViewModel : ViewModelBase, IDataErrorInfo
{
    private MacroStep _step;
    private readonly Func<bool> _canEdit;
    private int _number;
    private string _xText;
    private string _yText;
    private string _durationText;
    private string _wheelDeltaText;

    public MacroStepViewModel(MacroStep step, Func<bool> canEdit)
    {
        _step = step;
        _canEdit = canEdit;
        _xText = step.X.ToString(CultureInfo.InvariantCulture);
        _yText = step.Y.ToString(CultureInfo.InvariantCulture);
        _durationText = step.DurationMs.ToString(CultureInfo.InvariantCulture);
        _wheelDeltaText = step.WheelDelta.ToString(CultureInfo.InvariantCulture);
    }

    public event EventHandler? Changed;
    public MacroStep ToModel() => _step;
    public int Number { get => _number; internal set => SetProperty(ref _number, value); }
    public MacroStepKind Kind
    {
        get => _step.Kind;
        set => Change(_step with
        {
            Kind = value,
            Key = _step.Key == Key.None ? Key.A : _step.Key,
            MouseButton = _step.MouseButton ?? sWinShortcuts.Models.MouseButton.Left,
            WheelDelta = _step.WheelDelta == 0 ? 120 : _step.WheelDelta
        });
    }
    public Key Key { get => _step.Key; set => Change(_step with { Key = value }); }
    public MouseButton? MouseButton { get => _step.MouseButton; set => Change(_step with { MouseButton = value }); }
    public int X { get => _step.X; set => XText = value.ToString(CultureInfo.InvariantCulture); }
    public int Y { get => _step.Y; set => YText = value.ToString(CultureInfo.InvariantCulture); }
    public int DurationMs { get => _step.DurationMs; set => DurationText = value.ToString(CultureInfo.InvariantCulture); }
    public int WheelDelta { get => _step.WheelDelta; set => WheelDeltaText = value.ToString(CultureInfo.InvariantCulture); }
    public bool HorizontalWheel { get => _step.HorizontalWheel; set => Change(_step with { HorizontalWheel = value }); }
    public string XText { get => _xText; set => ChangeNumberText(ref _xText, value); }
    public string YText { get => _yText; set => ChangeNumberText(ref _yText, value); }
    public string DurationText { get => _durationText; set => ChangeNumberText(ref _durationText, value); }
    public string WheelDeltaText { get => _wheelDeltaText; set => ChangeNumberText(ref _wheelDeltaText, value); }
    public bool HasKey => Kind is MacroStepKind.KeyPress or MacroStepKind.KeyDown or MacroStepKind.KeyUp;
    public bool HasMouseButton => Kind is MacroStepKind.MouseClick or MacroStepKind.MouseDown or MacroStepKind.MouseUp;
    public bool HasCoordinates => Kind is MacroStepKind.MouseClick or MacroStepKind.MoveTo;
    public bool HasDuration => Kind is MacroStepKind.KeyPress or MacroStepKind.MouseClick or MacroStepKind.Wait;
    public bool HasHold => Kind is MacroStepKind.KeyPress or MacroStepKind.MouseClick;
    public bool IsWheel => Kind == MacroStepKind.MouseWheel;
    public string DurationLabel => Kind == MacroStepKind.Wait ? "Wait" : "Hold";
    public string ActionLabel => Kind switch
    {
        MacroStepKind.KeyPress => "Key press", MacroStepKind.KeyDown => "Key down", MacroStepKind.KeyUp => "Key up",
        MacroStepKind.MouseClick => "Mouse click", MacroStepKind.MouseDown => "Mouse down", MacroStepKind.MouseUp => "Mouse up",
        MacroStepKind.MoveTo => "Move to", MacroStepKind.MouseWheel => "Mouse wheel", _ => "Wait"
    };

    // Row presentation: what the step targets, and how long it holds or waits (kept in its own column).
    public string Detail => Kind switch
    {
        MacroStepKind.KeyPress or MacroStepKind.KeyDown or MacroStepKind.KeyUp => KeyDisplayConverter.ToDisplayText(Key),
        MacroStepKind.MouseClick => $"{ButtonText} at {PositionText}",
        MacroStepKind.MouseDown or MacroStepKind.MouseUp => ButtonText,
        MacroStepKind.MoveTo => PositionText,
        MacroStepKind.MouseWheel => $"{WheelDelta.ToString("+0;-0;0", CultureInfo.InvariantCulture)} {(HorizontalWheel ? "horizontal" : "vertical")}",
        _ => string.Empty
    };
    public string TimingText => Kind switch
    {
        MacroStepKind.KeyPress or MacroStepKind.MouseClick => DurationMs == 0 ? "auto hold" : $"{MillisecondsText} hold",
        MacroStepKind.Wait => MillisecondsText,
        _ => string.Empty
    };
    public string Summary => Detail.Length == 0 ? TimingText : TimingText.Length == 0 ? Detail : $"{Detail} · {TimingText}";
    public string KindHelp => Kind switch
    {
        MacroStepKind.KeyPress => "Presses and releases the key.",
        MacroStepKind.KeyDown => "Holds the key until a later Key up step releases it.",
        MacroStepKind.KeyUp => "Releases a key held by an earlier Key down step.",
        MacroStepKind.MouseClick => "Moves the cursor to the position, then presses and releases the button.",
        MacroStepKind.MouseDown => "Holds the button where the cursor is until a later Mouse up step.",
        MacroStepKind.MouseUp => "Releases a button held by an earlier Mouse down step.",
        MacroStepKind.MoveTo => "Moves the cursor to exact screen pixels at up to 9,000 px/s.",
        MacroStepKind.MouseWheel => "120 is one notch. Negative values scroll down or left.",
        _ => "Pauses before the next step."
    };
    private string ButtonText => MouseButton?.ToString() ?? "Choose button";
    private string PositionText => $"{X.ToString(CultureInfo.InvariantCulture)}, {Y.ToString(CultureInfo.InvariantCulture)}";
    private string MillisecondsText => $"{DurationMs.ToString(CultureInfo.InvariantCulture)} ms";
    public string Error =>
        HasCoordinates && (!IsNumber(XText) || !IsNumber(YText)) ? "Screen coordinates must be signed whole numbers." :
        HasDuration && !IsNumber(DurationText) ? "Duration must be a whole number of milliseconds." :
        IsWheel && !IsNumber(WheelDeltaText) ? "Wheel delta must be a signed whole number." :
        MacroValidation.GetStepError(_step) ?? string.Empty;
    public string this[string columnName] => Error;

    public void SetPosition(int x, int y) => Change(_step with { X = x, Y = y }, string.Empty);

    private void Change(MacroStep value, [CallerMemberName] string? propertyName = null)
    {
        if (!_canEdit() || (value == _step && propertyName != string.Empty)) return;
        var previous = _step;
        _step = value;
        if (previous.X != X || propertyName == string.Empty) _xText = X.ToString(CultureInfo.InvariantCulture);
        if (previous.Y != Y || propertyName == string.Empty) _yText = Y.ToString(CultureInfo.InvariantCulture);
        if (previous.DurationMs != DurationMs) _durationText = DurationMs.ToString(CultureInfo.InvariantCulture);
        if (previous.WheelDelta != WheelDelta) _wheelDeltaText = WheelDelta.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(XText));
        OnPropertyChanged(nameof(YText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(WheelDeltaText));
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(TimingText));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Error));
        if (propertyName == nameof(Kind))
        {
            OnPropertyChanged(nameof(Key));
            OnPropertyChanged(nameof(MouseButton));
            OnPropertyChanged(nameof(WheelDelta));
            OnPropertyChanged(nameof(HasKey));
            OnPropertyChanged(nameof(HasMouseButton));
            OnPropertyChanged(nameof(HasCoordinates));
            OnPropertyChanged(nameof(HasDuration));
            OnPropertyChanged(nameof(HasHold));
            OnPropertyChanged(nameof(IsWheel));
            OnPropertyChanged(nameof(DurationLabel));
            OnPropertyChanged(nameof(KindHelp));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsNumber(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private void ChangeNumberText(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (!_canEdit() || !SetProperty(ref field, value, propertyName)) return;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            _step = propertyName switch
            {
                nameof(XText) => _step with { X = number }, nameof(YText) => _step with { Y = number },
                nameof(DurationText) => _step with { DurationMs = number }, nameof(WheelDeltaText) => _step with { WheelDelta = number },
                _ => _step
            };
        }
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(TimingText));
        OnPropertyChanged(nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
