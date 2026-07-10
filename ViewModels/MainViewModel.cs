using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IProfileManager _profileManager;
    private readonly IDialogService _dialogService;
    private readonly ObservableCollection<ProfileViewModel> _profiles = [];
    private readonly IReadOnlyList<Key> _keyOptions = KeyCatalog.GetCommonKeys();
    private readonly IReadOnlyList<Key> _keyOptionsWithNone;
    private readonly IReadOnlyList<CapsLockMode> _capsLockModes = Enum.GetValues<CapsLockMode>();
    private readonly IReadOnlyList<HoldBreathMode> _holdBreathModes = Enum.GetValues<HoldBreathMode>();
    private readonly IReadOnlyList<SprintActivation> _sprintActivationModes = Enum.GetValues<SprintActivation>();
    private readonly IReadOnlyList<AutoRunSendMode> _autoRunSendModes = Enum.GetValues<AutoRunSendMode>();
    private readonly IReadOnlyList<ModifierKeys> _triggerModifiers =
        new[] { ModifierKeys.Control, ModifierKeys.Alt, ModifierKeys.Shift, ModifierKeys.Windows };
    private readonly IDisplayService _displayService;
    private readonly IColorControlService _colorControlService;
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);

    // Autosave is debounced PER ProfileViewModel instance (stable across rename) so editing profile B
    // can never cancel profile A's pending save. _saveSync guards both maps (touched from the UI thread,
    // pool continuations, and the exit flush).
    private readonly object _saveSync = new();
    private readonly Dictionary<ProfileViewModel, CancellationTokenSource> _debounce = [];
    private readonly HashSet<ProfileViewModel> _dirty = [];

    public MainViewModel(IProfileManager profileManager, IDialogService dialogService, IDisplayService displayService, IColorControlService colorControlService)
    {
        _profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _displayService = displayService ?? throw new ArgumentNullException(nameof(displayService));
        _colorControlService = colorControlService ?? throw new ArgumentNullException(nameof(colorControlService));
        _keyOptionsWithNone = KeyCatalog.SortKeys(new[] { Key.None }.Concat(_keyOptions)).ToArray();
        Profiles = new ReadOnlyObservableCollection<ProfileViewModel>(_profiles);

        _profileManager.ProfileAdded += OnProfileAdded;
        _profileManager.ProfileRemoved += OnProfileRemoved;
    }

    public ReadOnlyObservableCollection<ProfileViewModel> Profiles { get; }

    public IReadOnlyList<Key> KeyOptions => _keyOptions;

    public IReadOnlyList<Key> KeyOptionsWithNone => _keyOptionsWithNone;

    public IReadOnlyList<CapsLockMode> CapsLockModes => _capsLockModes;

    public IReadOnlyList<HoldBreathMode> HoldBreathModes => _holdBreathModes;

    public IReadOnlyList<SprintActivation> SprintActivationModes => _sprintActivationModes;

    public IReadOnlyList<AutoRunSendMode> AutoRunSendModes => _autoRunSendModes;

    public IReadOnlyList<ModifierKeys> TriggerModifiers => _triggerModifiers;

    [ObservableProperty]
    private ProfileViewModel? selectedProfile;

    [ObservableProperty]
    private bool isBusy;

    // Source of truth for the XAML gray-out of gated features. Seeded + kept in sync with the
    // service's AdvancedModeEnabled by MainWindow (startup resolve + post-Settings refresh).
    [ObservableProperty]
    private bool advancedModeEnabled;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_profiles.Count > 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _profileManager.InitializeAsync(cancellationToken);
            _profiles.Clear();

            foreach (var profile in _profileManager.Profiles.Select(p => new ProfileViewModel(p, _displayService, _colorControlService, _keyOptions)))
            {
                AttachProfile(profile);
                _profiles.Add(profile);
            }

            SelectedProfile = _profiles.FirstOrDefault();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        string? profileName = null;
        string? executableName = null;

        while (true)
        {
            var result = _dialogService.ShowAddProfileDialog(profileName, executableName);
            if (result is null)
            {
                return;
            }

            profileName = result.ProfileName;
            executableName = result.ExecutableName;

            try
            {
                await _profileManager.AddProfileAsync(result.ProfileName, result.ExecutableName);
                return;
            }
            catch (InvalidOperationException ex)
            {
                _dialogService.ShowError(ex.Message, "Unable to add profile");
                continue;
            }
            catch (Exception ex)
            {
                _dialogService.ShowError(ex.Message, "Unable to add profile");
                return;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveProfile))]
    private async Task RemoveProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        await _profileManager.RemoveProfileAsync(SelectedProfile.Model);
    }

    private bool CanRemoveProfile() => SelectedProfile is { IsWindowsProfile: false, IsColorProfile: false };

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedProfile.CommitChanges();
        await SaveProfileInternalAsync(SelectedProfile);
    }

    private bool CanSaveProfile() => SelectedProfile is not null;

    private bool CanEditRightMouse() => SelectedProfile is { IsWindowsProfile: false, IsColorProfile: false };

    // Combined mappings (global + right-click) commands

    [RelayCommand(CanExecute = nameof(CanEditRightMouse))]
    private void AddCombinedMapping()
    {
        SelectedProfile?.AddCombinedMapping();
    }

    [RelayCommand(CanExecute = nameof(CanEditRightMouse))]
    private void RemoveCombinedMapping(CombinedMappingEntryViewModel? entry)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedProfile.RemoveCombinedMapping(entry);
    }

    [RelayCommand(CanExecute = nameof(CanEditRightMouse))]
    private void RemoveAllCombinedMappings()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedProfile.RemoveAllCombinedMappings();
    }

    [RelayCommand(CanExecute = nameof(CanEditAltMouse))]
    private void AddAltMouseBinding()
    {
        SelectedProfile?.AddAltMouseBinding();
    }

    [RelayCommand(CanExecute = nameof(CanEditAltMouse))]
    private void RemoveAltMouseBinding(AltMouseBindingEntryViewModel? entry)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedProfile.RemoveAltMouseBinding(entry);
    }

    [RelayCommand(CanExecute = nameof(CanEditAltMouse))]
    private void RemoveAllAltMouseBindings()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        // Remove all entries by clearing the collection
        while (SelectedProfile.AltMouseBindings.Count > 0)
        {
            SelectedProfile.RemoveAltMouseBinding(SelectedProfile.AltMouseBindings[0]);
        }
    }

    private bool CanEditAltMouse() => SelectedProfile is { IsWindowsProfile: false, IsColorProfile: false };

    [RelayCommand(CanExecute = nameof(CanModifyProfile))]
    private async Task ModifyProfile()
    {
        var selected = SelectedProfile;
        if (selected is null)
        {
            return;
        }

        var proposedName = selected.Name ?? string.Empty;
        var proposedExecutable = selected.Executable ?? string.Empty;

        while (true)
        {
            var result = _dialogService.ShowEditProfileDialog(proposedName, proposedExecutable);
            if (result is null)
            {
                return;
            }

            proposedName = result.ProfileName ?? string.Empty;
            proposedExecutable = result.ExecutableName ?? string.Empty;

            var newName = proposedName.Trim();
            var newExecutable = proposedExecutable.Trim();

            if (!string.IsNullOrWhiteSpace(newName) &&
                !string.Equals(newName, selected.Name, StringComparison.OrdinalIgnoreCase) &&
                _profiles.Any(p => !ReferenceEquals(p, selected) &&
                                   string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
            {
                _dialogService.ShowError($"A profile named '{newName}' already exists.", "Unable to modify profile");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(newExecutable))
            {
                var candidateNormalized = NormalizeExecutable(newExecutable);
                var currentNormalized = NormalizeExecutable(selected.Executable);
                if (!string.Equals(candidateNormalized, currentNormalized, StringComparison.OrdinalIgnoreCase) &&
                    _profiles.Any(p => !ReferenceEquals(p, selected) &&
                                       string.Equals(p.Model.NormalizedExecutable, candidateNormalized, StringComparison.OrdinalIgnoreCase)))
                {
                    _dialogService.ShowError($"A profile for executable '{newExecutable}' already exists.", "Unable to modify profile");
                    continue;
                }
            }

            // Rename goes through the manager so file identity (SourcePath) is preserved and the same
            // Profile instance is kept (no clobber, no lost selection). Executable edits keep normal autosave.
            var nameChanged = !string.IsNullOrWhiteSpace(newName) &&
                !string.Equals(newName, selected.Name, StringComparison.Ordinal);

            if (nameChanged)
            {
                try
                {
                    await _profileManager.RenameProfileAsync(selected.Model, newName);
                    selected.RefreshNameFromModel();
                }
                catch (Exception ex)
                {
                    _dialogService.ShowError(ex.Message, "Unable to modify profile");
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(newExecutable))
            {
                selected.Executable = newExecutable;
            }

            return;
        }
    }

    private bool CanModifyProfile() => SelectedProfile is { IsWindowsProfile: false, IsColorProfile: false };

    [RelayCommand]
    private void BrowseLauncherTarget(WindowsLauncherEntryViewModel? launcher)
    {
        if (launcher is null)
        {
            return;
        }

        var path = _dialogService.ShowOpenFileDialog("Select Application", "Executable Files (*.exe)|*.exe", launcher.Path);
        if (!string.IsNullOrWhiteSpace(path))
        {
            launcher.Path = path;
        }
    }

    partial void OnSelectedProfileChanged(ProfileViewModel? value)
    {
        RemoveProfileCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
        AddAltMouseBindingCommand.NotifyCanExecuteChanged();
        RemoveAltMouseBindingCommand.NotifyCanExecuteChanged();
        RemoveAllAltMouseBindingsCommand.NotifyCanExecuteChanged();
        ModifyProfileCommand.NotifyCanExecuteChanged();
        AddCombinedMappingCommand.NotifyCanExecuteChanged();
        RemoveCombinedMappingCommand.NotifyCanExecuteChanged();
        RemoveAllCombinedMappingsCommand.NotifyCanExecuteChanged();
    }

    private void OnProfileAdded(object? sender, Profile profile)
    {
        // The manager raises this from a pool continuation when its gate was contended (e.g. a
        // debounced autosave holding it): _profiles is UI-bound, so mutate it only on the dispatcher.
        // A null dispatcher (unit tests) runs inline.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => OnProfileAddedCore(profile));
            return;
        }

        OnProfileAddedCore(profile);
    }

    private void OnProfileAddedCore(Profile profile)
    {
        var viewModel = new ProfileViewModel(profile, _displayService, _colorControlService, _keyOptions);
        AttachProfile(viewModel);
        _profiles.Add(viewModel);
        SelectedProfile = viewModel;
    }

    private void OnProfileRemoved(object? sender, Profile profile)
    {
        // Same dispatcher marshal as OnProfileAdded (see there).
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(() => OnProfileRemovedCore(profile));
            return;
        }

        OnProfileRemovedCore(profile);
    }

    private void OnProfileRemovedCore(Profile profile)
    {
        var existing = _profiles.FirstOrDefault(p => ReferenceEquals(p.Model, profile));
        if (existing is null)
        {
            return;
        }

        DetachProfile(existing);
        _profiles.Remove(existing);

        var windowsProfile = _profiles.FirstOrDefault(p => ReferenceEquals(p.Model, _profileManager.WindowsProfile));
        SelectedProfile = windowsProfile ?? _profiles.FirstOrDefault();
    }

    private void AttachProfile(ProfileViewModel viewModel)
    {
        viewModel.ProfileChanged += OnProfileChanged;
        viewModel.PropertyChanged += OnProfilePropertyChanged;
    }

    private void DetachProfile(ProfileViewModel viewModel)
    {
        viewModel.ProfileChanged -= OnProfileChanged;
        viewModel.PropertyChanged -= OnProfilePropertyChanged;

        // Drop any pending save so an edit-then-remove doesn't later hit the manager's "not managed" throw.
        lock (_saveSync)
        {
            _dirty.Remove(viewModel);
            if (_debounce.Remove(viewModel, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        // Unsubscribe the color VM from the singleton IDisplayService.DisplaysChanged (C7 leak guard).
        viewModel.Dispose();
    }

    private void OnProfileChanged(object? sender, EventArgs e)
    {
        if (sender is ProfileViewModel viewModel)
        {
            QueueAutoSave(viewModel);
        }
    }

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SelectedProfile))
        {
            RemoveAltMouseBindingCommand.NotifyCanExecuteChanged();
            RemoveAllAltMouseBindingsCommand.NotifyCanExecuteChanged();
            RemoveCombinedMappingCommand.NotifyCanExecuteChanged();
            RemoveAllCombinedMappingsCommand.NotifyCanExecuteChanged();
        }
    }

    private void QueueAutoSave(ProfileViewModel viewModel)
    {
        CancellationToken token;
        lock (_saveSync)
        {
            _dirty.Add(viewModel);
            if (_debounce.Remove(viewModel, out var existing))
            {
                existing.Cancel();
                existing.Dispose();
            }

            var cts = new CancellationTokenSource();
            _debounce[viewModel] = cts;
            token = cts.Token;
        }

        _ = DebouncedSaveAsync(viewModel, token);
    }

    private async Task DebouncedSaveAsync(ProfileViewModel viewModel, CancellationToken token)
    {
        try
        {
            await Task.Delay(500, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveIfDirtyAsync(viewModel).ConfigureAwait(false);
    }

    private async Task SaveIfDirtyAsync(ProfileViewModel viewModel)
    {
        lock (_saveSync)
        {
            // Whoever removes it first (the debounce timer or the flush) owns the save; the other no-ops.
            if (!_dirty.Remove(viewModel))
            {
                return;
            }

            if (_debounce.Remove(viewModel, out var cts))
            {
                cts.Dispose();
            }
        }

        await SaveProfileInternalAsync(viewModel).ConfigureAwait(false);
    }

    /// <summary>
    /// Persists every profile with a pending debounced edit immediately (no 500 ms wait).
    /// Call on application exit / session ending so in-flight edits are never lost.
    /// </summary>
    public async Task FlushPendingSavesAsync()
    {
        ProfileViewModel[] pending;
        lock (_saveSync)
        {
            pending = [.. _dirty];
        }

        foreach (var viewModel in pending)
        {
            await SaveIfDirtyAsync(viewModel).ConfigureAwait(false);
        }
    }

    private async Task SaveProfileInternalAsync(ProfileViewModel viewModel)
    {
        await _saveSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            await _profileManager.SaveProfileAsync(viewModel.Model).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(ex.Message, "Unable to save profile");
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    private static string NormalizeExecutable(string? executable) => ExecutableName.Normalize(executable);
}
