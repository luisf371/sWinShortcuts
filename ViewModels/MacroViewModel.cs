using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Utilities;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.ViewModels;

public sealed class MacroViewModel : ViewModelBase, IDisposable, IDataErrorInfo
{
    // New Wait steps start as a visible pause; 0 would add a row that does nothing.
    internal const int DEFAULT_WAIT_MS = 100;
    private MacroDefinition _definition;
    private MacroDefinition _lastRepresentable;
    private readonly Func<bool> _canEdit;
    private ReadOnlyObservableCollection<MacroStepViewModel> _steps;
    private IReadOnlyList<MacroStepViewModel> _visibleSteps = [];
    private MacroStepViewModel? _selectedStep;
    private CancellationTokenSource? _coordinatePick;
    // The view supplies the cursor badge and the ruler; without a view, a pick waits and reads the cursor directly.
    private Func<string, CancellationToken, Task<(int X, int Y)?>> _countdown = WaitThenReadCursorAsync;
    private Action<string> _showCaptured = static _ => { };
    private Func<bool, CancellationToken, Task<(int X, int Y)?>>? _measure;
    private bool _disposed;
    private string _validationMessage = string.Empty;
    private string _coordinatePickStatus = string.Empty;
    private int? _problemStepNumber;
    private bool _hasFormatError;
    private bool _batchChangingSteps;
    // Open press groups, keyed by their Down row, so a group that breaks and re-forms finds its state again.
    private readonly HashSet<MacroStepViewModel> _openPresses = [];
    private int _openPressCount;
    private int _closedPressCount;
    private bool _refreshingVisibleSteps;
    private bool _isWaitEditorOpen;
    private string _waitDurationText = string.Empty;
    private string? _waitEditResult;
    private MacroWaitScope _waitScope = MacroWaitScope.PressHolds;
    private int _holdWaitCount;
    private int _compactHoldCount;
    private bool _waitTargetsShown;

    public MacroViewModel(MacroDefinition definition, Func<bool> canEdit)
    {
        _definition = definition;
        _lastRepresentable = definition;
        _canEdit = canEdit;
        _steps = new(new(definition.Steps.Select(CreateStep)));
        RenumberSteps();
        // New steps and takes append by default; selecting a row inserts after it instead.
        _selectedStep = Steps.LastOrDefault();
        InsertStepCommand = new RelayCommand(InsertStep, () => CanAddStepOf(MacroStepKind.KeyPress));
        AddStepCommand = new RelayCommand<MacroStepKind>(AddStep, CanAddStepOf);
        DuplicateStepCommand = new RelayCommand(DuplicateStep, () => CanEdit && SelectedStep is not null && Steps.Count + SelectedStep.SourceStepCount <= MacroValidation.MaxSteps);
        DeleteStepCommand = new RelayCommand(DeleteStep, () => CanEdit && SelectedStep is not null);
        MoveStepUpCommand = new RelayCommand(() => MoveStep(-1), () => CanEdit && SelectedIndex > 0);
        MoveStepDownCommand = new RelayCommand(() => MoveStep(1), () => CanEdit && SelectedIndex >= 0 && RecordingInsertionIndex < Steps.Count);
        PickCoordinatesCommand = new AsyncRelayCommand(PickCoordinatesAsync, () => CanEdit && SelectedStep?.HasCoordinates == true && _coordinatePick is null);
        MeasureOffsetCommand = new AsyncRelayCommand(MeasureOffsetAsync,
            () => CanEdit && SelectedStep?.HasOffset == true && _coordinatePick is null && _measure is not null);
        // A running AsyncRelayCommand reports CanExecute false, so Cancel needs its own command.
        CancelCoordinatePickCommand = new RelayCommand(CancelCoordinatePick, () => _coordinatePick is not null);
        ShowProblemStepCommand = new RelayCommand(ShowProblemStep, () => CanEdit && ProblemStepNumber <= Steps.Count);
        ExpandSelectedStepCommand = new RelayCommand(() => ChangeOpenPresses(open => open.Add(SelectedStep!)),
            () => CanEdit && SelectedStep?.IsCollapsedPress == true);
        CollapseSelectedStepCommand = new RelayCommand(() => ChangeOpenPresses(open => open.Remove(OpenPressHead(SelectedStep)!)),
            () => CanEdit && OpenPressHead(SelectedStep) is not null);
        TogglePressGroupCommand = new RelayCommand<MacroStepViewModel>(
            row => ChangeOpenPresses(open => { if (!open.Add(row!)) open.Remove(row!); }),
            row => CanEdit && row?.PressGroupPart is MacroPressGroupPart.ClosedHead or MacroPressGroupPart.OpenHead);
        ExpandAllCommand = new RelayCommand(
            () => ChangeOpenPresses(open => open.UnionWith(Steps.Where(row => row.PressGroupPart == MacroPressGroupPart.ClosedHead))),
            () => CanEdit && _closedPressCount > 0);
        // Forgets every open state, including groups that are broken right now.
        CollapseAllCommand = new RelayCommand(() => ChangeOpenPresses(open => open.Clear()), () => CanEdit && _openPressCount > 0);
        ShowWaitEditorCommand = new RelayCommand(ShowWaitEditor, () => CanEdit && WaitTargetCount(MacroWaitScope.AllWaits) > 0);
        CloseWaitEditorCommand = new RelayCommand(CloseWaitEditor, () => IsWaitEditorOpen);
        ApplyWaitTimesCommand = new RelayCommand(ApplyWaitTimes, CanApplyWaitTimes);
        RefreshValidation(null);
    }

