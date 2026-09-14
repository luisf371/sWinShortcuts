using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.ViewModels;

public sealed class MacrosViewModel : ViewModelBase, IDisposable
{
    private readonly Profile _profile;
    private readonly Action _changed;
    private readonly ObservableCollection<MacroViewModel> _definitions;
    private MacroViewModel? _selectedMacro;
    private MacroViewModel? _recordingDestination;
    private Func<MacroViewModel, Task>? _record;
    private Action? _stop;
    private Action<Key?>? _stopGesture;
    private Action? _beforePublish;
    private Action? _leaveEditor;
    private Func<Guid, string?>? _shortcutError;
    private bool _disposed;
    private bool _disposeRequested;
    private bool _sessionBusy;
    private bool _isPlaying;
    private string _sessionStatus = "Idle";

    public MacrosViewModel(Profile profile, Action changed)
    {
        _profile = profile;
        _changed = changed;
        _definitions = new(profile.Macros.Definitions.Select(CreateMacro));
        Definitions = new(_definitions);
        _selectedMacro = _definitions.FirstOrDefault();
        NewMacroCommand = new RelayCommand(NewMacro, () => CanEdit && Definitions.Count < MacroValidation.MaxDefinitions);
        DuplicateMacroCommand = new RelayCommand(DuplicateMacro, () => CanEdit && SelectedMacro is not null && Definitions.Count < MacroValidation.MaxDefinitions);
        DeleteMacroCommand = new RelayCommand(DeleteMacro, () => CanEdit && SelectedMacro is not null);
        RecordCommand = new AsyncRelayCommand(RecordAsync, CanRecord);
        StopRecordingCommand = new RelayCommand(() => _stop?.Invoke(), () => IsRecording);
    }

