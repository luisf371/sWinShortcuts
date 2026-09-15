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
using sWinShortcuts.Converters;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Utilities;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.ViewModels;

public sealed class MacroViewModel : ViewModelBase, IDisposable, IDataErrorInfo
{
    private static readonly InputTriggerDisplayConverter ShortcutDisplayConverter = new();
    private MacroDefinition _definition;
    private MacroDefinition _lastRepresentable;
    private readonly Func<bool> _canEdit;
    private ReadOnlyObservableCollection<MacroStepViewModel> _steps;
    private IReadOnlyList<MacroStepViewModel> _visibleSteps = [];
    private MacroStepViewModel? _selectedStep;
    private CancellationTokenSource? _coordinatePick;
    private bool _disposed;
    private string _validationMessage = string.Empty;
    private string _coordinatePickStatus = string.Empty;
    private int? _problemStepNumber;
    private bool _hasFormatError;
    private bool _batchChangingSteps;
    private bool _collapseSteps = true;
    private bool _refreshingVisibleSteps;
    private bool _isWaitEditorOpen;
    private string _allWaitDurationText = string.Empty;
    private string? _waitEditResult;

    public MacroViewModel(MacroDefinition definition, Func<bool> canEdit)
    {
        _definition = definition;
        _lastRepresentable = definition;
        _canEdit = canEdit;
        _steps = new(new(definition.Steps.Select(CreateStep)));
        RenumberSteps();
        _selectedStep = Steps.FirstOrDefault();
        InsertStepCommand = new RelayCommand(InsertStep, () => CanAddStep);
        AddStepCommand = new RelayCommand<MacroStepKind>(AddStep, _ => CanAddStep);
        DuplicateStepCommand = new RelayCommand(DuplicateStep, () => CanEdit && SelectedStep is not null && Steps.Count + SelectedStep.SourceStepCount <= MacroValidation.MaxSteps);
        DeleteStepCommand = new RelayCommand(DeleteStep, () => CanEdit && SelectedStep is not null);
        MoveStepUpCommand = new RelayCommand(() => MoveStep(-1), () => CanEdit && SelectedIndex > 0);
        MoveStepDownCommand = new RelayCommand(() => MoveStep(1), () => CanEdit && SelectedIndex >= 0 && RecordingInsertionIndex < Steps.Count);
        PickCoordinatesCommand = new AsyncRelayCommand(PickCoordinatesAsync, () => CanEdit && SelectedStep?.HasCoordinates == true && _coordinatePick is null);
        ShowProblemStepCommand = new RelayCommand(ShowProblemStep, () => CanEdit && ProblemStepNumber <= Steps.Count);
        ExpandSelectedStepCommand = new RelayCommand(() => CollapseSteps = false, () => CanEdit && SelectedStep?.IsCollapsedPress == true);
        ShowWaitEditorCommand = new RelayCommand(ShowWaitEditor, () => CanEdit && WaitStepCount > 0);
        CloseWaitEditorCommand = new RelayCommand(CloseWaitEditor, () => IsWaitEditorOpen);
        ApplyAllWaitTimesCommand = new RelayCommand(ApplyAllWaitTimes, CanApplyAllWaitTimes);
        RefreshValidation(null);
    }

