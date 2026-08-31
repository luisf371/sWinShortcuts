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
using sWinShortcuts.Configuration;
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
    // Alt+Keyboard trigger keys: the common catalog minus the Alt keys (they are the modifier
    // itself). No None — a trigger row always carries a concrete key.
    private readonly IReadOnlyList<Key> _triggerKeyOptions;
    private readonly IReadOnlyList<InputTrigger> _holdBreathPanicTriggers;
    private readonly IReadOnlyList<CapsLockMode> _capsLockModes =
        [CapsLockMode.Normal, CapsLockMode.DoubleNormal, CapsLockMode.Disabled];
    private readonly IReadOnlyList<HoldBreathMode> _holdBreathModes = Enum.GetValues<HoldBreathMode>();
    private readonly IReadOnlyList<SprintActivation> _sprintActivationModes = Enum.GetValues<SprintActivation>();
    private readonly IReadOnlyList<AutoRunSendMode> _autoRunSendModes = Enum.GetValues<AutoRunSendMode>();
    private readonly IReadOnlyList<AntiAfkSendMode> _antiAfkSendModes = Enum.GetValues<AntiAfkSendMode>();
    private readonly IReadOnlyList<ModifierKeys> _triggerModifiers =
        new[] { ModifierKeys.None, ModifierKeys.Control, ModifierKeys.Alt, ModifierKeys.Shift, ModifierKeys.Windows };
    private readonly IDisplayService _displayService;
    private readonly IColorControlService _colorControlService;
    private readonly IProfileRuntimeService? _profileRuntimeService;
    // Shift held at Remove-invocation time bypasses the delete confirmation dialog (the Remove button
    // tooltip documents this). Seamed as a delegate so tests can pin the modifier state deterministically.
    private readonly Func<bool> _removeBypassModifierDown;
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);

    // Autosave is debounced PER ProfileViewModel instance (stable across rename) so editing profile B
    // can never cancel profile A's pending save. _saveSync guards both maps (touched from the UI thread,
    // pool continuations, and the exit flush).
    private readonly object _saveSync = new();
    private readonly Dictionary<ProfileViewModel, CancellationTokenSource> _debounce = [];
    private readonly HashSet<ProfileViewModel> _dirty = [];

    // F-014: a failed save keeps the profile dirty so the exit flush / next edit still persists it (bounded
    // in-place transient retries live in SaveProfileInternalAsync). _saveErrorShown suppresses repeat error
    // dialogs until the next success. _activeSaves holds at most ONE save-loop per profile (coalescing rapid
    // edits into one active save + one dirty follow-up); the loop runs on the pool — NEVER under _saveSync,
    // since the store's synchronous write under the lock would block the UI/hook thread — and finalizes
    // _dirty before it completes, so the exit flush can await these tasks with no lost/duplicated save.
    // All collections guarded by _saveSync.
    private readonly HashSet<ProfileViewModel> _saveErrorShown = [];
    private readonly Dictionary<ProfileViewModel, Task> _activeSaves = [];
    private readonly Dictionary<ProfileViewModel, long> _editSeq = []; // codex #4b: per-profile edit counter
    private readonly Dictionary<ProfileViewModel, Profile> _persistenceSnapshots = [];
    private bool _isFlushing; // guarded by _saveSync: exit flush is draining — no new debounce timers arm.
    private const int MaxFlushPasses = 3;

    private enum SaveOutcome { Saved, Failed, Suspended }

    public MainViewModel(
        IProfileManager profileManager,
        IDialogService dialogService,
        IDisplayService displayService,
        IColorControlService colorControlService,
        IProfileRuntimeService? profileRuntimeService = null,
        Func<bool>? removeBypassModifierDown = null)
    {
        _profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _displayService = displayService ?? throw new ArgumentNullException(nameof(displayService));
        _colorControlService = colorControlService ?? throw new ArgumentNullException(nameof(colorControlService));
        _profileRuntimeService = profileRuntimeService;
        _removeBypassModifierDown = removeBypassModifierDown ?? DefaultRemoveBypassModifierDown;
        _keyOptionsWithNone = KeyCatalog.SortKeys(new[] { Key.None }.Concat(_keyOptions)).ToArray();
        _triggerKeyOptions = _keyOptions.Where(k => k != Key.LeftAlt && k != Key.RightAlt).ToArray();
        _holdBreathPanicTriggers = BuildHoldBreathPanicTriggers();
        Profiles = new ReadOnlyObservableCollection<ProfileViewModel>(_profiles);

        _profileManager.ProfileAdded += OnProfileAdded;
        _profileManager.ProfileRemoved += OnProfileRemoved;
    }

    public ReadOnlyObservableCollection<ProfileViewModel> Profiles { get; }

    public IReadOnlyList<Key> KeyOptions => _keyOptions;

    public IReadOnlyList<Key> KeyOptionsWithNone => _keyOptionsWithNone;

    public IReadOnlyList<Key> TriggerKeyOptions => _triggerKeyOptions;
    public IReadOnlyList<InputTrigger> HoldBreathPanicTriggers => _holdBreathPanicTriggers;

    public IReadOnlyList<CapsLockMode> CapsLockModes => _capsLockModes;

    public IReadOnlyList<HoldBreathMode> HoldBreathModes => _holdBreathModes;

    public IReadOnlyList<SprintActivation> SprintActivationModes => _sprintActivationModes;

    public IReadOnlyList<AutoRunSendMode> AutoRunSendModes => _autoRunSendModes;

    public IReadOnlyList<AntiAfkSendMode> AntiAfkSendModes => _antiAfkSendModes;

    public IReadOnlyList<ModifierKeys> TriggerModifiers => _triggerModifiers;

    private static IReadOnlyList<InputTrigger> BuildHoldBreathPanicTriggers()
    {
        var triggers = new List<InputTrigger> { InputTrigger.None };
        triggers.AddRange(KeyCatalog.GetCommonKeys().Select(InputTrigger.FromKey));
        triggers.Add(InputTrigger.FromMouseButton(sWinShortcuts.Models.MouseButton.Middle));
        triggers.Add(InputTrigger.FromMouseButton(sWinShortcuts.Models.MouseButton.XButton1));
        triggers.Add(InputTrigger.FromMouseButton(sWinShortcuts.Models.MouseButton.XButton2));
        return triggers;
    }

    [ObservableProperty]
    private ProfileViewModel? selectedProfile;

    [ObservableProperty]
    private bool isBusy;

    // Source of truth for the XAML gray-out of gated features. Seeded + kept in sync with the
    // service's AdvancedModeEnabled by MainWindow (startup resolve + post-Settings refresh).
    [ObservableProperty]
    private bool advancedModeEnabled;

    // Profile-editor tab strip (MainWindow.xaml). Positional indices — must match the TabItem
    // order there. Launcher is built-in-only and FIRST (the built-in default opens on it:
    // Launcher, Display, System); Keys/Advanced are custom-profile only; Display/System are
    // always visible. Not persisted: in-session retention is free and a saved index adds write
    // churn for no value (it is coerced per profile kind anyway).
    public const int TabIndexLauncher = 0;
    public const int TabIndexKeys = 1;
    public const int TabIndexAdvanced = 2;
    public const int TabIndexDisplay = 3;
    public const int TabIndexSystem = 4;

    [ObservableProperty]
    private int selectedTabIndex;

    [ObservableProperty]
    private Key colorToggleKey = Key.None;

    [ObservableProperty]
    private Key rapidFireToggleKey = Key.None;

    // ---- Update banner (session state; pushed only by UpdateCheckService via the dispatcher) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateBanner))]
    private bool updateAvailable;

    // Session-only dismissal; never persisted. Deliberately NOT reset by NotifyUpdateAvailable, so
    // a later check finding another build does not re-show a dismissed banner this session.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateBanner))]
    private bool updateBannerDismissed;

    [ObservableProperty]
    private string updateBannerText = string.Empty;

    public string UpdateDownloadUrl => GitHubUrls.LatestReleasePageUrl;

    public bool ShowUpdateBanner => UpdateAvailable && !UpdateBannerDismissed;

    /// <summary>Called (dispatcher-only) by UpdateCheckService when a newer build exists.</summary>
    public void NotifyUpdateAvailable(int latestBuild, int? currentBuild)
    {
        UpdateBannerText = currentBuild is > 0
            ? $"Update available — Build {latestBuild} (you have Build {currentBuild})"
            : $"Update available — Build {latestBuild}";
        UpdateAvailable = true;
    }

    [RelayCommand]
    private void DismissUpdateBanner() => UpdateBannerDismissed = true;

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

            foreach (var profile in _profileManager.Profiles.Select(p => new ProfileViewModel(p, _displayService, _colorControlService, _keyOptions, _profileRuntimeService)))
            {
                AttachProfile(profile);
                _profiles.Add(profile);
            }

            SelectedProfile = _profiles.FirstOrDefault();
            CoerceSelectedTabIndex(); // startup may restore LastProfile = Color/Windows below
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

        // Confirmation gate: removal deletes durably from the store first (irreversible), so ask
        // unless Shift was held at invocation. Declining returns before any mutation — selection,
        // dirty state, and autosave are untouched. The modal disables its owner, so SelectedProfile
        // cannot change during the call.
        if (!_removeBypassModifierDown() &&
            !_dialogService.ShowRemoveProfileConfirmation(SelectedProfile.Name))
        {
            return;
        }

        try
        {
            await _profileManager.RemoveProfileAsync(SelectedProfile.Model);
        }
        catch (Exception ex)
        {
            // F-015: a failed durable delete leaves the profile managed + visible; surface why rather
            // than faulting the AsyncRelayCommand task.
            _dialogService.ShowError(ex.Message, "Unable to remove profile");
        }
    }

    private bool CanRemoveProfile() => SelectedProfile is { IsWindowsProfile: false };

    // Read at command-invocation time (the Remove button is mouse/keyboard activated, so the live
    // modifier state is the user's intent for THIS press).
    private static bool DefaultRemoveBypassModifierDown() => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var viewModel = SelectedProfile;
        viewModel.CommitChanges();

        // F-014: route through the SAME coalesced path (mark dirty → ensure the one active loop → await it)
        // so a manual Save is tracked, clears dirty/error state on success, and can't run a duplicate save
        // concurrently with autosave.
        lock (_saveSync)
        {
            _dirty.Add(viewModel);
            _persistenceSnapshots[viewModel] =
                ProfilePersistenceSnapshot.Create(viewModel.Model);
        }
        EnsureSaveStarted(viewModel);

        Task? active;
        lock (_saveSync)
        {
            _activeSaves.TryGetValue(viewModel, out active);
        }

        if (active is not null)
        {
            await active.ConfigureAwait(false);
        }
    }

    private bool CanSaveProfile() => SelectedProfile is { Model.IsPersistenceSuspended: false };

    private bool CanEditCustomProfileFeatures() => SelectedProfile is { IsWindowsProfile: false };

    // Combined mappings (global + right-click) commands

    [RelayCommand(CanExecute = nameof(CanEditCustomProfileFeatures))]
    private void AddCombinedMapping()
    {
        SelectedProfile?.AddCombinedMapping();
    }

    [RelayCommand(CanExecute = nameof(CanEditCustomProfileFeatures))]
    private void RemoveCombinedMapping(CombinedMappingEntryViewModel? entry)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedProfile.RemoveCombinedMapping(entry);
    }

    [RelayCommand(CanExecute = nameof(CanEditCustomProfileFeatures))]
    private void RemoveAllCombinedMappings()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedProfile.RemoveAllCombinedMappings();
    }

    [RelayCommand(CanExecute = nameof(CanEditCustomProfileFeatures))]
    private void AddAltMouseBinding()
    {
        SelectedProfile?.AddAltMouseBinding();
    }

    [RelayCommand(CanExecute = nameof(CanEditCustomProfileFeatures))]
    private void RemoveAltMouseBinding(AltMouseBindingEntryViewModel? entry)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedProfile.RemoveAltMouseBinding(entry);
    }

    [RelayCommand(CanExecute = nameof(CanEditCustomProfileFeatures))]
    private void RemoveAllAltMouseBindings()
    {
        SelectedProfile?.RemoveAllAltMouseBindings();
    }

    [RelayCommand(CanExecute = nameof(CanEditCustomProfileFeatures))]
    private void AddAltKeyboardBinding()
    {
        SelectedProfile?.AddAltKeyboardBinding();
    }

    [RelayCommand(CanExecute = nameof(CanEditCustomProfileFeatures))]
    private void RemoveAltKeyboardBinding(AltKeyboardBindingEntryViewModel? entry)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedProfile.RemoveAltKeyboardBinding(entry);
    }

    [RelayCommand(CanExecute = nameof(CanEditCustomProfileFeatures))]
    private void RemoveAllAltKeyboardBindings()
    {
        SelectedProfile?.RemoveAllAltKeyboardBindings();
    }

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

    private bool CanModifyProfile() => SelectedProfile is { IsWindowsProfile: false };

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

    [RelayCommand]
    private void BrowseCrosshairImage(ProfileViewModel? profile)
    {
        if (profile is null)
        {
            return;
        }

        var path = _dialogService.ShowOpenFileDialog(
            "Select Crosshair Image",
            "Image Files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            profile.CrosshairImagePath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            profile.CrosshairImagePath = path;
        }
    }

    // Two-arg partial (CommunityToolkit 8.2+): cancel the deselected profile's force preview so
    // its temporary color override never outlives the editing session.
    partial void OnSelectedProfileChanged(ProfileViewModel? oldValue, ProfileViewModel? newValue)
    {
        oldValue?.ColorSettings.EndForcePreview();

        CoerceSelectedTabIndex();

        RemoveProfileCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
        AddAltMouseBindingCommand.NotifyCanExecuteChanged();
        RemoveAltMouseBindingCommand.NotifyCanExecuteChanged();
        RemoveAllAltMouseBindingsCommand.NotifyCanExecuteChanged();
        AddAltKeyboardBindingCommand.NotifyCanExecuteChanged();
        RemoveAltKeyboardBindingCommand.NotifyCanExecuteChanged();
        RemoveAllAltKeyboardBindingsCommand.NotifyCanExecuteChanged();
        ModifyProfileCommand.NotifyCanExecuteChanged();
        AddCombinedMappingCommand.NotifyCanExecuteChanged();
        RemoveCombinedMappingCommand.NotifyCanExecuteChanged();
        RemoveAllCombinedMappingsCommand.NotifyCanExecuteChanged();
    }

    // AdvancedModeEnabled no longer affects tab visibility (the Advanced page grays out
    // in place instead), so there is no mode-change coercion hook here anymore.

    // Pure tab-index coercion: WPF keeps rendering a collapsed-but-selected tab, so every
    // profile-kind change must re-land the selection on a visible tab. The Advanced tab is
    // clickable regardless of Advanced Mode (a locked mode grays the page out instead of
    // hiding it), so visibility depends on profile kind only.
    public static int CoerceTabIndex(int requested, bool isWindowsProfile)
    {
        var visibleKeys = !isWindowsProfile;

        return requested switch
        {
            TabIndexLauncher when isWindowsProfile => TabIndexLauncher,
            TabIndexKeys when visibleKeys => TabIndexKeys,
            TabIndexAdvanced when visibleKeys => TabIndexAdvanced,
            TabIndexDisplay => TabIndexDisplay,
            TabIndexSystem => TabIndexSystem,
            _ => isWindowsProfile ? TabIndexSystem : TabIndexKeys
        };
    }

    private void CoerceSelectedTabIndex()
    {
        var profile = SelectedProfile;
        SelectedTabIndex = CoerceTabIndex(
            SelectedTabIndex,
            profile?.IsWindowsProfile ?? false);
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
        var viewModel = new ProfileViewModel(profile, _displayService, _colorControlService, _keyOptions, _profileRuntimeService);
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
        // Mark detached (under _saveSync) so an in-flight save that fails AFTER this delete can't re-add an
        // unmanaged VM to _dirty (codex CRITICAL #3), and clear its retry/error bookkeeping.
        lock (_saveSync)
        {
            viewModel.IsDetached = true;
            _dirty.Remove(viewModel);
            _saveErrorShown.Remove(viewModel);
            _editSeq.Remove(viewModel);
            _persistenceSnapshots.Remove(viewModel);
            if (_debounce.Remove(viewModel, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        // Unsubscribe the color VM from the singleton IDisplayService.DisplaysChanged (C7 leak guard).
        viewModel.Dispose();
    }

    private void OnProfileChanged(object? sender, ProfileChangedEventArgs e)
    {
        if (sender is ProfileViewModel viewModel)
        {
            if (e.Kind != ProfileChangeKind.None)
            {
                _profileRuntimeService?.NotifyProfileChanged(viewModel.Model, e.Kind);
            }
            QueueAutoSave(viewModel);
        }
    }

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, SelectedProfile))
        {
            RemoveAltMouseBindingCommand.NotifyCanExecuteChanged();
            RemoveAllAltMouseBindingsCommand.NotifyCanExecuteChanged();
            RemoveAltKeyboardBindingCommand.NotifyCanExecuteChanged();
            RemoveAllAltKeyboardBindingsCommand.NotifyCanExecuteChanged();
            RemoveCombinedMappingCommand.NotifyCanExecuteChanged();
            RemoveAllCombinedMappingsCommand.NotifyCanExecuteChanged();
        }
    }

    private void QueueAutoSave(ProfileViewModel viewModel)
    {
        CancellationToken token;
        lock (_saveSync)
        {
            // Check detachment + suspension ATOMICALLY under the lock (codex #6): a change callback already
            // in flight when the profile is detached must not re-add a detached VM to _dirty/_debounce that
            // EnsureSaveStarted would then refuse forever. F-008: a suspended built-in is read-only, so a
            // stray edit can never persist — don't accumulate a dirty flag that would be lost anyway.
            if (viewModel.IsDetached || viewModel.Model.IsPersistenceSuspended)
            {
                return;
            }

            _dirty.Add(viewModel);
            _editSeq[viewModel] = _editSeq.GetValueOrDefault(viewModel) + 1; // codex #4b: mark a fresh edit
            // This callback runs on the WPF dispatcher after the model update. Capture a deep snapshot
            // NOW, before the save moves to the pool, so validation and serialization see one coherent
            // edit even if the live model changes again during I/O.
            _persistenceSnapshots[viewModel] =
                ProfilePersistenceSnapshot.Create(viewModel.Model);

            // During the exit flush don't (re)arm a debounce timer (codex #4): the flush drives
            // EnsureSaveStarted directly, and a 500 ms timer firing during/after exit could start an
            // untracked save that removes _dirty after the final count. The edit stays dirty for the flush.
            if (_isFlushing)
            {
                return;
            }

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

        EnsureSaveStarted(viewModel);
    }

    // F-014: start (or coalesce into) the single active save-loop for this profile. Started via Task.Run so
    // the store's SYNCHRONOUS file I/O never runs under _saveSync — holding _saveSync across the write would
    // block the UI/hook thread in QueueAutoSave and can drop the LL hooks (codex CRITICAL).
    private void EnsureSaveStarted(ProfileViewModel viewModel)
    {
        lock (_saveSync)
        {
            if (viewModel.IsDetached || _activeSaves.ContainsKey(viewModel) || !_dirty.Contains(viewModel))
            {
                return;
            }

            _activeSaves[viewModel] = Task.Run(() => RunSaveLoopAsync(viewModel));
        }
    }

    // Persist the profile, then — if a newer edit re-dirtied it while the write was running — loop and
    // persist again, so rapid edits coalesce into one active save plus at most one follow-up. A
    // failed/suspended save leaves the profile dirty (for the exit flush / next edit) and exits the loop.
    // codex-final #1: the loop deregisters from _activeSaves in the SAME _saveSync critical section that
    // makes its final _dirty decision, so EnsureSaveStarted can never observe "active present but this loop
    // already past its last dirty check" — a gap in which a fresh edit would be stranded with no active loop
    // or debounce (data loss on crash / a manual Save returning without persisting).
    private async Task RunSaveLoopAsync(ProfileViewModel viewModel)
    {
        var deregistered = false;
        try
        {
            while (true)
            {
                long savedSeq;
                Profile persistenceSnapshot;
                lock (_saveSync)
                {
                    if (viewModel.IsDetached || !_dirty.Contains(viewModel))
                    {
                        if (viewModel.IsDetached)
                        {
                            _saveErrorShown.Remove(viewModel);
                        }
                        _persistenceSnapshots.Remove(viewModel);

                        // Deregister ATOMICALLY with observing _dirty empty/detached (codex-final #1).
                        _activeSaves.Remove(viewModel);
                        deregistered = true;
                        return;
                    }

                    savedSeq = _editSeq.GetValueOrDefault(viewModel); // codex #4b: detect edits during this save
                    if (!_persistenceSnapshots.TryGetValue(viewModel, out persistenceSnapshot!))
                    {
                        throw new InvalidOperationException(
                            "Dirty profile has no persistence snapshot.");
                    }
                    _dirty.Remove(viewModel);
                }

                SaveOutcome outcome;
                try
                {
                    outcome = await SaveProfileInternalAsync(
                        viewModel,
                        persistenceSnapshot).ConfigureAwait(false);
                }
                catch
                {
                    // Unexpected failure (e.g. the error dialog itself threw during dispatcher shutdown).
                    // Restore the dirty flag so the edit is NOT lost, then exit (codex #5). Deregister under
                    // the SAME lock as the dirty re-add (codex-final #1).
                    lock (_saveSync)
                    {
                        if (!viewModel.IsDetached)
                        {
                            _dirty.Add(viewModel);
                        }

                        _activeSaves.Remove(viewModel);
                        deregistered = true;
                    }
                    return;
                }

                lock (_saveSync)
                {
                    if (outcome == SaveOutcome.Saved)
                    {
                        _saveErrorShown.Remove(viewModel);
                        continue; // a concurrent edit may have re-dirtied it; the top of the loop decides.
                    }

                    // Failed or Suspended: keep the edit dirty (unless the profile was removed mid-save) so
                    // the exit flush / next edit still has it.
                    if (viewModel.IsDetached)
                    {
                        _saveErrorShown.Remove(viewModel);
                        _activeSaves.Remove(viewModel); // codex-final #1: atomic deregister
                        deregistered = true;
                        return;
                    }

                    _dirty.Add(viewModel);

                    // codex #4b: if a NEWER edit landed while this (failed) save ran, loop again to persist it
                    // rather than stranding it until the next edit / exit flush. A static failure (seq
                    // unchanged) exits and leaves it dirty — the in-place transient retries already ran.
                    if (outcome == SaveOutcome.Failed && _editSeq.GetValueOrDefault(viewModel) != savedSeq)
                    {
                        continue;
                    }

                    // Deregister ATOMICALLY with leaving it dirty (codex-final #1): a later edit / the exit
                    // flush / a manual Save then starts a FRESH loop to retry, with no active/dirty overlap.
                    _activeSaves.Remove(viewModel);
                    deregistered = true;
                    return;
                }
            }
        }
        finally
        {
            // Backstop ONLY for an UNEXPECTED throw that escaped the inner locks before we deregistered.
            // Guarded by `deregistered` so we never remove a DIFFERENT loop's registration that
            // EnsureSaveStarted may have created after our atomic in-lock removal above — which would let two
            // concurrent loops run for one profile (codex-final #1). On the normal paths this is a no-op.
            if (!deregistered)
            {
                lock (_saveSync)
                {
                    _activeSaves.Remove(viewModel);
                }
            }
        }
    }

    /// <summary>
    /// Persists every profile with a pending debounced edit immediately (no 500 ms wait) and awaits any
    /// in-flight save to completion. Call on application exit / session ending. Returns the number of
    /// profiles whose edits could NOT be persisted (0 = all saved) so the caller can report them.
    /// </summary>
    public async Task<int> FlushPendingSavesAsync()
    {
        lock (_saveSync)
        {
            // Enter flush mode: QueueAutoSave stops arming debounce timers, and cancel every pending one so
            // a 500 ms timer can't fire during/after exit and start an untracked save that removes _dirty
            // after the final count (codex #4). Any edit still queues into _dirty for the drain below.
            _isFlushing = true;
            foreach (var cts in _debounce.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _debounce.Clear();
        }

        try
        {
            return await DrainSavesAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_saveSync)
            {
                _isFlushing = false; // resume normal debounced autosave (e.g. after a canceled session end).
            }
        }
    }

    private async Task<int> DrainSavesAsync()
    {
        // Drain: ensure a save-loop runs for everything dirty, then await every active loop (each finalizes
        // _dirty before completing, so there is no observe-empty gap). Bounded by MaxFlushPasses so a
        // persistently-failing/suspended save can't block exit indefinitely.
        for (var pass = 0; pass < MaxFlushPasses; pass++)
        {
            ProfileViewModel[] pending;
            lock (_saveSync)
            {
                pending = [.. _dirty];
            }

            foreach (var viewModel in pending)
            {
                EnsureSaveStarted(viewModel);
            }

            Task[] active;
            lock (_saveSync)
            {
                active = [.. _activeSaves.Values];
            }

            if (active.Length == 0)
            {
                lock (_saveSync)
                {
                    if (_dirty.Count == 0)
                    {
                        return 0;
                    }
                }
            }
            else
            {
                try
                {
                    await Task.WhenAll(active).ConfigureAwait(false);
                }
                catch
                {
                    // Individual outcomes (dirty/error state) are finalized inside each RunSaveLoopAsync.
                }
            }
        }

        lock (_saveSync)
        {
            return _dirty.Count; // >0 = edits that could not be persisted before exit
        }
    }

    // F-014: reports the save OUTCOME. Transient I/O (a file lock, antivirus scan) is retried a bounded
    // number of times with short backoff before giving up; a non-transient error (e.g. duplicate-name
    // InvalidOperationException) fails immediately with a one-time dialog; a persistence-suspended
    // built-in (F-008) returns Suspended so the caller keeps the edit without a false success or dialog.
    private async Task<SaveOutcome> SaveProfileInternalAsync(
        ProfileViewModel viewModel,
        Profile persistenceSnapshot)
    {
        const int maxAttempts = 3;
        await _saveSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await _profileManager.SaveProfileSnapshotAsync(
                        viewModel.Model,
                        persistenceSnapshot).ConfigureAwait(false);
                    return SaveOutcome.Saved;
                }
                catch (PersistenceSuspendedException)
                {
                    return SaveOutcome.Suspended;
                }
                catch (Exception ex) when (attempt < maxAttempts && IsTransientSaveFailure(ex))
                {
                    await Task.Delay(120 * attempt).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Show the error only once per failure streak (reset on the next success) so a
                    // deferred-retry loop or the exit flush can't spam identical dialogs.
                    bool show;
                    lock (_saveSync)
                    {
                        show = _saveErrorShown.Add(viewModel);
                    }

                    if (show)
                    {
                        _dialogService.ShowError(ex.Message, "Unable to save profile");
                    }

                    return SaveOutcome.Failed;
                }
            }
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    private static bool IsTransientSaveFailure(Exception ex) =>
        ex is System.IO.IOException || ex is UnauthorizedAccessException;

    private static string NormalizeExecutable(string? executable) => ExecutableName.Normalize(executable);
}
