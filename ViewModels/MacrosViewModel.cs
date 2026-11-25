using System;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
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
    private Func<string?, string?>? _requestName;
    private Func<MacroConfirmation, bool>? _confirm;
    private Func<string, CancellationToken, Task<(int X, int Y)?>>? _countdown;
    private Action<string>? _showCaptured;
    private Func<string, bool>? _setClipboardText;
    private Func<string?>? _getClipboardText;
    private Action<MacroNotice>? _notify;
    private Func<bool, CancellationToken, Task<(int X, int Y)?>>? _measure;
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
        NewMacroCommand = new RelayCommand(NewMacro, CanAddMacro);
        RenameMacroCommand = new RelayCommand(RenameSelectedMacro, () => CanEdit && SelectedMacro is not null);
        DuplicateMacroCommand = new RelayCommand(DuplicateMacro, () => CanEdit && SelectedMacro is not null && Definitions.Count < MacroValidation.MaxDefinitions);
        DeleteMacroCommand = new RelayCommand(DeleteMacro, () => CanEdit && SelectedMacro is not null);
        ClearStepsCommand = new RelayCommand(ClearSelectedSteps, () => CanEdit && SelectedMacro is { Steps.Count: > 0 });
        RecordCommand = new AsyncRelayCommand(RecordAsync, CanRecord);
        StopRecordingCommand = new RelayCommand(() => _stop?.Invoke(), () => IsRecording);
        ExportMacroCommand = new RelayCommand(ExportSelectedMacro, () => SelectedMacro is { HasFormatError: false });
        ImportMacroCommand = new RelayCommand(ImportMacro, CanAddMacro);
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
    public IRelayCommand RenameMacroCommand { get; }
    public IRelayCommand DuplicateMacroCommand { get; }
    public IRelayCommand DeleteMacroCommand { get; }
    public IRelayCommand ClearStepsCommand { get; }
    public IAsyncRelayCommand RecordCommand { get; }
    public IRelayCommand StopRecordingCommand { get; }
    public IRelayCommand ExportMacroCommand { get; }
    public IRelayCommand ImportMacroCommand { get; }

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

    // The editor view supplies its modal naming dialog: the current label (null for a new macro) in, the
    // accepted label out, or null when cancelled.
    internal void ConfigureNaming(Func<string?, string?>? requestName) => _requestName = requestName;

    // The editor view also supplies its confirmation dialog for destructive edits: true proceeds. Programmatic
    // callers without an attached view proceed unasked, as they do for naming.
    internal void ConfigureConfirmation(Func<MacroConfirmation, bool>? confirm) => _confirm = confirm;

    // The editor view supplies the cursor countdown badge, the captured flash and the Measure ruler.
    // Null restores the view-less defaults on every macro.
    internal void ConfigureCoordinateCapture(
        Func<string, CancellationToken, Task<(int X, int Y)?>>? countdown,
        Action<string>? showCaptured,
        Func<bool, CancellationToken, Task<(int X, int Y)?>>? measure)
    {
        _countdown = countdown;
        _showCaptured = showCaptured;
        _measure = measure;
        foreach (var macro in _definitions) macro.ConfigureCoordinateCapture(countdown, showCaptured, measure);
    }

    // The editor view supplies the clipboard and its notices. setText returns false and getText null while
    // another app holds the clipboard.
    internal void ConfigureShare(Func<string, bool>? setText, Func<string?>? getText, Action<MacroNotice>? notify)
    {
        _setClipboardText = setText;
        _getClipboardText = getText;
        _notify = notify;
    }

    // Physical monitor bounds, the same pixels saved positions use. Tests replace it.
    internal Func<Rectangle[]> Monitors { get; set; } = WindowsInputSender.GetPhysicalMonitorBounds;

    // Label rules only: a clean temporary definition keeps unrelated draft errors from blocking a name.
    internal static string? GetLabelError(string? label) =>
        MacroValidation.GetFormatError(new MacroDefinition { Label = label?.Trim() ?? string.Empty });

    private MacroViewModel CreateMacro(MacroDefinition definition)
    {
        var macro = new MacroViewModel(definition, () => CanEdit);
        macro.ConfigureCoordinateCapture(_countdown, _showCaptured, _measure);
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
    private bool CanAddMacro() => CanEdit && Definitions.Count < MacroValidation.MaxDefinitions;
    private void NewMacro()
    {
        if (!CanAddMacro()) return;
        // Programmatic callers without an attached editor view keep the model's default label.
        if (_requestName is null) AddMacro(new MacroDefinition().Label);
        else if (_requestName(null) is { } label) AddMacro(label);
    }

    // Creates, selects and publishes one macro under an accepted label. A modal dialog keeps dispatching
    // lifecycle changes, so availability is checked again here instead of trusted from before it opened.
    internal MacroViewModel? AddMacro(string label)
    {
        if (!CanAddMacro() || GetLabelError(label) is not null) return null;
        // New macros start on: without a shortcut one cannot fire, so Off would only add a step once it has one.
        var macro = CreateMacro(new MacroDefinition { Label = label.Trim(), IsEnabled = true });
        _definitions.Add(macro);
        SelectedMacro = macro;
        Publish();
        return macro;
    }
    private void RenameSelectedMacro()
    {
        if (!CanEdit || SelectedMacro is not { } macro || _requestName?.Invoke(macro.Label) is not { } label) return;
        RenameMacro(macro, label);
    }

    // Renames only the macro the dialog was opened for, while it still belongs to this editable profile.
    internal bool RenameMacro(MacroViewModel macro, string label)
    {
        if (!CanEdit || !Definitions.Contains(macro) || GetLabelError(label) is not null) return false;
        macro.Label = label.Trim();
        return true;
    }
    private void DuplicateMacro()
    {
        if (!DuplicateMacroCommand.CanExecute(null)) return;
        // The source always holds its own name, so a duplicate always gets the next number.
        var definition = SelectedMacro!.GetRepresentableDefinition().Duplicate();
        var duplicate = CreateMacro(definition with { Label = GetFreeLabel(definition.Label) });
        _definitions.Add(duplicate);
        SelectedMacro = duplicate;
        Publish();
    }

    // The label itself when no macro uses it, else the next free trailing number: "Ammo" becomes "Ammo 1", "Ammo 1" becomes "Ammo 2".
    private string GetFreeLabel(string label)
    {
        var baseLabel = label.Trim();
        // Invalid editor fields retain an older saved label; reserve both names.
        var names = Definitions.Select(macro => macro.Label.Trim())
            .Concat(_profile.Macros.Definitions.Select(macro => macro.Label.Trim()))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseLabel)) return baseLabel;

        var number = BigInteger.Zero;
        var separator = baseLabel.LastIndexOf(' ');
        if (separator > 0 && BigInteger.TryParse(baseLabel.AsSpan(separator + 1), NumberStyles.None,
            CultureInfo.InvariantCulture, out var existingNumber))
        {
            baseLabel = baseLabel[..separator].TrimEnd();
            number = existingNumber;
        }

        string free;
        do
        {
            var suffix = (++number).ToString(CultureInfo.InvariantCulture);
            var baseLength = Math.Min(baseLabel.Length, 100 - suffix.Length - 1);
            if (baseLength > 0 && char.IsHighSurrogate(baseLabel[baseLength - 1])) baseLength--;
            free = baseLength > 0 ? $"{baseLabel[..baseLength].TrimEnd()} {suffix}" : suffix;
        } while (names.Contains(free));
        return free;
    }

    private void ExportSelectedMacro()
    {
        if (!ExportMacroCommand.CanExecute(null) || _setClipboardText is null) return;
        var macro = SelectedMacro!;
        var code = MacroShareCode.Export(macro.GetRepresentableDefinition(), Monitors());
        Notify(_setClipboardText(code)
            ? new MacroNotice("Macro copied", $"The code for \"{macro.Label.Trim()}\" is on the clipboard. Paste it anywhere to share the macro.")
            : ClipboardBusyNotice);
    }

    private void ImportMacro()
    {
        if (!CanAddMacro() || _getClipboardText is null) return;
        if (_getClipboardText() is not { } text)
        {
            Notify(ClipboardBusyNotice);
            return;
        }

        var result = MacroShareCode.Import(text, Monitors());
        if (result.Macro is not { } definition)
        {
            Notify(new MacroNotice("Can't import macro", result.Error switch
            {
                MacroImportError.NotFound => $"The clipboard has no macro code. Copy one complete code from {MacroShareCode.Prefix} through {MacroShareCode.Suffix}.",
                MacroImportError.NewerVersion => "This macro code was made with a newer version of sWinShortcuts. Update the app to import it.",
                MacroImportError.NewerMinor => "This macro code uses options from a newer version of sWinShortcuts. Update the app to import it.",
                _ => $"The clipboard must contain one valid macro code. Copy it from {MacroShareCode.Prefix} through {MacroShareCode.Suffix} and try again."
            }, IsWarning: true));
            return;
        }

        var macro = CreateMacro(definition with { Label = GetFreeLabel(definition.Label) });
        _definitions.Add(macro);
        SelectedMacro = macro;
        Publish();
        if (result.LayoutDiffers)
        {
            var positions = result.OffScreenPositions switch
            {
                0 => "Its screen positions are all on your screens, but they may point at different things.",
                1 => "1 screen position is outside your screens; change it before you play the macro.",
                var count => $"{count.ToString(CultureInfo.InvariantCulture)} screen positions are outside your screens; change them before you play the macro."
            };
            Notify(new MacroNotice("Different screen layout",
                $"\"{macro.Label}\" was made on a different screen layout and keeps its screen positions. {positions}", IsWarning: true));
        }
        else if (result.OffScreenPositions > 0)
        {
            var positions = result.OffScreenPositions == 1
                ? "1 screen position is outside your screens"
                : $"{result.OffScreenPositions.ToString(CultureInfo.InvariantCulture)} screen positions are outside your screens";
            Notify(new MacroNotice("Screen position outside your screens",
                $"In \"{macro.Label}\", {positions}; change it before you play the macro.", IsWarning: true));
        }
    }

    private static readonly MacroNotice ClipboardBusyNotice = new("Clipboard busy",
        "Another app is using the clipboard. Try again in a moment.", IsWarning: true);
    private void Notify(MacroNotice notice) => _notify?.Invoke(notice);
    private void DeleteMacro()
    {
        if (!DeleteMacroCommand.CanExecute(null)) return;
        var macro = SelectedMacro!;
        var count = macro.Steps.Count;
        if (count > 0 && _confirm is not null && !_confirm(new MacroConfirmation("Delete macro?",
                $"\"{macro.Label}\" and its {count} {(count == 1 ? "step" : "steps")} will be permanently deleted.", "Delete")))
            return;
        // The modal dialog keeps dispatching lifecycle changes; delete only the macro it asked about.
        if (!ReferenceEquals(SelectedMacro, macro) || !DeleteMacroCommand.CanExecute(null)) return;
        var index = _definitions.IndexOf(macro);
        macro.Changed -= OnMacroChanged;
        _definitions.Remove(macro);
        macro.Dispose();
        SelectedMacro = _definitions.Count == 0 ? null : _definitions[Math.Min(index, _definitions.Count - 1)];
        Publish();
    }
    private void ClearSelectedSteps()
    {
        if (!ClearStepsCommand.CanExecute(null)) return;
        var macro = SelectedMacro!;
        var count = macro.Steps.Count;
        if (_confirm is not null && !_confirm(new MacroConfirmation("Clear all steps?",
                $"All {count} {(count == 1 ? "step" : "steps")} in \"{macro.Label}\" will be removed. Its name, shortcut, and options stay.",
                "Clear steps")))
            return;
        if (!ReferenceEquals(SelectedMacro, macro) || !ClearStepsCommand.CanExecute(null)) return;
        macro.ClearSteps();
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
            MacroSessionMode.WaitingForPhysicalModifiers or MacroSessionMode.Playing or MacroSessionMode.WaitingForMouseRelease;
        var status = snapshot.Mode switch
        {
            MacroSessionMode.PreparingPlayback or MacroSessionMode.PreparingRecording or MacroSessionMode.WaitingForShortcutRelease or
                MacroSessionMode.WaitingForPhysicalModifiers => "Preparing",
            MacroSessionMode.Recording => "Recording",
            MacroSessionMode.Playing => "Playing",
            MacroSessionMode.WaitingForMouseRelease => "Waiting for mouse buttons to be released",
            MacroSessionMode.Finishing => "Finishing",
            MacroSessionMode.Faulted => "Faulted",
            _ => "Idle"
        };
        SessionStatus = snapshot.Mode switch
        {
            MacroSessionMode.Idle => status,
            // A take starts once its profile's app is in front; until then there is nothing to time or count.
            MacroSessionMode.WaitingForRecordingTarget => $"Switch to {snapshot.OwnerProfile?.Name ?? _profile.Name} to start recording",
            _ => $"{status} · {snapshot.Elapsed:mm\\:ss}{DescribeProgress(snapshot)}"
        };
        if (!string.IsNullOrEmpty(snapshot.FailureReason)) SessionStatus += $" · {snapshot.FailureReason}";
        if (_sessionBusy != wasBusy) RefreshAvailability();
    }
    // Recording counts captured steps; playback names the step it is on (RowCount is 1-based there).
    private static string DescribeProgress(MacroSessionSnapshot snapshot)
    {
        var row = snapshot.RowCount;
        if (row <= 0) return string.Empty;
        if (snapshot.Mode == MacroSessionMode.Recording) return $" · {row} {(row == 1 ? "step" : "steps")}";
        var total = snapshot.OwnerProfile?.Macros.Definitions.FirstOrDefault(macro => macro.Id == snapshot.MacroId)?.Steps.Length ?? 0;
        return total >= row ? $" · step {row} of {total}" : $" · step {row}";
    }
    internal void ShowRecordingResult(MacroRecordingResult result)
    {
        var detail = result.AppendedBalancingReleases ? " Held inputs were released in the saved take." : string.Empty;
        var count = result.Steps.Length;
        var ending = result.EndReason switch
        {
            MacroRecordingEndReason.RowLimit => $" · stopped at the {MacroValidation.MaxSteps}-step limit",
            MacroRecordingEndReason.DurationLimit => " · stopped at the 10-minute limit",
            MacroRecordingEndReason.Interrupted => " · interrupted",
            MacroRecordingEndReason.Cancelled => " · cancelled",
            MacroRecordingEndReason.Faulted => " · faulted",
            _ => string.Empty
        };
        SessionStatus = result.FailureReason is { Length: > 0 } failure
            ? $"Recording finished: {failure}{detail}"
            : count == 0
                ? "Recording stopped before anything was captured."
                : $"Recording saved · {count} {(count == 1 ? "step" : "steps")} added{ending}.{detail}";
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
        ClearStepsCommand.NotifyCanExecuteChanged();
        RenameMacroCommand.NotifyCanExecuteChanged();
        RecordCommand.NotifyCanExecuteChanged();
        StopRecordingCommand.NotifyCanExecuteChanged();
        ExportMacroCommand.NotifyCanExecuteChanged();
        ImportMacroCommand.NotifyCanExecuteChanged();
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

// A destructive edit the view confirms: the question, what is lost, and the specific action button text.
internal readonly record struct MacroConfirmation(string Title, string Message, string ActionText);

// A message the view shows after Export or Import; a warning uses the warning icon.
internal readonly record struct MacroNotice(string Title, string Message, bool IsWarning = false);
