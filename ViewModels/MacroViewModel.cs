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
    private MacroDefinition _definition;
    private MacroDefinition _lastRepresentable;
    private readonly Func<bool> _canEdit;
    private ReadOnlyObservableCollection<MacroStepViewModel> _steps;
    private MacroStepViewModel? _selectedStep;
    private CancellationTokenSource? _coordinatePick;
    private bool _disposed;
    private string _validationMessage = string.Empty;
    private string _coordinatePickStatus = string.Empty;
    private int? _problemStepNumber;
    private bool _hasFormatError;

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
        DuplicateStepCommand = new RelayCommand(DuplicateStep, () => CanEdit && SelectedStep is not null && Steps.Count < MacroValidation.MaxSteps);
        DeleteStepCommand = new RelayCommand(DeleteStep, () => CanEdit && SelectedStep is not null);
        MoveStepUpCommand = new RelayCommand(() => MoveStep(-1), () => CanEdit && SelectedIndex > 0);
        MoveStepDownCommand = new RelayCommand(() => MoveStep(1), () => CanEdit && SelectedIndex >= 0 && SelectedIndex < Steps.Count - 1);
        PickCoordinatesCommand = new AsyncRelayCommand(PickCoordinatesAsync, () => CanEdit && SelectedStep?.HasCoordinates == true && _coordinatePick is null);
        ShowProblemStepCommand = new RelayCommand(ShowProblemStep, () => CanEdit && ProblemStepNumber <= Steps.Count);
        RefreshValidation(null);
    }

    public event EventHandler? Changed;
    public Guid Id => _definition.Id;
    public bool CanEdit => !_disposed && _canEdit();
    public string Label { get => _definition.Label; set => Change(_definition with { Label = value ?? string.Empty }, nameof(Label)); }
    public bool IsEnabled { get => _definition.IsEnabled; set => Change(_definition with { IsEnabled = value }, nameof(IsEnabled)); }
    public bool CancelOnMouseMovement { get => _definition.CancelOnMouseMovement; set => Change(_definition with { CancelOnMouseMovement = value }, nameof(CancelOnMouseMovement)); }
    public Key ShortcutKey { get => _definition.ShortcutKey; set => Change(_definition with { ShortcutKey = value }, nameof(ShortcutKey)); }
    public ModifierKeys ShortcutModifiers { get => _definition.ShortcutModifiers; set => Change(_definition with { ShortcutModifiers = value }, nameof(ShortcutModifiers)); }
    public bool ControlModifier { get => ShortcutModifiers.HasFlag(ModifierKeys.Control); set => SetModifier(ModifierKeys.Control, value); }
    public bool AltModifier { get => ShortcutModifiers.HasFlag(ModifierKeys.Alt); set => SetModifier(ModifierKeys.Alt, value); }
    public bool ShiftModifier { get => ShortcutModifiers.HasFlag(ModifierKeys.Shift); set => SetModifier(ModifierKeys.Shift, value); }
    public bool WindowsModifier { get => ShortcutModifiers.HasFlag(ModifierKeys.Windows); set => SetModifier(ModifierKeys.Windows, value); }
    public string ShortcutText => FormatShortcut(ShortcutKey, ShortcutModifiers);
    public ReadOnlyObservableCollection<MacroStepViewModel> Steps => _steps;
    public MacroStepViewModel? SelectedStep
    {
        get => _selectedStep;
        set
        {
            if (!CanEdit || (value is not null && !Steps.Contains(value))) return;
            if (SetProperty(ref _selectedStep, value))
            {
                CancelCoordinatePick();
                OnPropertyChanged(nameof(InsertionHint));
                RefreshCommands();
            }
        }
    }
    public int SelectedIndex => SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
    public int RecordingInsertionIndex => SelectedIndex < 0 ? Steps.Count : SelectedIndex + 1;
    public bool CanAddStep => CanEdit && Steps.Count < MacroValidation.MaxSteps;

    // Where Add step and Record place new rows; mirrors RecordingInsertionIndex for the editor copy.
    public string InsertionHint
    {
        get
        {
            var index = SelectedIndex;
            return index < 0 || index == Steps.Count - 1 ? "at the end" : $"after step {index + 1}";
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
    public string this[string columnName] => columnName == nameof(Label) ? Error : string.Empty;
    public string CoordinatePickStatus { get => _coordinatePickStatus; private set => SetProperty(ref _coordinatePickStatus, value); }
    public IRelayCommand InsertStepCommand { get; }
    public IRelayCommand<MacroStepKind> AddStepCommand { get; }
    public IRelayCommand DuplicateStepCommand { get; }
    public IRelayCommand DeleteStepCommand { get; }
    public IRelayCommand MoveStepUpCommand { get; }
    public IRelayCommand MoveStepDownCommand { get; }
    public IAsyncRelayCommand PickCoordinatesCommand { get; }
    public IRelayCommand ShowProblemStepCommand { get; }
    public static IReadOnlyList<Key> KeyOptions { get; } = KeyCatalog.SortKeys(
        Enum.GetValues<Key>().Where(key => MacroValidation.IsSupportedKey(key)).Distinct()).ToArray();
    public static IReadOnlyList<Key> ShortcutKeyOptions { get; } = KeyCatalog.SortKeys(KeyOptions.Append(Key.None)).ToArray();
    public static IReadOnlyList<MouseButton> MouseButtons { get; } = Enum.GetValues<MouseButton>();
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

    private static string FormatShortcut(Key key, ModifierKeys modifiers) => key == Key.None ? "No shortcut" :
        (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : string.Empty) +
        (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : string.Empty) +
        (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : string.Empty) +
        (modifiers.HasFlag(ModifierKeys.Windows) ? "Win+" : string.Empty) +
        KeyDisplayConverter.ToDisplayText(key);

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
        if (propertyName is nameof(ShortcutKey) or nameof(ShortcutModifiers)) OnPropertyChanged(nameof(ShortcutText));
        PublishChange();
    }

    private void SetModifier(ModifierKeys flag, bool enabled) => ShortcutModifiers = enabled ? ShortcutModifiers | flag : ShortcutModifiers & ~flag;
    private MacroStepViewModel CreateStep(MacroStep step)
    {
        var row = new MacroStepViewModel(step, () => CanEdit);
        row.Changed += OnStepChanged;
        return row;
    }
    private void OnStepChanged(object? sender, EventArgs e) => PublishChange();
    private void PublishChange()
    {
        RefreshValidation(null);
        RefreshCommands();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void InsertStep() => AddStep(MacroStepKind.KeyPress);
    private void AddStep(MacroStepKind kind)
    {
        if (!CanAddStep || !Enum.IsDefined(kind)) return;
        InsertRecording(RecordingInsertionIndex, [new MacroStep { Kind = kind, Key = Key.A, MouseButton = MouseButton.Left, WheelDelta = 120 }]);
    }
    private void DuplicateStep()
    {
        if (DuplicateStepCommand.CanExecute(null)) InsertRecording(SelectedIndex + 1, [SelectedStep!.ToModel()]);
    }
    private void DeleteStep()
    {
        if (!DeleteStepCommand.CanExecute(null)) return;
        var index = SelectedIndex;
        var rows = Steps.ToList();
        rows.RemoveAt(index);
        ReplaceSteps(rows, Math.Min(index, rows.Count - 1));
    }
    private void MoveStep(int direction)
    {
        var index = SelectedIndex;
        if (!CanEdit || index < 0 || index + direction < 0 || index + direction >= Steps.Count) return;
        var rows = Steps.ToList();
        (rows[index], rows[index + direction]) = (rows[index + direction], rows[index]);
        ReplaceSteps(rows, index + direction);
    }
    private void ShowProblemStep()
    {
        if (CanEdit && ProblemStepNumber is int number && number <= Steps.Count) SelectedStep = Steps[number - 1];
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
        OnPropertyChanged(nameof(SelectedStep));
        OnPropertyChanged(nameof(InsertionHint));
        PublishChange();
    }
    private void RenumberSteps()
    {
        for (var i = 0; i < Steps.Count; i++) Steps[i].Number = i + 1;
    }

    public void RefreshCommands()
    {
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
        CancelCoordinatePick();
        foreach (var step in Steps) step.Changed -= OnStepChanged;
    }
}
