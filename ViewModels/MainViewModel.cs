using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
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
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);

    public MainViewModel(IProfileManager profileManager, IDialogService dialogService)
    {
        _profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _keyOptionsWithNone = new[] { Key.None }.Concat(_keyOptions).ToArray();
        Profiles = new ReadOnlyObservableCollection<ProfileViewModel>(_profiles);

        _profileManager.ProfileAdded += OnProfileAdded;
        _profileManager.ProfileRemoved += OnProfileRemoved;
    }

    public ReadOnlyObservableCollection<ProfileViewModel> Profiles { get; }

    public IReadOnlyList<Key> KeyOptions => _keyOptions;

    public IReadOnlyList<Key> KeyOptionsWithNone => _keyOptionsWithNone;

    public IReadOnlyList<CapsLockMode> CapsLockModes => _capsLockModes;

    [ObservableProperty]
    private ProfileViewModel? selectedProfile;

    [ObservableProperty]
    private bool isBusy;

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

            foreach (var profile in _profileManager.Profiles.Select(p => new ProfileViewModel(p, _keyOptions)))
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
        var result = _dialogService.ShowAddProfileDialog();
        if (result is null)
        {
            return;
        }

        try
        {
            await _profileManager.AddProfileAsync(result.ProfileName, result.ExecutableName);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(ex.Message, "Unable to add profile");
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

    private bool CanRemoveProfile() => SelectedProfile is { IsWindowsProfile: false };

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

    [RelayCommand(CanExecute = nameof(CanEditRightMouse))]
    private void AddRightMouseOverride()
    {
        SelectedProfile?.AddRightMouseOverride();
    }

    [RelayCommand(CanExecute = nameof(CanEditRightMouse))]
    private void RemoveRightMouseOverride(RightMouseOverrideEntryViewModel? entry)
    {
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedProfile.RemoveRightMouseOverride(entry);
    }

    [RelayCommand(CanExecute = nameof(CanEditRightMouse))]
    private void RemoveAllRightMouseOverrides()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        // Remove all entries by clearing the collection
        while (SelectedProfile.RightMouseOverrides.Count > 0)
        {
            SelectedProfile.RemoveRightMouseOverride(SelectedProfile.RightMouseOverrides[0]);
        }
    }

    private bool CanEditRightMouse() => SelectedProfile is { IsWindowsProfile: false };

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

    private bool CanEditAltMouse() => SelectedProfile is { IsWindowsProfile: false };

    [RelayCommand(CanExecute = nameof(CanModifyProfile))]
    private void ModifyProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var result = _dialogService.ShowEditProfileDialog(SelectedProfile.Name, SelectedProfile.Executable);
        if (result is null)
        {
            return;
        }

        // Update both name and executable
        if (!string.IsNullOrWhiteSpace(result.ProfileName))
        {
            SelectedProfile.Name = result.ProfileName;
        }

        if (!string.IsNullOrWhiteSpace(result.ExecutableName))
        {
            SelectedProfile.Executable = result.ExecutableName;
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

    partial void OnSelectedProfileChanged(ProfileViewModel? value)
    {
        RemoveProfileCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
        AddRightMouseOverrideCommand.NotifyCanExecuteChanged();
        RemoveRightMouseOverrideCommand.NotifyCanExecuteChanged();
        RemoveAllRightMouseOverridesCommand.NotifyCanExecuteChanged();
        AddAltMouseBindingCommand.NotifyCanExecuteChanged();
        RemoveAltMouseBindingCommand.NotifyCanExecuteChanged();
        RemoveAllAltMouseBindingsCommand.NotifyCanExecuteChanged();
        ModifyProfileCommand.NotifyCanExecuteChanged();
    }

    private void OnProfileAdded(object? sender, Profile profile)
    {
        var viewModel = new ProfileViewModel(profile, _keyOptions);
        AttachProfile(viewModel);
        _profiles.Add(viewModel);
        SelectedProfile = viewModel;
    }

    private void OnProfileRemoved(object? sender, Profile profile)
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
            RemoveRightMouseOverrideCommand.NotifyCanExecuteChanged();
            RemoveAllRightMouseOverridesCommand.NotifyCanExecuteChanged();
            RemoveAltMouseBindingCommand.NotifyCanExecuteChanged();
            RemoveAllAltMouseBindingsCommand.NotifyCanExecuteChanged();
        }
    }

    private void QueueAutoSave(ProfileViewModel viewModel)
    {
        _ = SaveProfileInternalAsync(viewModel);
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
}
