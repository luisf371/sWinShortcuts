using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.ViewModels;

public sealed class ProfileViewModel : ViewModelBase
{
    private readonly IReadOnlyList<Key> _keyOptions;
    private bool _isSyncing;

    public ProfileViewModel(Profile model, IReadOnlyList<Key>? keyOptions = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _keyOptions = keyOptions ?? KeyCatalog.GetCommonKeys();

        AltMouse = new AltMouseViewModel(Model.AltMouse);
        AltMouse.Changed += (_, _) => OnProfileChanged();

        RightMouseOverrides = new ObservableCollection<RightMouseOverrideEntryViewModel>(
            Model.RightMouseOverrides.Overrides.Select(pair => new RightMouseOverrideEntryViewModel(pair.Key, pair.Value)));
        RightMouseOverrides.CollectionChanged += OnRightMouseOverridesChanged;
        foreach (var entry in RightMouseOverrides)
        {
            AttachRightMouseEntry(entry);
        }

        WindowsLaunchers = new ObservableCollection<WindowsLauncherEntryViewModel>(
            Model.WindowsLauncher.Launchers.OrderBy(pair => pair.Key).Select(pair => new WindowsLauncherEntryViewModel(pair.Key, pair.Value)));
        WindowsLaunchers.CollectionChanged += OnWindowsLaunchersChanged;
        foreach (var launcher in WindowsLaunchers)
        {
            AttachLauncherEntry(launcher);
        }

        _isEnabled = Model.IsEnabled;
        _executable = Model.Executable;
    }

    public event EventHandler? ProfileChanged;

    public Profile Model { get; }

    public string Name => Model.Name;

    private string _executable = string.Empty;
    public string Executable
    {
        get => _executable;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (!string.Equals(_executable, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _executable = normalized;
                Model.Executable = normalized;
                OnPropertyChanged();
                OnProfileChanged();
            }
        }
    }