    public event EventHandler? Changed;
    public Guid Id => _definition.Id;
    public bool CanEdit => !_disposed && _canEdit();
    public string Label { get => _definition.Label; set => Change(_definition with { Label = value ?? string.Empty }, nameof(Label)); }
    public bool IsEnabled { get => _definition.IsEnabled; set => Change(_definition with { IsEnabled = value }, nameof(IsEnabled)); }
    public bool ToggleMode { get => _definition.ToggleMode; set => Change(_definition with { ToggleMode = value }, nameof(ToggleMode)); }
    public bool CancelOnMouseMovement { get => _definition.CancelOnMouseMovement; set => Change(_definition with { CancelOnMouseMovement = value }, nameof(CancelOnMouseMovement)); }
    public Key ShortcutKey { get => _definition.ShortcutKey; set => Change(_definition with { ShortcutKey = value, ShortcutMouseButton = null }, nameof(ShortcutKey)); }
    public InputTrigger ShortcutTrigger
    {
        get => _definition.ShortcutTrigger;
        set
        {
            if (value.Kind is not (InputTriggerKind.None or InputTriggerKind.KeyboardKey or InputTriggerKind.MouseButton) ||
                (value.Kind == InputTriggerKind.MouseButton && !Enum.IsDefined(value.MouseButton))) return;
            Change(_definition with
            {
                ShortcutKey = value.Kind == InputTriggerKind.KeyboardKey ? value.Key : Key.None,
                ShortcutMouseButton = value.Kind == InputTriggerKind.MouseButton ? value.MouseButton : null
            }, nameof(ShortcutTrigger));
        }
    }
    public ModifierKeys ShortcutModifiers { get => _definition.ShortcutModifiers; set => Change(_definition with { ShortcutModifiers = value }, nameof(ShortcutModifiers)); }
    public bool ControlModifier { get => ShortcutModifiers.HasFlag(ModifierKeys.Control); set => SetModifier(ModifierKeys.Control, value); }
    public bool AltModifier { get => ShortcutModifiers.HasFlag(ModifierKeys.Alt); set => SetModifier(ModifierKeys.Alt, value); }
    public bool ShiftModifier { get => ShortcutModifiers.HasFlag(ModifierKeys.Shift); set => SetModifier(ModifierKeys.Shift, value); }
    public bool WindowsModifier { get => ShortcutModifiers.HasFlag(ModifierKeys.Windows); set => SetModifier(ModifierKeys.Windows, value); }
    public string ShortcutText => FormatShortcut(ShortcutTrigger, ShortcutModifiers);
    public ReadOnlyObservableCollection<MacroStepViewModel> Steps => _steps;
    public IReadOnlyList<MacroStepViewModel> VisibleSteps => _visibleSteps;
    public bool CollapseSteps
    {
        get => _collapseSteps;
        set
        {
            if (!CanEdit || !SetProperty(ref _collapseSteps, value)) return;
            RefreshVisibleSteps();
            RefreshCommands();
        }
    }
    public int WaitStepCount => Steps.Count(step => step.Kind == MacroStepKind.Wait);
    public string StepCountText => $"{Steps.Count} {(Steps.Count == 1 ? "step" : "steps")}";
    public bool IsWaitEditorOpen => _isWaitEditorOpen;
    public string AllWaitDurationText
    {
        get => _allWaitDurationText;
        set
        {
            if (!CanEdit || !IsWaitEditorOpen || !SetProperty(ref _allWaitDurationText, value ?? string.Empty)) return;
            _waitEditResult = null;
            NotifyWaitEditor();
        }
    }
    public bool HasWaitEditError => AllWaitDurationText.Length > 0 && !TryGetAllWaitDuration(out _);
    public bool WaitEditApplied => _waitEditResult is not null;
    public string WaitEditMessage
    {
        get
        {
            var count = WaitStepCount;
            if (count == 0) return "This macro has no Wait steps.";
            if (HasWaitEditError) return $"Enter a whole number from 0 to {MacroValidation.MaxDurationMs.ToString(CultureInfo.InvariantCulture)}.";
            if (_waitEditResult is not null) return _waitEditResult;
            var holds = VisibleSteps.Count(step => step.IsCollapsedPress);
            var holdText = holds == 0 ? string.Empty : $", including {holds} press {(holds == 1 ? "hold" : "holds")}";
            return $"Applies to {count} Wait {(count == 1 ? "step" : "steps")}{holdText}.";
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
        nameof(AllWaitDurationText) when HasWaitEditError => WaitEditMessage,
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
    public IRelayCommand ShowProblemStepCommand { get; }
    public IRelayCommand ExpandSelectedStepCommand { get; }
    public IRelayCommand ShowWaitEditorCommand { get; }
    public IRelayCommand CloseWaitEditorCommand { get; }
    public IRelayCommand ApplyAllWaitTimesCommand { get; }
    public static IReadOnlyList<Key> KeyOptions { get; } = KeyCatalog.SortKeys(
        Enum.GetValues<Key>().Where(key => MacroValidation.IsSupportedKey(key)).Distinct()).ToArray();
    public static IReadOnlyList<Key> ShortcutKeyOptions { get; } = KeyCatalog.SortKeys(KeyOptions.Append(Key.None)).ToArray();
    public static IReadOnlyList<MouseButton> MouseButtons { get; } = Enum.GetValues<MouseButton>();
    public static IReadOnlyList<InputTrigger> ShortcutOptions { get; } =
        [InputTrigger.None, .. MouseButtons.Select(InputTrigger.FromMouseButton), .. KeyOptions.Select(InputTrigger.FromKey)];
    public static IReadOnlyList<KeyValuePair<MacroStepKind, string>> ActionOptions { get; } =
    [
        new(MacroStepKind.KeyPress, "Key press"), new(MacroStepKind.KeyDown, "Key down"), new(MacroStepKind.KeyUp, "Key up"),
        new(MacroStepKind.Wait, "Wait"), new(MacroStepKind.MouseClick, "Mouse click"), new(MacroStepKind.MouseDown, "Mouse down"),
        new(MacroStepKind.MouseUp, "Mouse up"), new(MacroStepKind.MoveTo, "Move to"), new(MacroStepKind.MouseWheel, "Mouse wheel")
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

    private static string FormatShortcut(InputTrigger trigger, ModifierKeys modifiers) => trigger.Kind == InputTriggerKind.None ? "No shortcut" :
        (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : string.Empty) +
        (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : string.Empty) +
        (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : string.Empty) +
        (modifiers.HasFlag(ModifierKeys.Windows) ? "Win+" : string.Empty) +
        ShortcutDisplayConverter.Convert(trigger, typeof(string), null, CultureInfo.CurrentCulture);

    private void Change(MacroDefinition value, string propertyName)
    {
        if (!CanEdit || value == _definition) return;
        _definition = value;
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(ShortcutModifiers))
        {
            OnPropertyChanged(nameof(ControlModifier));
            OnPropertyChanged(nameof(AltModifier));
            OnPropertyChanged(nameof(ShiftModifier));
            OnPropertyChanged(nameof(WindowsModifier));
        }
        if (propertyName == nameof(ShortcutKey)) OnPropertyChanged(nameof(ShortcutTrigger));
        if (propertyName == nameof(ShortcutTrigger)) OnPropertyChanged(nameof(ShortcutKey));
        if (propertyName is nameof(ShortcutKey) or nameof(ShortcutTrigger) or nameof(ShortcutModifiers)) OnPropertyChanged(nameof(ShortcutText));
        PublishChange();
    }

    private void SetModifier(ModifierKeys flag, bool enabled) => ShortcutModifiers = enabled ? ShortcutModifiers | flag : ShortcutModifiers & ~flag;
    private MacroStepViewModel CreateStep(MacroStep step)
    {
        var row = new MacroStepViewModel(step, () => CanEdit);
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

    private bool TryGetAllWaitDuration(out int duration) =>
        int.TryParse(AllWaitDurationText, NumberStyles.Integer, CultureInfo.InvariantCulture, out duration) &&
        duration is >= 0 and <= MacroValidation.MaxDurationMs;

    private bool CanApplyAllWaitTimes() => CanEdit && IsWaitEditorOpen && WaitStepCount > 0 && TryGetAllWaitDuration(out _);

    private void ShowWaitEditor()
    {
        if (!CanEdit || WaitStepCount == 0 || IsWaitEditorOpen) return;
        SetProperty(ref _isWaitEditorOpen, true, nameof(IsWaitEditorOpen));
        NotifyWaitEditor();
    }

    public void CloseWaitEditor()
    {
        if (!IsWaitEditorOpen) return;
        SetProperty(ref _isWaitEditorOpen, false, nameof(IsWaitEditorOpen));
        SetProperty(ref _allWaitDurationText, string.Empty, nameof(AllWaitDurationText));
        _waitEditResult = null;
        NotifyWaitEditor();
    }

    private void ApplyAllWaitTimes()
    {
        if (!CanApplyAllWaitTimes() || !TryGetAllWaitDuration(out var duration)) return;
        var count = WaitStepCount;
        SetAllWaitTimes(duration);
        _waitEditResult = $"Set {count} Wait {(count == 1 ? "step" : "steps")} to {duration.ToString(CultureInfo.InvariantCulture)} ms.";
        NotifyWaitEditor();
    }

    private void NotifyWaitEditor()
    {
        OnPropertyChanged(nameof(WaitEditMessage));
        OnPropertyChanged(nameof(HasWaitEditError));
        OnPropertyChanged(nameof(WaitEditApplied));
        ShowWaitEditorCommand.NotifyCanExecuteChanged();
        CloseWaitEditorCommand.NotifyCanExecuteChanged();
        ApplyAllWaitTimesCommand.NotifyCanExecuteChanged();
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

    public void SetAllWaitTimes(int durationMs)
    {
        if (!CanEdit || durationMs is < 0 or > MacroValidation.MaxDurationMs) return;
        var text = durationMs.ToString(CultureInfo.InvariantCulture);
        var changed = false;
        _batchChangingSteps = true;
        try
        {
            foreach (var step in Steps)
            {
                if (step.Kind != MacroStepKind.Wait || step.DurationText == text) continue;
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
    private void AddStep(MacroStepKind kind)
    {
        if (!CanAddStep || !Enum.IsDefined(kind)) return;
        InsertRecording(RecordingInsertionIndex, [new MacroStep { Kind = kind, Key = Key.A, MouseButton = MouseButton.Left, WheelDelta = 120 }]);
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
        ReplaceSteps(result, index);
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

    private bool CanCollapseTriplet(int index, HashSet<ushort> heldKeys, HashSet<MouseButton> heldButtons)
    {
        if (!CollapseSteps || index + 2 >= Steps.Count ||
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

    private void RefreshVisibleSteps()
    {
        _refreshingVisibleSteps = true;
        try
        {
            var visible = new List<MacroStepViewModel>(Steps.Count);
            HashSet<ushort> heldKeys = [];
            HashSet<MouseButton> heldButtons = [];
            for (var i = 0; i < Steps.Count; i++)
            {
                var row = Steps[i];
                var wait = CanCollapseTriplet(i, heldKeys, heldButtons) ? Steps[i + 1] : null;
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
        ShowProblemStepCommand.NotifyCanExecuteChanged();
        ExpandSelectedStepCommand.NotifyCanExecuteChanged();
        NotifyWaitEditor();
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
            await Task.Delay(TimeSpan.FromSeconds(3), cancellation.Token);
            if (CanEdit && ReferenceEquals(destination, SelectedStep) && destination.HasCoordinates &&
                WindowsInputSender.TryGetPhysicalCursorPosition(out var x, out var y))
            {
                destination.SetPosition(x, y);
                CoordinatePickStatus = $"Captured ({x}, {y}).";
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_coordinatePick, cancellation)) _coordinatePick = null;
            RefreshCommands();
        }
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