    public ReadOnlyObservableCollection<MacroViewModel> Definitions { get; }
    public bool CanEdit => !_disposed && !_disposeRequested && !IsRecording && _profile.IsEnabled && !_profile.IsPersistenceSuspended;
    public bool IsRecording => _recordingDestination is not null;
    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }
    public string SessionStatus { get => _sessionStatus; private set => SetProperty(ref _sessionStatus, value); }
    public string? LoadError => _profile.Macros.LoadError;
    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);
    public bool IsEnabled
    {
        get => _profile.Macros.IsEnabled;
        set
        {
            if (!CanEdit || value == IsEnabled) return;
            _beforePublish?.Invoke();
            _profile.Macros.IsEnabled = value;
            OnPropertyChanged();
            _changed();
            RefreshValidation();
        }
    }
    public MacroViewModel? SelectedMacro
    {
        get => _selectedMacro;
        set
        {
            if (IsRecording || (value is not null && !Definitions.Contains(value))) return;
            if (ReferenceEquals(_selectedMacro, value)) return;
            _selectedMacro?.CancelCoordinatePick();
            _selectedMacro?.CloseWaitEditor();
            _selectedMacro = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            RefreshAvailability();
        }
    }
    public bool HasSelection => SelectedMacro is not null;
    public IRelayCommand NewMacroCommand { get; }
    public IRelayCommand DuplicateMacroCommand { get; }
    public IRelayCommand DeleteMacroCommand { get; }
    public IAsyncRelayCommand RecordCommand { get; }
    public IRelayCommand StopRecordingCommand { get; }

    internal void ConfigureRuntime(
        Func<MacroViewModel, Task> record, Action stop, Action<Key?> stopGesture,
        Action beforePublish, Func<Guid, string?> shortcutError, Action leaveEditor)
    {
        _record = record;
        _stop = stop;
        _stopGesture = stopGesture;
        _beforePublish = beforePublish;
        _shortcutError = shortcutError;
        _leaveEditor = leaveEditor;
        RefreshAvailability();
        RefreshValidation();
    }

    private MacroViewModel CreateMacro(MacroDefinition definition)
    {
        var macro = new MacroViewModel(definition, () => CanEdit);
        macro.Changed += OnMacroChanged;
        return macro;
    }
    private void OnMacroChanged(object? sender, EventArgs e) => Publish();
    private void Publish()
    {
        if (_disposed) return;
        _beforePublish?.Invoke();
        _profile.Macros.Definitions = Definitions.Select(macro => macro.GetRepresentableDefinition()).ToArray();
        _changed();
        RefreshValidation();
        RefreshAvailability();
    }
    private void NewMacro()
    {
        if (!NewMacroCommand.CanExecute(null)) return;
        var macro = CreateMacro(new MacroDefinition());
        _definitions.Add(macro);
        SelectedMacro = macro;
        Publish();
    }
    private void DuplicateMacro()
    {
        if (!DuplicateMacroCommand.CanExecute(null)) return;
        var definition = SelectedMacro!.GetRepresentableDefinition().Duplicate();
        var baseLabel = definition.Label.Trim();
        var number = BigInteger.Zero;
        var separator = baseLabel.LastIndexOf(' ');
        if (separator > 0 && BigInteger.TryParse(baseLabel.AsSpan(separator + 1), NumberStyles.None,
            CultureInfo.InvariantCulture, out var existingNumber))
        {
            baseLabel = baseLabel[..separator].TrimEnd();
            number = existingNumber;
        }

        // Invalid editor fields retain an older saved label; reserve both names.
        var names = Definitions.Select(macro => macro.Label.Trim())
            .Concat(_profile.Macros.Definitions.Select(macro => macro.Label.Trim()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string label;
        do
        {
            var suffix = (++number).ToString(CultureInfo.InvariantCulture);
            var baseLength = Math.Min(baseLabel.Length, 100 - suffix.Length - 1);
            if (baseLength > 0 && char.IsHighSurrogate(baseLabel[baseLength - 1])) baseLength--;
            label = baseLength > 0 ? $"{baseLabel[..baseLength].TrimEnd()} {suffix}" : suffix;
        } while (names.Contains(label));

        var duplicate = CreateMacro(definition with { Label = label });
        _definitions.Add(duplicate);
        SelectedMacro = duplicate;
        Publish();
    }
    private void DeleteMacro()
    {
        if (!DeleteMacroCommand.CanExecute(null)) return;
        var macro = SelectedMacro!;
        var index = _definitions.IndexOf(macro);
        macro.Changed -= OnMacroChanged;
        _definitions.Remove(macro);
        macro.Dispose();
        SelectedMacro = _definitions.Count == 0 ? null : _definitions[Math.Min(index, _definitions.Count - 1)];
        Publish();
    }
    private bool CanRecord() => CanEdit && !_sessionBusy && _record is not null && SelectedMacro is { } macro &&
        macro.Steps.Count < MacroValidation.MaxSteps && string.IsNullOrEmpty(macro.Error);
    private Task RecordAsync() => CanRecord() ? _record!(SelectedMacro!) : Task.CompletedTask;

    public void BeginStopGesture(Key? key = null) => _stopGesture?.Invoke(key);
    public void LeaveEditor()
    {
        SelectedMacro?.CancelCoordinatePick();
        SelectedMacro?.CloseWaitEditor();
        if (IsRecording) _leaveEditor?.Invoke();
    }

    public void SetRecordingDestination(MacroViewModel? macro)
    {
        SelectedMacro?.CancelCoordinatePick();
        _recordingDestination = macro;
        OnPropertyChanged(nameof(IsRecording));
        RefreshAvailability();
        if (macro is null && _disposeRequested) Dispose();
    }
    internal void RefreshSession(MacroSessionSnapshot snapshot)
    {
        var wasBusy = _sessionBusy;
        _sessionBusy = snapshot.Mode != MacroSessionMode.Idle;
        if (_sessionBusy && !wasBusy)
        {
            foreach (var macro in Definitions) macro.CancelCoordinatePick();
        }
        IsPlaying = snapshot.Mode is MacroSessionMode.PreparingPlayback or MacroSessionMode.WaitingForShortcutRelease or
            MacroSessionMode.WaitingForPhysicalModifiers or MacroSessionMode.Playing;
        var status = snapshot.Mode switch
        {
            MacroSessionMode.PreparingPlayback or MacroSessionMode.PreparingRecording or MacroSessionMode.WaitingForShortcutRelease or
                MacroSessionMode.WaitingForPhysicalModifiers => "Preparing",
            MacroSessionMode.Recording => "Recording",
            MacroSessionMode.Playing => "Playing",
            MacroSessionMode.Finishing => "Finishing",
            MacroSessionMode.Faulted => "Faulted",
            _ => "Idle"
        };
        SessionStatus = snapshot.Mode == MacroSessionMode.Idle ? status :
            $"{status} · {snapshot.Elapsed:mm\\:ss} · {snapshot.RowCount} rows";
        if (!string.IsNullOrEmpty(snapshot.FailureReason)) SessionStatus += $" · {snapshot.FailureReason}";
        if (_sessionBusy != wasBusy) RefreshAvailability();
    }
    internal void ShowRecordingResult(MacroRecordingResult result)
    {
        var detail = result.AppendedBalancingReleases ? " Held inputs were released in the saved take." : string.Empty;
        SessionStatus = result.FailureReason is { Length: > 0 } failure
            ? $"Recording finished: {failure}{detail}"
            : $"Recording saved · {result.Steps.Length} rows · {result.EndReason}.{detail}";
    }
    internal void ShowFailure(string message) => SessionStatus = message;
    public void RefreshValidation()
    {
        foreach (var macro in Definitions) macro.RefreshValidation(_shortcutError?.Invoke(macro.Id));
    }
    public void RefreshAvailability()
    {
        OnPropertyChanged(nameof(CanEdit));
        NewMacroCommand.NotifyCanExecuteChanged();
        DuplicateMacroCommand.NotifyCanExecuteChanged();
        DeleteMacroCommand.NotifyCanExecuteChanged();
        RecordCommand.NotifyCanExecuteChanged();
        StopRecordingCommand.NotifyCanExecuteChanged();
        foreach (var macro in Definitions) macro.RefreshCommands();
    }
    public void Dispose()
    {
        if (_disposed) return;
        LeaveEditor();
        if (IsRecording)
        {
            _disposeRequested = true;
            return;
        }
        _disposed = true;
        foreach (var macro in Definitions)
        {
            macro.Changed -= OnMacroChanged;
            macro.Dispose();
        }
    }
}
