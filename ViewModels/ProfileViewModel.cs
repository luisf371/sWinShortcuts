using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.ViewModels;

public sealed class ProfileViewModel : ViewModelBase
{
    private readonly IReadOnlyList<Key> _keyOptions;
    private bool _isSyncing;

    public ProfileViewModel(Profile model, IDisplayService displayService, IColorControlService colorControlService, IReadOnlyList<Key>? keyOptions = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        ArgumentNullException.ThrowIfNull(displayService);
        ArgumentNullException.ThrowIfNull(colorControlService);
        _keyOptions = keyOptions ?? KeyCatalog.GetCommonKeys();

        AltMouse = new AltMouseViewModel(Model.AltMouse);
        AltMouse.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(AvailableMouseButtons));
            OnProfileChanged();
        };
        AltMouse.Bindings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(AvailableMouseButtons));

        CombinedMappings = new ObservableCollection<CombinedMappingEntryViewModel>(
            Model.CombinedMappings.Mappings.Select(e => new CombinedMappingEntryViewModel
            {
                SourceKey = e.SourceKey,
                TargetKey = e.TargetKey,
                SuppressOriginalKey = e.SuppressOriginalKey,
                RightClickOnly = e.RightClickOnly
            }));
        CombinedMappings.CollectionChanged += OnCombinedMappingsChanged;
        foreach (var m in CombinedMappings)
        {
            AttachCombinedVm(m);
        }

        WindowsLaunchers = new ObservableCollection<WindowsLauncherEntryViewModel>(
            Model.WindowsLauncher.Launchers.OrderBy(pair => pair.Key).Select(pair => new WindowsLauncherEntryViewModel(pair.Key, pair.Value)));
        WindowsLaunchers.CollectionChanged += OnWindowsLaunchersChanged;
        foreach (var launcher in WindowsLaunchers)
        {
            AttachLauncherEntry(launcher);
        }

        ColorSettings = new ColorSettingsViewModel(Model.ColorSettings, displayService, colorControlService, Model.IsColorProfile);
        ColorSettings.Changed += (_, _) => OnProfileChanged();

        _name = Model.Name;
        _isEnabled = Model.IsEnabled;
        _executable = Model.Executable;
    }

    public event EventHandler? ProfileChanged;

    public Profile Model { get; }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            var normalized = (value ?? string.Empty).Trim();
            if (SetProperty(ref _name, normalized))
            {
                Model.Name = normalized;
                OnPropertyChanged();
                OnProfileChanged();
            }
        }
    }

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

    public bool IsColorProfile => Model.IsColorProfile;

    public AltMouseViewModel AltMouse { get; }

    public ObservableCollection<AltMouseBindingEntryViewModel> AltMouseBindings => AltMouse.Bindings;

    public IReadOnlyList<Models.MouseButton> AvailableMouseButtons
    {
        get
        {
            var allButtons = new[] { Models.MouseButton.Left, Models.MouseButton.Right, Models.MouseButton.Middle, Models.MouseButton.XButton1, Models.MouseButton.XButton2 };
            var usedButtons = AltMouse.Bindings.Select(b => b.Button).ToHashSet();
            return allButtons.Where(b => !usedButtons.Contains(b)).ToList();
        }
    }

    public ObservableCollection<CombinedMappingEntryViewModel> CombinedMappings { get; }

    private CombinedMappingEntryViewModel? _selectedCombinedMapping;
    public CombinedMappingEntryViewModel? SelectedCombinedMapping
    {
        get => _selectedCombinedMapping;
        set => SetProperty(ref _selectedCombinedMapping, value);
    }

    public ObservableCollection<WindowsLauncherEntryViewModel> WindowsLaunchers { get; }

    public IReadOnlyList<Key> AvailableCombinedSourceKeys
    {
        get
        {
            var used = CombinedMappings.Select(e => e.SourceKey).ToHashSet();
            return _keyOptions.Where(k => !used.Contains(k)).ToList();
        }
    }

    public bool WindowsLauncherEnabled
    {
        get => Model.WindowsLauncher.IsEnabled;
        set
        {
            if (Model.WindowsLauncher.IsEnabled != value)
            {
                Model.WindowsLauncher.IsEnabled = value;
                OnPropertyChanged();
                OnProfileChanged();
            }
        }
    }

    public bool CombinedKeyMappingsEnabled
    {
        get => Model.CombinedMappings.IsEnabled;
        set
        {
            if (Model.CombinedMappings.IsEnabled != value)
            {
                Model.CombinedMappings.IsEnabled = value;
                OnPropertyChanged();
                OnProfileChanged();
            }
        }
    }

    private WindowsLauncherEntryViewModel? _selectedLauncher;
    public WindowsLauncherEntryViewModel? SelectedLauncher
    {
        get => _selectedLauncher;
        set => SetProperty(ref _selectedLauncher, value);
    }

    public bool RightClickHoldBreathEnabled
    {
        get => Model.RightClickHoldBreath.IsEnabled;
        set
        {
            if (Model.RightClickHoldBreath.IsEnabled != value)
            {
                Model.RightClickHoldBreath.IsEnabled = value;
                OnPropertyChanged();
                OnProfileChanged();
            }
        }
    }

    public Key RightClickHoldBreathKey
    {
        get => Model.RightClickHoldBreath.HoldBreathKey;
        set
        {
            if (Model.RightClickHoldBreath.HoldBreathKey != value)
            {
                Model.RightClickHoldBreath.HoldBreathKey = value;
                OnPropertyChanged();
                OnProfileChanged();
            }
        }
    }

    public HoldBreathMode RightClickHoldBreathMode
    {
        get => Model.RightClickHoldBreath.Mode;
        set
        {
            if (Model.RightClickHoldBreath.Mode != value)
            {
                Model.RightClickHoldBreath.Mode = value;
                OnPropertyChanged();
                OnProfileChanged();
            }
        }
    }

    public int RightClickHoldBreathDelay
    {
        get => Model.RightClickHoldBreath.DelayMilliseconds;
        set
        {
            if (Model.RightClickHoldBreath.DelayMilliseconds != value)
            {
                Model.RightClickHoldBreath.DelayMilliseconds = value;
                OnPropertyChanged();
                OnProfileChanged();
            }
        }
    }

    public bool CapsLockEnabled
    {
        get => Model.CapsLock.IsEnabled;
        set
        {
            if (Model.CapsLock.IsEnabled != value)
            {
                Model.CapsLock.IsEnabled = value;
                OnPropertyChanged();
                OnProfileChanged();
            }
        }
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
            var newValue = value == Key.None ? null : (Key?)value;
            if (Model.CapsLock.RemapTarget != newValue)
            {
                Model.CapsLock.RemapTarget = newValue;
                OnPropertyChanged();
                OnProfileChanged();
            }
        }
    }

    public IReadOnlyList<Key> KeyOptions => _keyOptions;

    public ColorSettingsViewModel ColorSettings { get; }

    public void AddCombinedMapping()
    {
        var available = AvailableCombinedSourceKeys;
        var defaultSource = available.FirstOrDefault();
        if (defaultSource == default)
        {
            defaultSource = _keyOptions.FirstOrDefault(k => k != Key.None);
            if (defaultSource == default)
            {
                defaultSource = Key.A;
            }
        }

        var combined = new CombinedMappingEntryViewModel
        {
            SourceKey = defaultSource,
            RightClickOnly = false
        };

        CombinedMappings.Add(combined);
        SelectedCombinedMapping = combined;
        OnPropertyChanged(nameof(AvailableCombinedSourceKeys));
        OnProfileChanged();
    }

    public void RemoveCombinedMapping(CombinedMappingEntryViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (CombinedMappings.Remove(entry))
        {
            if (ReferenceEquals(SelectedCombinedMapping, entry))
            {
                SelectedCombinedMapping = CombinedMappings.LastOrDefault();
            }
            OnPropertyChanged(nameof(AvailableCombinedSourceKeys));
            OnProfileChanged();
        }
    }

    public void RemoveAllCombinedMappings()
    {
        if (CombinedMappings.Count == 0) return;
        CombinedMappings.Clear();
        OnPropertyChanged(nameof(AvailableCombinedSourceKeys));
        OnProfileChanged();
    }

    public void AddAltMouseBinding()
    {
        var availableButtons = AvailableMouseButtons;
        if (availableButtons.Count == 0)
        {
            return;
        }

        var entry = new AltMouseBindingEntryViewModel(availableButtons[0], null, null);

        entry.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(AltMouseBindingEntryViewModel.Button))
            {
                OnPropertyChanged(nameof(AvailableMouseButtons));
            }
        };

        AltMouse.Bindings.Add(entry);
        OnPropertyChanged(nameof(AvailableMouseButtons));
    }

    public void RemoveAltMouseBinding(AltMouseBindingEntryViewModel? entry)
    {
        if (entry is null)
        {
            return;
        }

        if (AltMouse.Bindings.Remove(entry))
        {
            OnPropertyChanged(nameof(AvailableMouseButtons));
        }
    }

    public void CommitChanges()
    {
        OnProfileChanged();
    }

    private void OnCombinedMappingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (CombinedMappingEntryViewModel m in e.NewItems)
            {
                AttachCombinedVm(m);
            }
        }
        if (e.OldItems is not null)
        {
            foreach (CombinedMappingEntryViewModel m in e.OldItems)
            {
                DetachCombinedVm(m);
            }
        }
        OnPropertyChanged(nameof(AvailableCombinedSourceKeys));
        OnProfileChanged();
    }

    private void AttachCombinedVm(CombinedMappingEntryViewModel m)
    {
        m.PropertyChanged += OnCombinedVmChanged;
    }

    private void DetachCombinedVm(CombinedMappingEntryViewModel m)
    {
        m.PropertyChanged -= OnCombinedVmChanged;
    }

    private void OnCombinedVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CombinedMappingEntryViewModel.SourceKey))
        {
            OnPropertyChanged(nameof(AvailableCombinedSourceKeys));
        }
        // Persist and notify engine immediately on any mapping change
        OnProfileChanged();
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
            Model.RightClickHoldBreath.IsEnabled = RightClickHoldBreathEnabled;
            Model.RightClickHoldBreath.HoldBreathKey = RightClickHoldBreathKey;
            Model.RightClickHoldBreath.Mode = RightClickHoldBreathMode;
            Model.RightClickHoldBreath.DelayMilliseconds = RightClickHoldBreathDelay;
            Model.CapsLock.IsEnabled = CapsLockEnabled;
            Model.CapsLock.Mode = CapsLockMode;
            Model.CapsLock.RemapTarget = CapsLockRemapKey;
            Model.WindowsLauncher.IsEnabled = WindowsLauncherEnabled;
            Model.Executable = Executable;
            Model.ColorSettings.SelectedDisplayId = ColorSettings.SelectedDisplayId;
            Model.ColorSettings.IsEnabled = ColorSettings.IsEnabled;

            Model.CombinedMappings.IsEnabled = CombinedKeyMappingsEnabled;
            Model.CombinedMappings.Mappings.Clear();
            foreach (var vm in CombinedMappings)
            {
                Model.CombinedMappings.Mappings.Add(new CombinedMappingEntry
                {
                    SourceKey = vm.SourceKey,
                    TargetKey = vm.TargetKey,
                    SuppressOriginalKey = vm.SuppressOriginalKey,
                    RightClickOnly = vm.RightClickOnly
                });
            }
        }
        finally
        {
            _isSyncing = false;
        }

        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }
}
