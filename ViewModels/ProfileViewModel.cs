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
        AltMouse.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(AvailableMouseButtons));
            OnProfileChanged();
        };
        AltMouse.Bindings.CollectionChanged += (_, _) => OnPropertyChanged(nameof(AvailableMouseButtons));

        RightMouseOverrides = new ObservableCollection<RightMouseOverrideEntryViewModel>(
            Model.RightMouseOverrides.Overrides.Select(entry => new RightMouseOverrideEntryViewModel(entry)));
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

    public ObservableCollection<RightMouseOverrideEntryViewModel> RightMouseOverrides { get; }

    public ObservableCollection<WindowsLauncherEntryViewModel> WindowsLaunchers { get; }

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

    public void AddAltMouseBinding()
    {
        var availableButtons = AvailableMouseButtons;
        if (availableButtons.Count == 0)
        {
            return;
        }

        var entry = new AltMouseBindingEntryViewModel(availableButtons[0], null, null);
        
        // Attach change handler before adding to collection
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
            Model.RightClickHoldBreath.IsEnabled = RightClickHoldBreathEnabled;
            Model.RightClickHoldBreath.HoldBreathKey = RightClickHoldBreathKey;
            Model.RightClickHoldBreath.Mode = RightClickHoldBreathMode;
            Model.RightClickHoldBreath.DelayMilliseconds = RightClickHoldBreathDelay;
            Model.CapsLock.IsEnabled = CapsLockEnabled;
            Model.CapsLock.Mode = CapsLockMode;
            Model.CapsLock.RemapTarget = CapsLockRemapKey;
            Model.WindowsLauncher.IsEnabled = WindowsLauncherEnabled;
            Model.Executable = Executable;

            if (!IsWindowsProfile)
            {
                Model.RightMouseOverrides.Overrides.Clear();
                foreach (var vm in RightMouseOverrides)
                {
                    var model = new RightMouseOverrideEntry();
                    vm.UpdateModel(model);
                    Model.RightMouseOverrides.Overrides.Add(model);
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