    public event EventHandler? Changed;
    public Guid Id => _definition.Id;
    public bool CanEdit => !_disposed && _canEdit();
    public string Label { get => _definition.Label; set => Change(_definition with { Label = value ?? string.Empty }, nameof(Label)); }
    public bool IsEnabled { get => _definition.IsEnabled; set => Change(_definition with { IsEnabled = value }, nameof(IsEnabled)); }
    public bool ToggleMode { get => _definition.ToggleMode; set => Change(_definition with { ToggleMode = value }, nameof(ToggleMode)); }
    public bool StartOnPress { get => _definition.StartOnPress; set => Change(_definition with { StartOnPress = value }, nameof(StartOnPress)); }
    public bool CancelOnMouseMovement { get => _definition.CancelOnMouseMovement; set => Change(_definition with { CancelOnMouseMovement = value }, nameof(CancelOnMouseMovement)); }
    // Left out of PlaybackSummary: with every other option on, the label would no longer fit the row at 650 px.
    public bool ShowPlayingSteps { get => _definition.ShowPlayingSteps; set => Change(_definition with { ShowPlayingSteps = value }, nameof(ShowPlayingSteps)); }
    // Playback menu: two radio pairs over ToggleMode and StartOnPress. A menu click on the checked item writes false,
    // which is ignored and re-announced so the item stays checked.
    public bool PlaysOnce { get => !ToggleMode; set { if (value) ToggleMode = false; OnPropertyChanged(); } }
    public bool PlaysInLoop { get => ToggleMode; set { if (value) ToggleMode = true; OnPropertyChanged(); } }
    public bool StartsWhenReleased { get => !StartOnPress; set { if (value) StartOnPress = false; OnPropertyChanged(); } }
    public bool StartsWhenPressed { get => StartOnPress; set { if (value) StartOnPress = true; OnPropertyChanged(); } }
    // The Playback button's label: what is set, without opening the menu. Defaults (start on release, mouse movement
    // ignored) are left out so every combination fits the row.
    public string PlaybackSummary => (ToggleMode ? "Loop" : "Once") + (StartOnPress ? " · on press" : string.Empty) +
        (CancelOnMouseMovement ? " · stops on move" : string.Empty);
    // The capture box's value: modifiers with their sides plus one key or mouse button, applied as one edit.
    // Clearing the trigger also clears the modifiers, so a None shortcut never keeps stale prefixes.
    public KeyChord Shortcut
    {
        get => _definition.ShortcutTrigger.Kind == InputTriggerKind.None
            ? KeyChord.None
            : new KeyChord(_definition.ShortcutModifiers, _definition.ShortcutTrigger, _definition.ShortcutRightModifiers);
        set
        {
            var trigger = value.Input;
            if (trigger.Kind is not (InputTriggerKind.None or InputTriggerKind.KeyboardKey or InputTriggerKind.MouseButton) ||
                (trigger.Kind == InputTriggerKind.MouseButton && !Enum.IsDefined(trigger.MouseButton))) return;
            Change(_definition with
            {
                ShortcutKey = trigger.Kind == InputTriggerKind.KeyboardKey ? trigger.Key : Key.None,
                ShortcutMouseButton = trigger.Kind == InputTriggerKind.MouseButton ? trigger.MouseButton : null,
                ShortcutModifiers = trigger.Kind == InputTriggerKind.None ? ModifierKeys.None : value.Modifiers,
                ShortcutRightModifiers = trigger.Kind == InputTriggerKind.None ? ModifierKeys.None : value.RightModifiers
            }, nameof(Shortcut));
        }
    }

    public string ShortcutText => Shortcut.ToDisplayText("No shortcut");

