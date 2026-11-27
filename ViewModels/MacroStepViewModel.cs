using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
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
    private readonly Func<MacroStepViewModel, (int X, int Y)?> _positionBefore;
    private readonly Func<MacroStepViewModel, int> _savedSlotBefore;
    private (int X, int Y)? _lastPosition;
    private (int X, int Y)? _lastOffset;
    private MacroStepKind _lastOffsetKind = MacroStepKind.MoveCursorBy;
    private int _number;
    private string _xText;
    private string _yText;
    private string _durationText;
    private string _wheelDeltaText;
    private MacroStepViewModel? _collapsedWait;
    private int _collapsedDurationMs;
    private string _collapsedError = string.Empty;
    private bool _isPressHold;
    private string _problem = string.Empty;
    private bool _isWaitTarget;

    public static IReadOnlyList<KeyValuePair<int, string>> SlotOptions { get; } =
        Enumerable.Range(1, MacroValidation.MaxPositionSlots)
            .Select(slot => new KeyValuePair<int, string>(slot, FormatSlot(slot))).ToArray();

    public MacroStepViewModel(MacroStep step, Func<bool> canEdit, Func<MacroStepViewModel, (int X, int Y)?> positionBefore,
        Func<MacroStepViewModel, int> savedSlotBefore)
    {
        _step = step;
        _canEdit = canEdit;
        _positionBefore = positionBefore;
        _savedSlotBefore = savedSlotBefore;
        _xText = step.X.ToString(CultureInfo.InvariantCulture);
        _yText = step.Y.ToString(CultureInfo.InvariantCulture);
        _durationText = step.DurationMs.ToString(CultureInfo.InvariantCulture);
        _wheelDeltaText = step.WheelDelta.ToString(CultureInfo.InvariantCulture);
    }

    public event EventHandler? Changed;
    public MacroStep ToModel() => _step;
    public int Number
    {
        get => _number;
        internal set
        {
            if (!SetProperty(ref _number, value)) return;
            OnPropertyChanged(nameof(DisplayNumber));
            OnPropertyChanged(nameof(AutomationStepLabel));
        }
    }
    public MacroStepKind Kind
    {
        get => _step.Kind;
        set
        {
            if (!_canEdit()) return;
            if (HasOffset && Kind != value) _lastOffsetKind = Kind;

            var isPosition = value is MacroStepKind.MoveTo or MacroStepKind.MouseClick;
            var isOffset = value is MacroStepKind.MoveCursorBy or MacroStepKind.MoveMouseBy;
            var groupChanged = HasCoordinates != isPosition || HasOffset != isOffset;
            var coordinates = (X, Y);
            if (groupChanged)
            {
                if (HasCoordinates) _lastPosition = coordinates;
                if (HasOffset) _lastOffset = coordinates;

                if (isPosition) coordinates = _lastPosition ?? _positionBefore(this) ?? (0, 0);
                if (isOffset) coordinates = _lastOffset ?? (0, 0);
            }

            var slot = Slot;
            if (slot == 0)
            {
                if (value == MacroStepKind.SavePosition) slot = 1;
                if (value == MacroStepKind.MoveToSaved) slot = _savedSlotBefore(this);
            }

            Change(_step with
            {
                Kind = value,
                Slot = slot,
                X = coordinates.X,
                Y = coordinates.Y,
                Key = _step.Key == Key.None ? Key.A : _step.Key,
                MouseButton = _step.MouseButton ?? sWinShortcuts.Models.MouseButton.Left,
                WheelDelta = _step.WheelDelta == 0 ? 120 : _step.WheelDelta,
                // A 0 ms Wait does nothing (an auto-hold Key press converts to one); start it like a new Wait step.
                DurationMs = value == MacroStepKind.Wait && _step.DurationMs == 0 ? MacroViewModel.DEFAULT_WAIT_MS : _step.DurationMs
            }, resetCoordinateText: groupChanged);
        }
    }
    public int Slot { get => _step.Slot; set => Change(_step with { Slot = value }); }
    public Key Key { get => _step.Key; set => Change(_step with { Key = value }); }
    public MouseButton? MouseButton { get => _step.MouseButton; set => Change(_step with { MouseButton = value }); }
    public int X { get => _step.X; set => XText = value.ToString(CultureInfo.InvariantCulture); }
    public int Y { get => _step.Y; set => YText = value.ToString(CultureInfo.InvariantCulture); }
    public int DurationMs { get => _step.DurationMs; set => DurationText = value.ToString(CultureInfo.InvariantCulture); }
    public int WheelDelta { get => _step.WheelDelta; set => WheelDeltaText = value.ToString(CultureInfo.InvariantCulture); }
    public bool HorizontalWheel { get => _step.HorizontalWheel; set => Change(_step with { HorizontalWheel = value }); }
    // Wheel direction picker: the radio being cleared writes false, which is ignored.
    public bool IsVerticalWheel { get => !HorizontalWheel; set { if (value) HorizontalWheel = false; } }
    public bool IsHorizontalWheel { get => HorizontalWheel; set { if (value) HorizontalWheel = true; } }
    public string XText { get => _xText; set => ChangeNumberText(ref _xText, value); }
    public string YText { get => _yText; set => ChangeNumberText(ref _yText, value); }
    public string DurationText { get => _durationText; set => ChangeNumberText(ref _durationText, value); }
    public string WheelDeltaText { get => _wheelDeltaText; set => ChangeNumberText(ref _wheelDeltaText, value); }
    public bool HasKey => Kind is MacroStepKind.KeyPress or MacroStepKind.KeyDown or MacroStepKind.KeyUp;
    public bool HasMouseButton => Kind is MacroStepKind.MouseClick or MacroStepKind.MouseDown or MacroStepKind.MouseUp;
    public bool HasCoordinates => Kind is MacroStepKind.MouseClick or MacroStepKind.MoveTo;
    public bool HasOffset => Kind is MacroStepKind.MoveCursorBy or MacroStepKind.MoveMouseBy;
    public bool HasSlot => Kind is MacroStepKind.SavePosition or MacroStepKind.MoveToSaved;
    public bool IsMove => Kind is MacroStepKind.MoveTo or MacroStepKind.MoveCursorBy or MacroStepKind.MoveMouseBy or MacroStepKind.MoveToSaved;
    public MacroStepKind ActionChoice
    {
        get => IsMove ? MacroStepKind.MoveTo : Kind;
        set
        {
            if (value != MacroStepKind.MoveTo || !IsMove) Kind = value;
        }
    }
    public bool MoveIsPosition { get => Kind == MacroStepKind.MoveTo; set { if (value) Kind = MacroStepKind.MoveTo; } }
    public bool MoveIsOffset { get => HasOffset; set { if (value && !HasOffset) Kind = _lastOffsetKind; } }
    public bool MoveIsSaved { get => Kind == MacroStepKind.MoveToSaved; set { if (value) Kind = MacroStepKind.MoveToSaved; } }
    public bool MoveAsPixels { get => Kind == MacroStepKind.MoveCursorBy; set { if (value) Kind = MacroStepKind.MoveCursorBy; } }
    public bool MoveAsRaw { get => Kind == MacroStepKind.MoveMouseBy; set { if (value) Kind = MacroStepKind.MoveMouseBy; } }
    public bool HasHeldButtons => IsMove;
    public MacroHeldButtonPolicy HeldButtons
    {
        get => _step.HeldButtons;
        set
        {
            Change(_step with { HeldButtons = value });
            OnPropertyChanged(nameof(HeldButtonsStop));
            OnPropertyChanged(nameof(HeldButtonsWait));
            OnPropertyChanged(nameof(HeldButtonsAllow));
        }
    }
    // Radio buttons: WPF also writes false to the one being unchecked, which is ignored.
    public bool HeldButtonsStop { get => HeldButtons == MacroHeldButtonPolicy.Stop; set { if (value) HeldButtons = MacroHeldButtonPolicy.Stop; } }
    public bool HeldButtonsWait { get => HeldButtons == MacroHeldButtonPolicy.Wait; set { if (value) HeldButtons = MacroHeldButtonPolicy.Wait; } }
    public bool HeldButtonsAllow { get => HeldButtons == MacroHeldButtonPolicy.Allow; set { if (value) HeldButtons = MacroHeldButtonPolicy.Allow; } }
    public bool HasDuration => Kind is MacroStepKind.KeyPress or MacroStepKind.MouseClick or MacroStepKind.Wait;
    public bool HasHold => Kind is MacroStepKind.KeyPress or MacroStepKind.MouseClick;
    public bool IsWheel => Kind == MacroStepKind.MouseWheel;
    public string DurationLabel => Kind == MacroStepKind.Wait ? "Wait" : "Hold";
    public string ActionLabel => ActionLabelOf(Kind);
    internal static string ActionLabelOf(MacroStepKind kind) => kind switch
    {
        // Old one-step presses no longer play; the label tells the user which rows to replace.
        MacroStepKind.KeyPress => "Old key press", MacroStepKind.KeyDown => "Key down", MacroStepKind.KeyUp => "Key up",
        MacroStepKind.MouseClick => "Old mouse click", MacroStepKind.MouseDown => "Mouse down", MacroStepKind.MouseUp => "Mouse up",
        MacroStepKind.MoveTo or MacroStepKind.MoveCursorBy or MacroStepKind.MoveMouseBy or MacroStepKind.MoveToSaved => "Move",
        MacroStepKind.SavePosition => "Save position",
        MacroStepKind.MouseWheel => "Mouse wheel",
        MacroStepKind.FreezeMouse => "Freeze mouse", MacroStepKind.UnfreezeMouse => "Unfreeze mouse", _ => "Wait"
    };

    // Row presentation: what the step targets, and how long it holds or waits (kept in its own column).
    public string Detail => DetailOf(_step);
    // Also the cursor badge's wording for the playing step (MacroStepOverlayService).
    internal static string DetailOf(MacroStep step) => step.Kind switch
    {
        MacroStepKind.KeyPress or MacroStepKind.KeyDown or MacroStepKind.KeyUp => KeyDisplayConverter.ToDisplayText(step.Key),
        MacroStepKind.MouseClick => $"{ButtonText(step.MouseButton)} at {PositionText(step)}",
        MacroStepKind.MouseDown or MacroStepKind.MouseUp => ButtonText(step.MouseButton),
        MacroStepKind.MoveTo => PositionText(step),
        MacroStepKind.SavePosition => FormatSlot(step.Slot),
        MacroStepKind.MoveToSaved => $"to saved {FormatSlot(step.Slot)}",
        MacroStepKind.MoveCursorBy or MacroStepKind.MoveMouseBy =>
            FormattableString.Invariant($"{step.X:+0;-0;0}, {step.Y:+0;-0;0} {(step.Kind == MacroStepKind.MoveCursorBy ? "px" : "raw")}"),
        MacroStepKind.MouseWheel => $"{step.WheelDelta.ToString("+0;-0;0", CultureInfo.InvariantCulture)} {(step.HorizontalWheel ? "horizontal" : "vertical")}",
        _ => string.Empty
    };
    public string TimingText => Kind switch
    {
        MacroStepKind.KeyPress or MacroStepKind.MouseClick => DurationMs == 0 ? "auto hold" : $"{MillisecondsText} hold",
        MacroStepKind.Wait => IsPressHold ? $"{MillisecondsText} hold" : MillisecondsText,
        _ when HasHeldButtons && HeldButtons == MacroHeldButtonPolicy.Wait => "waits for buttons",
        _ when HasHeldButtons && HeldButtons == MacroHeldButtonPolicy.Allow => "moves while held",
        _ => string.Empty
    };
    // A Wait that holds a press (between its Down and matching Up) rather than pausing between steps.
    public bool IsPressHold => _isPressHold;
    public MacroStepViewModel? CollapsedWait => _collapsedWait;
    public bool IsCollapsedPress => CollapsedWait is not null;
    public int SourceStepCount => IsCollapsedPress ? 3 : 1;
    public string DisplayNumber => IsCollapsedPress
        ? $"{Number.ToString(CultureInfo.InvariantCulture)}–{(Number + 2).ToString(CultureInfo.InvariantCulture)}"
        : Number.ToString(CultureInfo.InvariantCulture);
    public string AutomationStepLabel => IsCollapsedPress
        ? $"Steps {Number.ToString(CultureInfo.InvariantCulture)} to {(Number + 2).ToString(CultureInfo.InvariantCulture)}"
        : $"Step {Number.ToString(CultureInfo.InvariantCulture)}";
    public string DisplayActionLabel => IsCollapsedPress ? (HasKey ? "Key press" : "Mouse click") : ActionLabel;
    public string DisplayTimingText => IsCollapsedPress
        ? $"{_collapsedDurationMs.ToString(CultureInfo.InvariantCulture)} ms hold"
        : TimingText;
    // A row's own field error first, else the macro's playback problem when it names this row.
    public string DisplayError => IsCollapsedPress ? _collapsedError : Error.Length > 0 ? Error : _problem;
    public bool IsProblemOnly => !IsCollapsedPress && Error.Length == 0 && _problem.Length > 0;
    public string Summary => Detail.Length == 0 ? TimingText : TimingText.Length == 0 ? Detail : $"{Detail} · {TimingText}";
    public string KindHelp => Kind switch
    {
        MacroStepKind.KeyPress => "This old step no longer plays. Note its key and hold, delete it, then add a Key press from Add step.",
        MacroStepKind.KeyDown => "Holds the key until a later Key up step releases it.",
        MacroStepKind.KeyUp => "Releases a key held by an earlier Key down step.",
        MacroStepKind.MouseClick => "This old step no longer plays. Note its button and hold, delete it, then add a Mouse click " +
            "from Add step. Add a Move to its position first.",
        MacroStepKind.MouseDown => "Holds the button where the cursor is until a later Mouse up step.",
        MacroStepKind.MouseUp => "Releases a button held by an earlier Mouse down step.",
        MacroStepKind.MoveTo => "Moves the cursor to exact screen pixels at up to 9,000 px/s.",
        MacroStepKind.MoveCursorBy => "Moves the on-screen cursor this many pixels from where it is when the step runs. " +
            "Positive X is right, positive Y is down. Fails if that lands off-screen. Use Raw to turn a game camera.",
        MacroStepKind.MoveMouseBy => "Sends mouse movement the way a real mouse does, for turning a game camera. " +
            "The game's sensitivity controls how far the camera turns. Windows mouse speed controls the distance on the desktop. " +
            "Use Pixels for exact screen distances.",
        MacroStepKind.SavePosition => "Remembers where the cursor is now in the chosen slot. A later Move set to Saved returns there.",
        MacroStepKind.MoveToSaved => "Moves the cursor back to the position saved in the chosen slot, like a move to a position. Stops if that spot is no longer on a screen.",
        MacroStepKind.MouseWheel => "120 is one notch. Negative values scroll down or left.",
        MacroStepKind.FreezeMouse => "Blocks your physical mouse movement until an Unfreeze mouse step or the end of the macro. " +
            "The macro's own cursor moves still work; F12 stops playback. Some games read the mouse directly and may still see movement.",
        MacroStepKind.UnfreezeMouse => "Lets your physical mouse move the cursor again.",
        _ => "Pauses before the next step."
    };
    private static string ButtonText(MouseButton? button) => button is { } value ? ButtonName(value) : "Choose button";
    // XButton1/2 are the side buttons; the shortcut picker calls them Mouse Button 4 and 5.
    internal static string ButtonName(MouseButton button) => button switch
    {
        sWinShortcuts.Models.MouseButton.XButton1 => "Button 4",
        sWinShortcuts.Models.MouseButton.XButton2 => "Button 5",
        _ => button.ToString()
    };
    private static string FormatSlot(int slot) => $"#{slot.ToString(CultureInfo.InvariantCulture)}";
    private static string PositionText(MacroStep step) =>
        $"{step.X.ToString(CultureInfo.InvariantCulture)}, {step.Y.ToString(CultureInfo.InvariantCulture)}";
    private string MillisecondsText => $"{DurationMs.ToString(CultureInfo.InvariantCulture)} ms";
    public string Error =>
        HasCoordinates && (!IsNumber(XText) || !IsNumber(YText)) ? "Screen coordinates must be signed whole numbers." :
        HasOffset && (!IsNumber(XText) || !IsNumber(YText)) ? "Offsets must be signed whole numbers." :
        HasDuration && !IsNumber(DurationText) ? "Duration must be a whole number of milliseconds." :
        IsWheel && !IsNumber(WheelDeltaText) ? "Wheel delta must be a signed whole number." :
        MacroValidation.GetStepError(_step) ?? string.Empty;
    public string this[string columnName] => Error;

    public void SetPosition(int x, int y) => Change(_step with { X = x, Y = y }, string.Empty);

    // Presentation only, like SetCollapsedWait: the editor classifies holds from the surrounding rows.
    internal void SetPressHold(bool value)
    {
        if (_isPressHold == value) return;
        _isPressHold = value;
        OnPropertyChanged(nameof(IsPressHold));
        OnPropertyChanged(nameof(TimingText));
        OnPropertyChanged(nameof(DisplayTimingText));
        OnPropertyChanged(nameof(Summary));
    }

    // The open wait-time editor would change this row's time (for a collapsed press, its hold).
    public bool IsWaitTarget => _isWaitTarget;
    internal void SetWaitTarget(bool value)
    {
        if (_isWaitTarget == value) return;
        _isWaitTarget = value;
        OnPropertyChanged(nameof(IsWaitTarget));
    }

    // Presentation only, like SetPressHold: the one-line status trims long messages, so the named row shows it too.
    internal void SetProblem(string problem)
    {
        if (_problem == problem) return;
        _problem = problem;
        OnPropertyChanged(nameof(DisplayError));
        OnPropertyChanged(nameof(IsProblemOnly));
    }

    // Presentation only, like SetCollapsedWait: this row's place in a Down, Wait, Up group, or None.
    public MacroPressGroupPart PressGroupPart { get; private set; }
    internal void SetPressGroupPart(MacroPressGroupPart value)
    {
        if (PressGroupPart == value) return;
        PressGroupPart = value;
        OnPropertyChanged(nameof(PressGroupPart));
    }

    // Presentation only: preserve each source row and its unparsed editor text.
    internal void SetCollapsedWait(MacroStepViewModel? wait)
    {
        var changed = !ReferenceEquals(_collapsedWait, wait);
        var duration = wait?.DurationMs ?? 0;
        var durationChanged = _collapsedDurationMs != duration;
        var error = wait?.Error ?? string.Empty;
        var errorChanged = _collapsedError != error;
        if (!changed && !durationChanged && !errorChanged) return;
        _collapsedWait = wait;
        _collapsedDurationMs = duration;
        _collapsedError = error;
        if (changed)
        {
            OnPropertyChanged(nameof(CollapsedWait));
            OnPropertyChanged(nameof(IsCollapsedPress));
            OnPropertyChanged(nameof(SourceStepCount));
            OnPropertyChanged(nameof(DisplayNumber));
            OnPropertyChanged(nameof(AutomationStepLabel));
            OnPropertyChanged(nameof(DisplayActionLabel));
        }
        if (changed || durationChanged) OnPropertyChanged(nameof(DisplayTimingText));
        if (changed || errorChanged)
        {
            OnPropertyChanged(nameof(DisplayError));
            OnPropertyChanged(nameof(IsProblemOnly));
        }
    }

    private void Change(MacroStep value, [CallerMemberName] string? propertyName = null, bool resetCoordinateText = false)
    {
        if (!_canEdit() || (value == _step && propertyName != string.Empty)) return;
        var previous = _step;
        _step = value;
        resetCoordinateText |= propertyName == string.Empty;
        if (previous.X != X || resetCoordinateText) _xText = X.ToString(CultureInfo.InvariantCulture);
        if (previous.Y != Y || resetCoordinateText) _yText = Y.ToString(CultureInfo.InvariantCulture);
        if (previous.DurationMs != DurationMs) _durationText = DurationMs.ToString(CultureInfo.InvariantCulture);
        if (previous.WheelDelta != WheelDelta) _wheelDeltaText = WheelDelta.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(XText));
        OnPropertyChanged(nameof(YText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(WheelDeltaText));
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(HorizontalWheel))
        {
            OnPropertyChanged(nameof(IsVerticalWheel));
            OnPropertyChanged(nameof(IsHorizontalWheel));
        }
        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(DisplayActionLabel));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(TimingText));
        OnPropertyChanged(nameof(DisplayTimingText));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(DisplayError));
        OnPropertyChanged(nameof(IsProblemOnly));
        if (propertyName == nameof(Kind))
        {
            OnPropertyChanged(nameof(Key));
            OnPropertyChanged(nameof(MouseButton));
            OnPropertyChanged(nameof(WheelDelta));
            OnPropertyChanged(nameof(HasKey));
            OnPropertyChanged(nameof(HasMouseButton));
            OnPropertyChanged(nameof(HasCoordinates));
            OnPropertyChanged(nameof(HasOffset));
            OnPropertyChanged(nameof(HasSlot));
            OnPropertyChanged(nameof(Slot));
            OnPropertyChanged(nameof(IsMove));
            OnPropertyChanged(nameof(ActionChoice));
            OnPropertyChanged(nameof(MoveIsPosition));
            OnPropertyChanged(nameof(MoveIsOffset));
            OnPropertyChanged(nameof(MoveIsSaved));
            OnPropertyChanged(nameof(MoveAsPixels));
            OnPropertyChanged(nameof(MoveAsRaw));
            OnPropertyChanged(nameof(HasHeldButtons));
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
        OnPropertyChanged(nameof(DisplayError));
        OnPropertyChanged(nameof(IsProblemOnly));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(TimingText));
        OnPropertyChanged(nameof(DisplayTimingText));
        OnPropertyChanged(nameof(Summary));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

// A row's place in a Down, Wait, Up press group. A closed group shows as its head row; an open one shows all three.
public enum MacroPressGroupPart
{
    None,
    ClosedHead,
    OpenHead,
    OpenMiddle,
    OpenLast
}