    private bool _isEnabled;
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                Model.IsEnabled = value;
                OnProfileChanged();
            }
        }
    }

    public bool IsWindowsProfile => Model.IsWindowsProfile;

    public AltMouseViewModel AltMouse { get; }

    public ObservableCollection<RightMouseOverrideEntryViewModel> RightMouseOverrides { get; }

    public ObservableCollection<WindowsLauncherEntryViewModel> WindowsLaunchers { get; }

    public bool RightMouseOverrideEnabled
    {
        get => Model.RightMouseOverrides.IsEnabled;
        set
        {
            if (Model.RightMouseOverrides.IsEnabled != value)
            {
                Model.RightMouseOverrides.IsEnabled = value;
                OnPropertyChanged();
                OnProfileChanged();
            }
        }
    }

    public bool SuppressOriginalRightMouseKey
    {
        get => Model.RightMouseOverrides.SuppressOriginalKey;
        set
        {
            if (Model.RightMouseOverrides.SuppressOriginalKey != value)
            {
                Model.RightMouseOverrides.SuppressOriginalKey = value;
                OnPropertyChanged();
                OnProfileChanged();
            }
        }
    }

    private RightMouseOverrideEntryViewModel? _selectedRightMouseOverride;
    public RightMouseOverrideEntryViewModel? SelectedRightMouseOverride
    {
        get => _selectedRightMouseOverride;
        set => SetProperty(ref _selectedRightMouseOverride, value);
    }

    private WindowsLauncherEntryViewModel? _selectedLauncher;
    public WindowsLauncherEntryViewModel? SelectedLauncher
    {
        get => _selectedLauncher;
        set => SetProperty(ref _selectedLauncher, value);
    }

    public CapsLockMode CapsLockMode
    {
        get => Model.CapsLock.Mode;
        set
        {
            if (Model.CapsLock.Mode != value)
            {
                Model.CapsLock.Mode = value;
                OnPropertyChanged();
                OnProfileChanged();
            }
        }
    }

    public Key CapsLockRemapKey
    {
        get => Model.CapsLock.RemapTarget ?? Key.None;
        set
        {
            System.Diagnostics.Debug.WriteLine($"[DEBUG] CapsLockRemapKey setter called with value: {value}");
            var newValue = value == Key.None ? null : (Key?)value;
            if (Model.CapsLock.RemapTarget != newValue)
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG] CapsLockRemapKey changing from {(Model.CapsLock.RemapTarget?.ToString() ?? "NULL")} to {(newValue?.ToString() ?? "NULL")}");
                Model.CapsLock.RemapTarget = newValue;
                OnPropertyChanged();
                OnProfileChanged();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[DEBUG] CapsLockRemapKey value unchanged: {value}");
            }
        }
    }

    public IReadOnlyList<Key> KeyOptions => _keyOptions;

    public void AddRightMouseOverride()
    {
        var defaultSource = _keyOptions.FirstOrDefault(k => k != Key.None);
        if (defaultSource == default)
        {
            defaultSource = Key.A;
        }

        var entry = new RightMouseOverrideEntryViewModel();
        RightMouseOverrides.Add(entry);
        SelectedRightMouseOverride = entry;
    }

    public void RemoveRightMouseOverride(RightMouseOverrideEntryViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (RightMouseOverrides.Remove(entry))
        {
            DetachRightMouseEntry(entry);
            if (ReferenceEquals(SelectedRightMouseOverride, entry))
            {
                SelectedRightMouseOverride = RightMouseOverrides.LastOrDefault();
            }

            OnProfileChanged();
        }
    }

    public void CommitChanges()
    {
        OnProfileChanged();
    }

    private void OnRightMouseOverridesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (RightMouseOverrideEntryViewModel item in e.NewItems)
            {
                AttachRightMouseEntry(item);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (RightMouseOverrideEntryViewModel item in e.OldItems)
            {
                DetachRightMouseEntry(item);
            }
        }

        OnProfileChanged();
    }

    private void AttachRightMouseEntry(RightMouseOverrideEntryViewModel entry)
    {
        entry.Changed += OnChildChanged;
    }

    private void DetachRightMouseEntry(RightMouseOverrideEntryViewModel entry)
    {
        entry.Changed -= OnChildChanged;
    }

    private void OnWindowsLaunchersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (WindowsLauncherEntryViewModel item in e.NewItems)
            {
                AttachLauncherEntry(item);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (WindowsLauncherEntryViewModel item in e.OldItems)
            {
                DetachLauncherEntry(item);
            }
        }

        OnProfileChanged();
    }

    private void AttachLauncherEntry(WindowsLauncherEntryViewModel entry)
    {
        entry.Changed += OnChildChanged;
    }

    private void DetachLauncherEntry(WindowsLauncherEntryViewModel entry)
    {
        entry.Changed -= OnChildChanged;
    }

    private void OnChildChanged(object? sender, EventArgs e)
    {
        OnProfileChanged();
    }

    private void OnProfileChanged()
    {
        if (_isSyncing)
        {
            return;
        }

        _isSyncing = true;
        try
        {
            Model.IsEnabled = IsEnabled;
            Model.RightMouseOverrides.IsEnabled = RightMouseOverrideEnabled;
            Model.RightMouseOverrides.SuppressOriginalKey = SuppressOriginalRightMouseKey;
            Model.CapsLock.Mode = CapsLockMode;
            Model.CapsLock.RemapTarget = CapsLockRemapKey;
            Model.Executable = Executable;

            if (!IsWindowsProfile)
            {
                Model.RightMouseOverrides.Overrides.Clear();
                foreach (var entry in RightMouseOverrides)
                {
                    Model.RightMouseOverrides.Overrides[entry.SourceKey] = entry.TargetKey;
                }
            }
        }
        finally
        {
            _isSyncing = false;
        }

        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }
}
