using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private MacroDefinition _definition;
    private MacroDefinition _lastRepresentable;
    private readonly Func<bool> _canEdit;
    private ReadOnlyObservableCollection<MacroStepViewModel> _steps;
    private MacroStepViewModel? _selectedStep;
    private CancellationTokenSource? _coordinatePick;
    private bool _disposed;
    private string _validationMessage = string.Empty;
    private string _coordinatePickStatus = string.Empty;

    public MacroViewModel(MacroDefinition definition, Func<bool> canEdit)
    {
        _definition = definition;
        _lastRepresentable = definition;
        _canEdit = canEdit;
        _steps = new(new(definition.Steps.Select(CreateStep)));
        RenumberSteps();
        _selectedStep = Steps.FirstOrDefault();
        InsertStepCommand = new RelayCommand(InsertStep, () => CanEdit && Steps.Count < MacroValidation.MaxSteps);
        DuplicateStepCommand = new RelayCommand(DuplicateStep, () => CanEdit && SelectedStep is not null && Steps.Count < MacroValidation.MaxSteps);
        DeleteStepCommand = new RelayCommand(DeleteStep, () => CanEdit && SelectedStep is not null);
        MoveStepUpCommand = new RelayCommand(() => MoveStep(-1), () => CanEdit && SelectedIndex > 0);
        MoveStepDownCommand = new RelayCommand(() => MoveStep(1), () => CanEdit && SelectedIndex >= 0 && SelectedIndex < Steps.Count - 1);
        PickCoordinatesCommand = new AsyncRelayCommand(PickCoordinatesAsync, () => CanEdit && SelectedStep?.HasCoordinates == true && _coordinatePick is null);
        RefreshValidation(null);
    }

    public event EventHandler? Changed;
    public Guid Id => _definition.Id;
    public bool CanEdit => !_disposed && _canEdit();
    public string Label { get => _definition.Label; set => Change(_definition with { Label = value ?? string.Empty }, nameof(Label)); }
    public bool IsEnabled { get => _definition.IsEnabled; set => Change(_definition with { IsEnabled = value }, nameof(IsEnabled)); }
    public Key ShortcutKey { get => _definition.ShortcutKey; set => Change(_definition with { ShortcutKey = value }, nameof(ShortcutKey)); }
    public ModifierKeys ShortcutModifiers { get => _definition.ShortcutModifiers; set => Change(_definition with { ShortcutModifiers = value }, nameof(ShortcutModifiers)); }
    public bool ControlModifier { get => ShortcutModifiers.HasFlag(ModifierKeys.Control); set => SetModifier(ModifierKeys.Control, value); }
    public bool AltModifier { get => ShortcutModifiers.HasFlag(ModifierKeys.Alt); set => SetModifier(ModifierKeys.Alt, value); }
    public bool ShiftModifier { get => ShortcutModifiers.HasFlag(ModifierKeys.Shift); set => SetModifier(ModifierKeys.Shift, value); }
    public bool WindowsModifier { get => ShortcutModifiers.HasFlag(ModifierKeys.Windows); set => SetModifier(ModifierKeys.Windows, value); }
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
                RefreshCommands();
            }
        }
    }
    public int SelectedIndex => SelectedStep is null ? -1 : Steps.IndexOf(SelectedStep);
    public int RecordingInsertionIndex => SelectedIndex < 0 ? Steps.Count : SelectedIndex + 1;
    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }
    public bool IsPlayable => string.IsNullOrEmpty(ValidationMessage);
    public string Error => GetEditorFormatError(ToDefinition()) ?? string.Empty;
    public string this[string columnName] => columnName == nameof(Label) ? Error : string.Empty;
    public string CoordinatePickStatus { get => _coordinatePickStatus; private set => SetProperty(ref _coordinatePickStatus, value); }
    public IRelayCommand InsertStepCommand { get; }
    public IRelayCommand DuplicateStepCommand { get; }
    public IRelayCommand DeleteStepCommand { get; }
    public IRelayCommand MoveStepUpCommand { get; }
    public IRelayCommand MoveStepDownCommand { get; }
    public IAsyncRelayCommand PickCoordinatesCommand { get; }
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
        ValidationMessage = formatError is not null
            ? $"Not saved: {formatError} Playback is disabled until corrected."
            : conflict ?? MacroValidation.GetPlaybackError(current) ?? string.Empty;
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
    private void InsertStep()
    {
        if (!InsertStepCommand.CanExecute(null)) return;
        InsertRecording(RecordingInsertionIndex, [new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A, MouseButton = MouseButton.Left, WheelDelta = 120 }]);
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
        PublishChange();
    }
    private void RenumberSteps()
    {
        for (var i = 0; i < Steps.Count; i++) Steps[i].Number = i + 1;
    }

    public void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        InsertStepCommand.NotifyCanExecuteChanged();
        DuplicateStepCommand.NotifyCanExecuteChanged();
        DeleteStepCommand.NotifyCanExecuteChanged();
        MoveStepUpCommand.NotifyCanExecuteChanged();
        MoveStepDownCommand.NotifyCanExecuteChanged();
        PickCoordinatesCommand.NotifyCanExecuteChanged();
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