    // Field rules for the capture boxes (bound with x:Static).
    public static Func<KeyChord, string?> ShortcutValidator { get; } = KeyBindingRules.MacroShortcut;
    public static Func<KeyChord, string?> KeyValidator { get; } = KeyBindingRules.MacroKey;
    public ReadOnlyObservableCollection<MacroStepViewModel> Steps => _steps;
    public IReadOnlyList<MacroStepViewModel> VisibleSteps => _visibleSteps;
    public int WaitStepCount => Steps.Count(step => step.Kind == MacroStepKind.Wait);
    // How long each key or button is held: the Wait inside a press plus every Key press and Mouse click hold.
    public int PressHoldCount => _holdWaitCount + _compactHoldCount;
    // Wait steps that pause between actions instead of holding a press.
    public int BetweenWaitCount => WaitStepCount - _holdWaitCount;
    // Segment counts and availability for the wait-time bar.
    public int AllWaitCount => PressHoldCount + BetweenWaitCount;
    public bool HasPressHolds => PressHoldCount > 0;
    public bool HasBetweenWaits => BetweenWaitCount > 0;
    public string StepCountText => $"{Steps.Count} {(Steps.Count == 1 ? "step" : "steps")}";
    public bool IsWaitEditorOpen => _isWaitEditorOpen;
    public MacroWaitScope WaitScope
    {
        get => _waitScope;
        set
        {
            if (!CanEdit || !IsWaitEditorOpen || !Enum.IsDefined(value) || !SetProperty(ref _waitScope, value)) return;
            _waitEditResult = null;
            NotifyWaitScope();
            NotifyWaitEditor();
            RefreshWaitTargets();
        }
    }
    // Radio-button projections of WaitScope; clearing one is ignored because choosing another sets the scope.
    public bool IsPressHoldScope { get => WaitScope == MacroWaitScope.PressHolds; set { if (value) WaitScope = MacroWaitScope.PressHolds; } }
    public bool IsBetweenStepsScope { get => WaitScope == MacroWaitScope.BetweenSteps; set { if (value) WaitScope = MacroWaitScope.BetweenSteps; } }
    public bool IsAllWaitsScope { get => WaitScope == MacroWaitScope.AllWaits; set { if (value) WaitScope = MacroWaitScope.AllWaits; } }
    public string WaitDurationText
    {
        get => _waitDurationText;
        set
        {
            if (!CanEdit || !IsWaitEditorOpen || !SetProperty(ref _waitDurationText, value ?? string.Empty)) return;
            _waitEditResult = null;
            NotifyWaitEditor();
        }
    }
    public bool HasWaitEditError => WaitDurationText.Length > 0 && !TryGetWaitDuration(out _);
    public bool WaitEditApplied => _waitEditResult is not null;
    public string WaitEditMessage
    {
        get
        {
            // One short line: the segments carry the counts and the list highlights the rows.
            if (WaitTargetCount(WaitScope) == 0) return "None in this macro";
            // Name the problem in a few words: anything but digits, or more than the step limit allows.
            if (HasWaitEditError) return WaitDurationText.All(char.IsAsciiDigit)
                ? $"Max {MacroValidation.MaxDurationMs.ToString(CultureInfo.InvariantCulture)} ms" : "Digits only";
            return _waitEditResult ?? string.Empty;
        }
    }
    public MacroStepViewModel? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (_refreshingVisibleSteps || !CanEdit || (value is not null && !Steps.Contains(value))) return;
            value = GetVisibleStep(value);
            if (SetProperty(ref _selectedStep, value))
            {
                CancelCoordinatePick();
                OnPropertyChanged(nameof(InsertionHint));
                OnPropertyChanged(nameof(SelectedPressKey));
                OnPropertyChanged(nameof(SelectedPressMouseButton));
                RefreshCommands();
            }
        }
    }
    public int SelectedIndex => SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
    public int RecordingInsertionIndex => SelectedIndex < 0 ? Steps.Count : SelectedIndex + SelectedStep!.SourceStepCount;
    public bool CanAddStep => CanEdit && Steps.Count < MacroValidation.MaxSteps;
    public Key SelectedPressKey
    {
        get => SelectedStep is { IsCollapsedPress: true, HasKey: true } step ? step.Key : Key.None;
        set
        {
            if (MacroValidation.IsSupportedKey(value)) ChangeSelectedPressTarget(value, null);
        }
    }
    public MouseButton? SelectedPressMouseButton
    {
        get => SelectedStep is { IsCollapsedPress: true, HasMouseButton: true } step ? step.MouseButton : null;
        set
        {
            if (value is MouseButton button && Enum.IsDefined(button)) ChangeSelectedPressTarget(Key.None, button);
        }
    }

    // Where Add step and Record place new rows; mirrors RecordingInsertionIndex for the editor copy.
    public string InsertionHint
    {
        get
        {
            var index = RecordingInsertionIndex;
            return index == Steps.Count ? "at the end" : $"after step {index}";
        }
    }
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }
    public bool IsPlayable => string.IsNullOrEmpty(ValidationMessage);

    // True while a field is unrepresentable: the draft is "Not saved" rather than merely unplayable.
    public bool HasFormatError { get => _hasFormatError; private set => SetProperty(ref _hasFormatError, value); }

    // The step named by the current validation message, so the editor can jump straight to it.
    public int? ProblemStepNumber
    {
        get => _problemStepNumber;
        private set
        {
            if (!SetProperty(ref _problemStepNumber, value)) return;
            OnPropertyChanged(nameof(HasProblemStep));
            ShowProblemStepCommand.NotifyCanExecuteChanged();
        }
    }
    public bool HasProblemStep => ProblemStepNumber is not null;
    public string Error => GetEditorFormatError(ToDefinition()) ?? string.Empty;
    public string this[string columnName] => columnName switch
    {
        nameof(Label) => Error,
        nameof(WaitDurationText) when HasWaitEditError => WaitEditMessage,
        _ => string.Empty
    };
    public string CoordinatePickStatus { get => _coordinatePickStatus; private set => SetProperty(ref _coordinatePickStatus, value); }
    public IRelayCommand InsertStepCommand { get; }
    public IRelayCommand<MacroStepKind> AddStepCommand { get; }
    public IRelayCommand DuplicateStepCommand { get; }
    public IRelayCommand DeleteStepCommand { get; }
    public IRelayCommand MoveStepUpCommand { get; }
    public IRelayCommand MoveStepDownCommand { get; }
    public IAsyncRelayCommand PickCoordinatesCommand { get; }
    public IAsyncRelayCommand MeasureOffsetCommand { get; }
    public IRelayCommand CancelCoordinatePickCommand { get; }
    public bool IsCapturingCoordinates => _coordinatePick is not null;
    public IRelayCommand ShowProblemStepCommand { get; }
    public IRelayCommand ExpandSelectedStepCommand { get; }
    public IRelayCommand CollapseSelectedStepCommand { get; }
    public IRelayCommand<MacroStepViewModel> TogglePressGroupCommand { get; }
    public IRelayCommand ExpandAllCommand { get; }
    public IRelayCommand CollapseAllCommand { get; }
    public IRelayCommand ShowWaitEditorCommand { get; }
    public IRelayCommand CloseWaitEditorCommand { get; }
    public IRelayCommand ApplyWaitTimesCommand { get; }
    public static IReadOnlyList<MouseButton> MouseButtons { get; } = Enum.GetValues<MouseButton>();
    public static IReadOnlyList<KeyValuePair<MouseButton, string>> MouseButtonOptions { get; } =
        MouseButtons.Select(button => new KeyValuePair<MouseButton, string>(button, MacroStepViewModel.ButtonName(button))).ToArray();
    public static IReadOnlyList<KeyValuePair<MacroStepKind, string>> ActionOptions { get; } =
    [
        // Single Down and Up steps live here; Add step inserts them as Key press and Mouse click groups.
        new(MacroStepKind.KeyDown, "Key down"), new(MacroStepKind.KeyUp, "Key up"),
        new(MacroStepKind.MouseDown, "Mouse down"), new(MacroStepKind.MouseUp, "Mouse up"),
        new(MacroStepKind.MoveTo, "Move"), new(MacroStepKind.SavePosition, "Save position"),
        new(MacroStepKind.MouseWheel, "Mouse wheel"),
        new(MacroStepKind.FreezeMouse, "Freeze mouse"), new(MacroStepKind.UnfreezeMouse, "Unfreeze mouse"),
        new(MacroStepKind.Wait, "Wait")
    ];

    public MacroDefinition ToDefinition() => _definition with { Steps = Steps.Select(step => step.ToModel()).ToArray() };

    internal MacroDefinition GetRepresentableDefinition()
    {
        var current = ToDefinition();
        if (GetEditorFormatError(current) is null) _lastRepresentable = current with { Label = current.Label.Trim() };
        else _lastRepresentable = _lastRepresentable with { IsEnabled = false };
        return _lastRepresentable;
    }

    internal void RefreshValidation(string? conflict)
    {
        var current = ToDefinition();
        var formatError = GetEditorFormatError(current);
        HasFormatError = formatError is not null;
        ValidationMessage = formatError is not null
            ? $"Not saved: {formatError} Playback is disabled until corrected."
            : conflict ?? MacroValidation.GetPlaybackError(current) ?? string.Empty;
        ProblemStepNumber = FindStepNumber(ValidationMessage);
        for (var i = 0; i < Steps.Count; i++) Steps[i].SetProblem(i + 1 == ProblemStepNumber ? ValidationMessage : string.Empty);
        OnPropertyChanged(nameof(IsPlayable));
        OnPropertyChanged(nameof(Error));
        RefreshVisibleSteps();
    }

    private string? GetEditorFormatError(MacroDefinition current)
    {
        var error = MacroValidation.GetFormatError(current);
        if (error is not null) return error;
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Error is { Length: > 0 } rowError) return $"Step {i + 1}: {rowError}";
        }
        return null;
    }

    // Validation messages name rows as "Step N: ..."; anything else has no row to show.
    private static int? FindStepNumber(string message)
    {
        const string marker = "Step ";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        var rest = message.AsSpan(start + marker.Length);
        var end = rest.IndexOf(':');
        return end > 0 && int.TryParse(rest[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;
    }

    private void Change(MacroDefinition value, string propertyName)
    {
        if (!CanEdit || value == _definition) return;
        _definition = value;
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(Shortcut)) OnPropertyChanged(nameof(ShortcutText));
        if (propertyName is nameof(ToggleMode) or nameof(StartOnPress) or nameof(CancelOnMouseMovement))
        {
            OnPropertyChanged(nameof(PlaysOnce));
            OnPropertyChanged(nameof(PlaysInLoop));
            OnPropertyChanged(nameof(StartsWhenReleased));
            OnPropertyChanged(nameof(StartsWhenPressed));
            OnPropertyChanged(nameof(PlaybackSummary));
        }
        PublishChange();
    }

    private MacroStepViewModel CreateStep(MacroStep step)
    {
        var row = new MacroStepViewModel(step, () => CanEdit, row => FindPositionBefore(row.Number - 1),
            row => FindSavedSlotBefore(row.Number - 1));
        row.Changed += OnStepChanged;
        return row;
    }
    private void OnStepChanged(object? sender, EventArgs e)
    {
        if (!_batchChangingSteps) PublishChange();
    }
    private void PublishChange()
    {
        _waitEditResult = null;
        OnPropertyChanged(nameof(WaitStepCount));
        RefreshValidation(null);
        RefreshCommands();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void InsertStep() => AddStep(MacroStepKind.KeyPress);

    private bool TryGetWaitDuration(out int duration) =>
        int.TryParse(WaitDurationText, NumberStyles.Integer, CultureInfo.InvariantCulture, out duration) &&
        duration is >= 0 and <= MacroValidation.MaxDurationMs;

    private bool CanApplyWaitTimes() => CanEdit && IsWaitEditorOpen && WaitTargetCount(WaitScope) > 0 && TryGetWaitDuration(out _);

    private int WaitTargetCount(MacroWaitScope scope) => scope switch
    {
        MacroWaitScope.PressHolds => PressHoldCount,
        MacroWaitScope.BetweenSteps => BetweenWaitCount,
        _ => PressHoldCount + BetweenWaitCount
    };

    // Press holds are the Wait inside a press plus Key press and Mouse click holds; every other Wait pauses between steps.
    private static bool IsWaitTarget(MacroStepViewModel step, MacroWaitScope scope) =>
        (step.Kind == MacroStepKind.Wait || step.HasHold) &&
        (scope == MacroWaitScope.AllWaits || (step.IsPressHold || step.HasHold) == (scope == MacroWaitScope.PressHolds));

    // Presentation only: while the editor is open, the rows its scope would change are highlighted.
    private void RefreshWaitTargets()
    {
        if (!IsWaitEditorOpen && !_waitTargetsShown) return;
        _waitTargetsShown = IsWaitEditorOpen;
        foreach (var step in Steps)
        {
            step.SetWaitTarget(IsWaitEditorOpen &&
                (IsWaitTarget(step, WaitScope) || (step.CollapsedWait is { } wait && IsWaitTarget(wait, WaitScope))));
        }
    }

    private void ShowWaitEditor()
    {
        if (!CanEdit || WaitTargetCount(MacroWaitScope.AllWaits) == 0 || IsWaitEditorOpen) return;
        // Open on press holds when there are any, suggesting a short uniform hold that still registers in games
        // without touching the pauses that give the macro its rhythm.
        _waitScope = PressHoldCount > 0 ? MacroWaitScope.PressHolds : MacroWaitScope.BetweenSteps;
        _waitDurationText = _waitScope == MacroWaitScope.PressHolds
            ? MacroValidation.DefaultPressHoldMs.ToString(CultureInfo.InvariantCulture) : string.Empty;
        SetProperty(ref _isWaitEditorOpen, true, nameof(IsWaitEditorOpen));
        NotifyWaitScope();
        OnPropertyChanged(nameof(WaitDurationText));
        NotifyWaitEditor();
        RefreshWaitTargets();
    }

    public void CloseWaitEditor()
    {
        if (!IsWaitEditorOpen) return;
        SetProperty(ref _isWaitEditorOpen, false, nameof(IsWaitEditorOpen));
        SetProperty(ref _waitDurationText, string.Empty, nameof(WaitDurationText));
        _waitEditResult = null;
        NotifyWaitEditor();
        RefreshWaitTargets();
    }

    private void ApplyWaitTimes()
    {
        if (!CanApplyWaitTimes() || !TryGetWaitDuration(out var duration)) return;
        SetWaitTimes(WaitScope, duration);
        _waitEditResult = $"Set to {duration.ToString(CultureInfo.InvariantCulture)} ms";
        NotifyWaitEditor();
    }

    private void NotifyWaitScope()
    {
        OnPropertyChanged(nameof(WaitScope));
        OnPropertyChanged(nameof(IsPressHoldScope));
        OnPropertyChanged(nameof(IsBetweenStepsScope));
        OnPropertyChanged(nameof(IsAllWaitsScope));
    }

    private void NotifyWaitEditor()
    {
        OnPropertyChanged(nameof(PressHoldCount));
        OnPropertyChanged(nameof(BetweenWaitCount));
        OnPropertyChanged(nameof(AllWaitCount));
        OnPropertyChanged(nameof(HasPressHolds));
        OnPropertyChanged(nameof(HasBetweenWaits));
        OnPropertyChanged(nameof(WaitEditMessage));
        OnPropertyChanged(nameof(HasWaitEditError));
        OnPropertyChanged(nameof(WaitEditApplied));
        ShowWaitEditorCommand.NotifyCanExecuteChanged();
        CloseWaitEditorCommand.NotifyCanExecuteChanged();
        ApplyWaitTimesCommand.NotifyCanExecuteChanged();
    }

    private void ChangeSelectedPressTarget(Key key, MouseButton? button)
    {
        var down = SelectedStep;
        if (_refreshingVisibleSteps || _batchChangingSteps || !CanEdit || down?.IsCollapsedPress != true ||
            (button.HasValue ? !down.HasMouseButton : !down.HasKey)) return;
        var up = Steps[SelectedIndex + 2];
        if (button.HasValue ? down.MouseButton == button && up.MouseButton == button : down.Key == key && up.Key == key) return;
        _batchChangingSteps = true;
        try
        {
            if (button.HasValue)
            {
                down.MouseButton = button;
                up.MouseButton = button;
            }
            else
            {
                down.Key = key;
                up.Key = key;
            }
        }
        finally
        {
            _batchChangingSteps = false;
            PublishChange();
        }
    }

    // One duration for every timing row in the scope, published once; other rows keep their raw editor text.
    public void SetWaitTimes(MacroWaitScope scope, int durationMs)
    {
        if (!CanEdit || !Enum.IsDefined(scope) || durationMs is < 0 or > MacroValidation.MaxDurationMs) return;
        var text = durationMs.ToString(CultureInfo.InvariantCulture);
        var changed = false;
        _batchChangingSteps = true;
        try
        {
            foreach (var step in Steps)
            {
                if (!IsWaitTarget(step, scope) || step.DurationText == text) continue;
                changed = true;
                step.DurationText = text;
            }
        }
        finally
        {
            _batchChangingSteps = false;
            if (changed) PublishChange();
        }
    }
    // Key press and Mouse click insert a Down, Wait, Up group; every other kind is one row.
    private bool CanAddStepOf(MacroStepKind kind) => CanEdit && Enum.IsDefined(kind) &&
        Steps.Count + (kind is MacroStepKind.KeyPress or MacroStepKind.MouseClick ? 3 : 1) <= MacroValidation.MaxSteps;

    private void AddStep(MacroStepKind kind)
    {
        if (!CanAddStepOf(kind)) return;
        var index = RecordingInsertionIndex;
        var step = new MacroStep { Kind = kind, Key = Key.A, MouseButton = MouseButton.Left, WheelDelta = 120 };
        var hold = step with { Kind = MacroStepKind.Wait, DurationMs = MacroValidation.DefaultPressHoldMs };
        if (kind == MacroStepKind.KeyPress)
        {
            InsertRecording(index, [step with { Kind = MacroStepKind.KeyDown }, hold, step with { Kind = MacroStepKind.KeyUp }]);
            return;
        }
        if (kind == MacroStepKind.MouseClick)
        {
            InsertRecording(index, [step with { Kind = MacroStepKind.MouseDown }, hold, step with { Kind = MacroStepKind.MouseUp }]);
            return;
        }
        if (kind == MacroStepKind.Wait) step = step with { DurationMs = DEFAULT_WAIT_MS };
        else if (kind == MacroStepKind.MoveTo && FindPositionBefore(index) is { } position)
            step = step with { X = position.X, Y = position.Y };
        else if (kind == MacroStepKind.SavePosition)
        {
            var usedSlots = Steps.Take(index).Where(row => row.Kind == MacroStepKind.SavePosition).Select(row => row.Slot).ToHashSet();
            step = step with { Slot = Enumerable.Range(1, MacroValidation.MaxPositionSlots).FirstOrDefault(slot => !usedSlots.Contains(slot), 1) };
        }
        else if (kind == MacroStepKind.MoveToSaved) step = step with { Slot = FindSavedSlotBefore(index) };
        InsertRecording(index, [step]);
    }

    // Find the last screen position above the given zero-based step index.
    private (int X, int Y)? FindPositionBefore(int index)
    {
        for (var i = Math.Min(index, Steps.Count) - 1; i >= 0; i--)
        {
            if (Steps[i].HasCoordinates) return (Steps[i].X, Steps[i].Y);
        }
        return null;
    }
    private int FindSavedSlotBefore(int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (Steps[i].Kind == MacroStepKind.SavePosition) return Steps[i].Slot;
        }
        return 1;
    }

    private void DuplicateStep()
    {
        if (DuplicateStepCommand.CanExecute(null))
            InsertRecording(RecordingInsertionIndex, Steps.Skip(SelectedIndex).Take(SelectedStep!.SourceStepCount).Select(step => step.ToModel()).ToArray());
    }
    private void DeleteStep()
    {
        if (!DeleteStepCommand.CanExecute(null)) return;
        var index = SelectedIndex;
        var rows = Steps.ToList();
        rows.RemoveRange(index, SelectedStep!.SourceStepCount);
        ReplaceSteps(rows, Math.Min(index, rows.Count - 1));
    }
    private void MoveStep(int direction)
    {
        var index = SelectedIndex;
        if (!CanEdit || index < 0) return;
        var count = SelectedStep!.SourceStepCount;
        if (direction < 0 ? index == 0 : index + count >= Steps.Count) return;
        var destination = direction < 0
            ? GetVisibleStep(Steps[index - 1])!.Number - 1
            : index + Steps[index + count].SourceStepCount;
        var rows = Steps.ToList();
        var moved = rows.GetRange(index, count);
        rows.RemoveRange(index, count);
        rows.InsertRange(destination, moved);
        ReplaceSteps(rows, destination);
    }
    private void ShowProblemStep()
    {
        if (!CanEdit || ProblemStepNumber is not int number || number > Steps.Count) return;
        SelectedStep = Steps[number - 1];
    }

    public void InsertRecording(int index, IReadOnlyList<MacroStep> rows)
    {
        if (_disposed) return;
        if (index < 0 || index > Steps.Count || rows.Count > MacroValidation.MaxSteps - Steps.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "The recording does not fit in this macro.");
        if (rows.Count == 0) return;
        var result = Steps.ToList();
        result.InsertRange(index, rows.Select(CreateStep));
        // Select the last inserted row so the next Add step or Record continues after this insertion.
        ReplaceSteps(result, index + rows.Count - 1);
    }

    // Removes every step while the name, shortcut, and options stay; the editor confirms first.
    internal void ClearSteps()
    {
        if (!CanEdit || Steps.Count == 0) return;
        ReplaceSteps([], -1);
    }

    private void ReplaceSteps(IReadOnlyList<MacroStepViewModel> rows, int selectedIndex)
    {
        CancelCoordinatePick();
        foreach (var step in Steps) step.Changed -= OnStepChanged;
        foreach (var step in rows)
        {
            step.Changed -= OnStepChanged;
            step.Changed += OnStepChanged;
        }
        _steps = new(new(rows));
        // Removed rows leave the open set; rows still present keep their state even when not a group head now.
        _openPresses.IntersectWith(rows);
        RenumberSteps();
        _selectedStep = selectedIndex >= 0 ? Steps[selectedIndex] : null;
        OnPropertyChanged(nameof(Steps));
        PublishChange();
    }
    private void RenumberSteps()
    {
        for (var i = 0; i < Steps.Count; i++) Steps[i].Number = i + 1;
    }

    private MacroStepViewModel? GetVisibleStep(MacroStepViewModel? step)
    {
        if (step is null) return null;
        var index = Steps.IndexOf(step);
        for (var offset = 1; offset <= 2 && index >= offset; offset++)
        {
            if (Steps[index - offset].SourceStepCount > offset) return Steps[index - offset];
        }
        return step;
    }

    private bool IsPressGroup(int index, HashSet<ushort> heldKeys, HashSet<MouseButton> heldButtons)
    {
        if (index + 2 >= Steps.Count ||
            (ProblemStepNumber is int problem && (problem == index + 1 || problem == index + 3))) return false;
        var down = Steps[index];
        var wait = Steps[index + 1];
        var up = Steps[index + 2];
        return wait.Kind == MacroStepKind.Wait && down.Error.Length == 0 && up.Error.Length == 0 &&
            ((down.Kind == MacroStepKind.KeyDown && up.Kind == MacroStepKind.KeyUp && down.Key == up.Key &&
              !heldKeys.Contains(KeyInteropUtilities.ToVirtualKey(down.Key))) ||
             (down.Kind == MacroStepKind.MouseDown && up.Kind == MacroStepKind.MouseUp && down.MouseButton is MouseButton button &&
              up.MouseButton == button && !heldButtons.Contains(button)));
    }

    // A press hold is the one Wait between a fresh Down and its matching Up; a recorded click may move the cursor
    // before releasing. Waits inside repeats, longer holds, or around other input pause between steps.
    private void RefreshPressHolds()
    {
        var holds = FindPressHolds(Steps.Select(step => step.ToModel()).ToArray());
        var holdWaits = 0;
        var compactHolds = 0;
        for (var i = 0; i < Steps.Count; i++)
        {
            Steps[i].SetPressHold(holds[i]);
            if (holds[i]) holdWaits++;
            else if (Steps[i].HasHold) compactHolds++;
        }
        _holdWaitCount = holdWaits;
        _compactHoldCount = compactHolds;
    }

    // Also names the held key or button in the cursor badge (MacroStepOverlayService).
    internal static bool[] FindPressHolds(MacroStep[] steps)
    {
        var holds = new bool[steps.Length];
        HashSet<ushort> heldKeys = [];
        HashSet<MouseButton> heldButtons = [];
        for (var i = 0; i < steps.Length; i++)
        {
            var row = steps[i];
            switch (row.Kind)
            {
                case MacroStepKind.KeyDown when MacroValidation.IsSupportedKey(row.Key):
                    if (heldKeys.Add(KeyInteropUtilities.ToVirtualKey(row.Key))) MarkPressHold(steps, holds, i);
                    break;
                case MacroStepKind.KeyUp when MacroValidation.IsSupportedKey(row.Key):
                    heldKeys.Remove(KeyInteropUtilities.ToVirtualKey(row.Key));
                    break;
                case MacroStepKind.MouseDown when row.MouseButton is MouseButton downButton:
                    if (heldButtons.Add(downButton)) MarkPressHold(steps, holds, i);
                    break;
                case MacroStepKind.MouseUp when row.MouseButton is MouseButton upButton:
                    heldButtons.Remove(upButton);
                    break;
            }
        }
        return holds;
    }

    private static void MarkPressHold(MacroStep[] steps, bool[] holds, int downIndex)
    {
        var down = steps[downIndex];
        if (downIndex + 2 >= steps.Length || steps[downIndex + 1].Kind != MacroStepKind.Wait) return;
        var upIndex = downIndex + 2;
        if (down.Kind == MacroStepKind.MouseDown)
        {
            while (upIndex < steps.Length && steps[upIndex].Kind is MacroStepKind.MoveTo or MacroStepKind.MoveCursorBy or
                MacroStepKind.MoveMouseBy or MacroStepKind.MoveToSaved or MacroStepKind.SavePosition) upIndex++;
        }
        if (upIndex >= steps.Length) return;
        var up = steps[upIndex];
        if (down.Kind == MacroStepKind.KeyDown
            ? up.Kind == MacroStepKind.KeyUp && up.Key == down.Key
            : up.Kind == MacroStepKind.MouseUp && up.MouseButton == down.MouseButton)
        {
            holds[downIndex + 1] = true;
        }
    }

    private void RefreshVisibleSteps()
    {
        _refreshingVisibleSteps = true;
        try
        {
            RefreshPressHolds();
            var visible = new List<MacroStepViewModel>(Steps.Count);
            var parts = new MacroPressGroupPart[Steps.Count];
            HashSet<ushort> heldKeys = [];
            HashSet<MouseButton> heldButtons = [];
            _openPressCount = 0;
            _closedPressCount = 0;
            for (var i = 0; i < Steps.Count; i++)
            {
                var row = Steps[i];
                MacroStepViewModel? wait = null;
                if (IsPressGroup(i, heldKeys, heldButtons))
                {
                    if (_openPresses.Contains(row))
                    {
                        // An open group's rows are visited one by one below, so held-key tracking matches a closed one.
                        parts[i] = MacroPressGroupPart.OpenHead;
                        parts[i + 1] = MacroPressGroupPart.OpenMiddle;
                        parts[i + 2] = MacroPressGroupPart.OpenLast;
                        _openPressCount++;
                    }
                    else
                    {
                        parts[i] = MacroPressGroupPart.ClosedHead;
                        wait = Steps[i + 1];
                        _closedPressCount++;
                    }
                }
                row.SetCollapsedWait(wait);
                visible.Add(row);
                if (wait is null)
                {
                    // Track surrounding holds even when invalid Wait text masks the playback error.
                    switch (row.Kind)
                    {
                        case MacroStepKind.KeyDown when MacroValidation.IsSupportedKey(row.Key): heldKeys.Add(KeyInteropUtilities.ToVirtualKey(row.Key)); break;
                        case MacroStepKind.KeyUp when MacroValidation.IsSupportedKey(row.Key): heldKeys.Remove(KeyInteropUtilities.ToVirtualKey(row.Key)); break;
                        case MacroStepKind.MouseDown when row.MouseButton is MouseButton downButton: heldButtons.Add(downButton); break;
                        case MacroStepKind.MouseUp when row.MouseButton is MouseButton upButton: heldButtons.Remove(upButton); break;
                    }
                    continue;
                }
                Steps[++i].SetCollapsedWait(null);
                Steps[++i].SetCollapsedWait(null);
            }
            for (var i = 0; i < Steps.Count; i++) Steps[i].SetPressGroupPart(parts[i]);
            var changed = !_visibleSteps.SequenceEqual(visible);
            if (changed)
            {
                _visibleSteps = visible.AsReadOnly();
                _waitEditResult = null;
            }
            var selected = GetVisibleStep(_selectedStep);
            var selectionChanged = !ReferenceEquals(_selectedStep, selected);
            _selectedStep = selected;
            // ItemsSource changes can write a transient null selection back through a two-way binding.
            if (changed) OnPropertyChanged(nameof(VisibleSteps));
            if (changed || selectionChanged) OnPropertyChanged(nameof(SelectedStep));
            OnPropertyChanged(nameof(InsertionHint));
            OnPropertyChanged(nameof(SelectedPressKey));
            OnPropertyChanged(nameof(SelectedPressMouseButton));
            OnPropertyChanged(nameof(StepCountText));
            NotifyWaitEditor();
            RefreshWaitTargets();
        }
        finally
        {
            _refreshingVisibleSteps = false;
        }
    }

    public void RefreshCommands()
    {
        if (!CanEdit) CloseWaitEditor();
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanAddStep));
        InsertStepCommand.NotifyCanExecuteChanged();
        AddStepCommand.NotifyCanExecuteChanged();
        DuplicateStepCommand.NotifyCanExecuteChanged();
        DeleteStepCommand.NotifyCanExecuteChanged();
        MoveStepUpCommand.NotifyCanExecuteChanged();
        MoveStepDownCommand.NotifyCanExecuteChanged();
        PickCoordinatesCommand.NotifyCanExecuteChanged();
        MeasureOffsetCommand.NotifyCanExecuteChanged();
        CancelCoordinatePickCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsCapturingCoordinates));
        ShowProblemStepCommand.NotifyCanExecuteChanged();
        ExpandSelectedStepCommand.NotifyCanExecuteChanged();
        CollapseSelectedStepCommand.NotifyCanExecuteChanged();
        TogglePressGroupCommand.NotifyCanExecuteChanged();
        ExpandAllCommand.NotifyCanExecuteChanged();
        CollapseAllCommand.NotifyCanExecuteChanged();
        NotifyWaitEditor();
    }

    // The Down row of the open group that holds this row (its Down, Wait or Up), or null.
    private MacroStepViewModel? OpenPressHead(MacroStepViewModel? row) => row?.PressGroupPart switch
    {
        MacroPressGroupPart.OpenHead => row,
        MacroPressGroupPart.OpenMiddle => Steps[row.Number - 2],
        MacroPressGroupPart.OpenLast => Steps[row.Number - 3],
        _ => null
    };

    // Opening or closing changes the selected row's step count outside the SelectedStep setter, so commands refresh too.
    private void ChangeOpenPresses(Action<HashSet<MacroStepViewModel>> change)
    {
        if (!CanEdit) return;
        change(_openPresses);
        RefreshVisibleSteps();
        RefreshCommands();
    }

    private async Task PickCoordinatesAsync()
    {
        var destination = SelectedStep;
        if (!CanEdit || destination?.HasCoordinates != true) return;
        using var cancellation = new CancellationTokenSource();
        _coordinatePick = cancellation;
        try
        {
            CoordinatePickStatus = "Point to the screen position; capturing in 3 seconds…";
            RefreshCommands();
            var point = await _countdown("Pick", cancellation.Token);
            if (point is (int x, int y) && IsCaptureTarget(destination, cancellation) && destination.HasCoordinates)
            {
                destination.SetPosition(x, y);
                CoordinatePickStatus = $"Captured ({x}, {y}).";
                _showCaptured($"✓ {x}, {y}");
            }
            else
            {
                CoordinatePickStatus = string.Empty;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_coordinatePick, cancellation)) _coordinatePick = null;
            RefreshCommands();
        }
    }

    // Countdown to the start point, then the ruler from there; the measured distance becomes the offset.
    private async Task MeasureOffsetAsync()
    {
        var destination = SelectedStep;
        if (!CanEdit || destination?.HasOffset != true || _measure is not { } measure) return;
        using var cancellation = new CancellationTokenSource();
        _coordinatePick = cancellation;
        try
        {
            CoordinatePickStatus = "Point to where the move starts; measuring in 3 seconds…";
            RefreshCommands();
            // The ruler reads its own start point, so only a finished countdown matters here.
            var start = await _countdown("Start", cancellation.Token);
            if (start is null || !IsCaptureTarget(destination, cancellation) || !destination.HasOffset)
            {
                CoordinatePickStatus = string.Empty;
                return;
            }
            CoordinatePickStatus = "Click where the move should end. Esc cancels.";
            var distance = await measure(destination.MoveAsRaw, cancellation.Token);
            if (distance is (int x, int y) && IsCaptureTarget(destination, cancellation) && destination.HasOffset)
            {
                destination.SetPosition(x, y);
                CoordinatePickStatus = FormattableString.Invariant($"Measured ({x:+0;-0;0}, {y:+0;-0;0}).");
            }
            else
            {
                CoordinatePickStatus = string.Empty;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_coordinatePick, cancellation)) _coordinatePick = null;
            RefreshCommands();
        }
    }

    // A result applies only to the step that started the capture, while it is still selected and editable.
    private bool IsCaptureTarget(MacroStepViewModel destination, CancellationTokenSource cancellation) =>
        !cancellation.IsCancellationRequested && CanEdit && ReferenceEquals(destination, SelectedStep);

    private static async Task<(int X, int Y)?> WaitThenReadCursorAsync(string caption, CancellationToken token)
    {
        await Task.Delay(TimeSpan.FromSeconds(3), token);
        return WindowsInputSender.TryGetPhysicalCursorPosition(out var x, out var y) ? (x, y) : null;
    }

    // Null restores the view-less defaults: a plain wait for picks and no Measure.
    internal void ConfigureCoordinateCapture(
        Func<string, CancellationToken, Task<(int X, int Y)?>>? countdown,
        Action<string>? showCaptured,
        Func<bool, CancellationToken, Task<(int X, int Y)?>>? measure)
    {
        _countdown = countdown ?? WaitThenReadCursorAsync;
        _showCaptured = showCaptured ?? (static _ => { });
        _measure = measure;
        MeasureOffsetCommand.NotifyCanExecuteChanged();
    }

    public void CancelCoordinatePick()
    {
        _coordinatePick?.Cancel();
        CoordinatePickStatus = string.Empty;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseWaitEditor();
        CancelCoordinatePick();
        foreach (var step in Steps) step.Changed -= OnStepChanged;
    }
}

// Which timing rows the wait-time editor changes.
public enum MacroWaitScope
{
    PressHolds,
    BetweenSteps,
    AllWaits
}
