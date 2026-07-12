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

public sealed class ProfileViewModel : ViewModelBase, IDisposable
{
    private readonly IReadOnlyList<Key> _keyOptions;
    private bool _isSyncing;

    // F-014/F-015: set by MainViewModel (under its _saveSync) when this VM is detached because its profile
    // was removed. An in-flight autosave checks this before requeueing so a removed profile is never
    // re-added to the dirty set (which would otherwise hit the manager's "not managed" throw at exit).
    public bool IsDetached { get; set; }

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
            OnProfileChanged(ProfileChangeKind.AltMouse);
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

        ColorSettings = new ColorSettingsViewModel(
            Model.ColorSettings,
            displayService,
            colorControlService,
            Model.IsColorProfile,
            parentEnabledCheck: Model.IsColorProfile ? () => IsEnabled : null);

        ColorSettings.Changed += (_, _) => OnProfileChanged(ProfileChangeKind.Color);

        _name = Model.Name;
        _isEnabled = Model.IsEnabled;
        _executable = Model.Executable;

        UpdateSelectableKeys();
    }

    public event EventHandler<ProfileChangedEventArgs>? ProfileChanged;

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
                OnProfileChanged(ProfileChangeKind.None);
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
                OnProfileChanged(ProfileChangeKind.Identity);
            }
        }
    }

    private bool _isEnabled;

    // F-008: the editor content is bound to this. A persistence-suspended built-in (its source was
    // unreadable at load) is read-only, so editing is disabled and the grayed content is the persistent
    // read-only indicator. For every normal profile this equals IsEnabled (unchanged behavior).
    public bool CanEditContent => IsEnabled && !Model.IsPersistenceSuspended;

    // F-008: a persistence-suspended built-in is fully read-only — even its enable/master checkbox is
    // disabled, so toggling it can't change live model/color state that autosave would then silently drop.
    // Static (suspension is set once at load).
    public bool CanToggleEnabled => !Model.IsPersistenceSuspended;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                Model.IsEnabled = value;
                OnPropertyChanged(nameof(CanEditContent));
                OnProfileChanged(ProfileChangeKind.Master);

                if (IsColorProfile)
                {
                    // For the dedicated Color Profile, this sidebar toggle acts as the Master Switch.
                    // We must notify the color settings to re-evaluate (revert to defaults if disabled).
                    ColorSettings.RefreshMasterEnabledState();
                }
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
                OnProfileChanged(ProfileChangeKind.WindowsLauncher);
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
                OnProfileChanged(ProfileChangeKind.CombinedMappings);
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
                OnProfileChanged(ProfileChangeKind.HoldBreath);
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
                OnProfileChanged(ProfileChangeKind.HoldBreath);
            }
        }
    }

    public InputTrigger RightClickHoldBreathPanicTrigger
    {
        get => Model.RightClickHoldBreath.PanicTrigger;
        set
        {
            if (Model.RightClickHoldBreath.PanicTrigger != value)
            {
                Model.RightClickHoldBreath.PanicTrigger = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RightClickHoldBreathEarlyCancelConfigured));
                OnProfileChanged(ProfileChangeKind.HoldBreath);
            }
        }
    }

    public bool RightClickHoldBreathEarlyCancelConfigured =>
        Model.RightClickHoldBreath.PanicTrigger.Kind != InputTriggerKind.None;

    public bool RightClickHoldBreathSuppressEarlyCancelInput
    {
        get => Model.RightClickHoldBreath.SuppressEarlyCancelInput;
        set
        {
            if (Model.RightClickHoldBreath.SuppressEarlyCancelInput != value)
            {
                Model.RightClickHoldBreath.SuppressEarlyCancelInput = value;
                OnPropertyChanged();
                OnProfileChanged(ProfileChangeKind.HoldBreath);
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
                OnProfileChanged(ProfileChangeKind.HoldBreath);
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
                OnProfileChanged(ProfileChangeKind.HoldBreath);
            }
        }
    }

    public bool AutoRunEnabled
    {
        get => Model.AutoRun.IsEnabled;
        set
        {
            if (Model.AutoRun.IsEnabled != value)
            {
                Model.AutoRun.IsEnabled = value;
                OnPropertyChanged();
                OnProfileChanged(ProfileChangeKind.AutoRun);
            }
        }
    }

    public ModifierKeys AutoRunTriggerModifier
    {
        get => Model.AutoRun.TriggerModifier;
        set
        {
            if (Model.AutoRun.TriggerModifier != value)
            {
                Model.AutoRun.TriggerModifier = value;
                OnPropertyChanged();
                OnProfileChanged(ProfileChangeKind.AutoRun);
            }
        }
    }

    public Key AutoRunTriggerKey
    {
        get => Model.AutoRun.TriggerKey;
        set
        {
            if (Model.AutoRun.TriggerKey != value)
            {
                Model.AutoRun.TriggerKey = value;
                OnPropertyChanged();
                OnProfileChanged(ProfileChangeKind.AutoRun);
            }
        }
    }

    public bool AutoRunSprintEnabled
    {
        get => Model.AutoRun.SprintEnabled;
        set
        {
            if (Model.AutoRun.SprintEnabled != value)
            {
                Model.AutoRun.SprintEnabled = value;
                OnPropertyChanged();
                OnProfileChanged(ProfileChangeKind.AutoRun);
            }
        }
    }

    public Key AutoRunSprintKey
    {
        get => Model.AutoRun.SprintKey;
        set
        {
            if (Model.AutoRun.SprintKey != value)
            {
                Model.AutoRun.SprintKey = value;
                OnPropertyChanged();
                OnProfileChanged(ProfileChangeKind.AutoRun);
            }
        }
    }

    public SprintActivation AutoRunSprintMode
    {
        get => Model.AutoRun.SprintMode;
        set
        {
            if (Model.AutoRun.SprintMode != value)
            {
                Model.AutoRun.SprintMode = value;
                OnPropertyChanged();
                OnProfileChanged(ProfileChangeKind.AutoRun);
            }
        }
    }

    public AutoRunSendMode AutoRunSendMode
    {
        get => Model.AutoRun.SendMode;
        set
        {
            if (Model.AutoRun.SendMode != value)
            {
                Model.AutoRun.SendMode = value;
                OnPropertyChanged();
                OnProfileChanged(ProfileChangeKind.AutoRun);
            }
        }
    }

    public bool AntiAfkEnabled
    {
        get => Model.AntiAfk.IsEnabled;
        set
        {
            if (Model.AntiAfk.IsEnabled != value)
            {
                Model.AntiAfk.IsEnabled = value;
                OnPropertyChanged();
                OnProfileChanged(ProfileChangeKind.AntiAfk);
            }
        }
    }

    public int AntiAfkIntervalMinutes
    {
        get => Model.AntiAfk.IntervalMinutes;
        set
        {
            var clamped = Math.Clamp(value, 1, 15);
            if (Model.AntiAfk.IntervalMinutes != clamped)
            {
                Model.AntiAfk.IntervalMinutes = clamped;
                OnPropertyChanged();
                OnProfileChanged(ProfileChangeKind.AntiAfk);
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
                OnProfileChanged(ProfileChangeKind.CapsLock);
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
                OnProfileChanged(ProfileChangeKind.CapsLock);
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
                OnProfileChanged(ProfileChangeKind.CapsLock);
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
        // OnCombinedMappingsChanged will fire and update keys
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
            // OnCombinedMappingsChanged will fire and update keys
        }
    }

    public void RemoveAllCombinedMappings()
    {
        if (CombinedMappings.Count == 0) return;
        CombinedMappings.Clear();
        // OnCombinedMappingsChanged will fire and update keys
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
        // Child/property edits notify their exact runtime category when they occur. Manual Save only
        // forces persistence; treating it as AllRuntime would needlessly cancel live gestures/runs.
        OnProfileChanged(ProfileChangeKind.None);
    }

    /// <summary>
    /// Syncs the displayed name from the model after an external rename (via the manager) WITHOUT
    /// re-triggering the Name setter's autosave — the manager already persisted the rename.
    /// </summary>
    public void RefreshNameFromModel()
    {
        SetProperty(ref _name, Model.Name, nameof(Name));
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
        UpdateSelectableKeys();
        OnPropertyChanged(nameof(AvailableCombinedSourceKeys));
        OnProfileChanged(ProfileChangeKind.CombinedMappings);
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
            UpdateSelectableKeys();
            OnPropertyChanged(nameof(AvailableCombinedSourceKeys));
        }
        // Persist and notify engine immediately on any mapping change
        OnProfileChanged(ProfileChangeKind.CombinedMappings);
    }

    private void UpdateSelectableKeys()
    {
        var allUsed = CombinedMappings.Select(m => m.SourceKey).ToHashSet();
        
        foreach (var vm in CombinedMappings)
        {
            // For each row, allowed keys are:
            // 1. Keys not used by anyone
            // 2. OR the key used by THIS row (so it can keep its own selection)
            var allowed = _keyOptions
                .Where(k => !allUsed.Contains(k) || k == vm.SourceKey);
            
            vm.SelectableSourceKeys = KeyCatalog.SortKeys(allowed).ToList();
        }
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

        OnProfileChanged(ProfileChangeKind.WindowsLauncher);
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
        OnProfileChanged(ProfileChangeKind.WindowsLauncher);
    }

    public void Dispose()
    {
        ColorSettings.Dispose();
    }

    private void OnProfileChanged(ProfileChangeKind changeKind)
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
            Model.RightClickHoldBreath.PanicTrigger = RightClickHoldBreathPanicTrigger;
            Model.RightClickHoldBreath.SuppressEarlyCancelInput = RightClickHoldBreathSuppressEarlyCancelInput;
            Model.RightClickHoldBreath.Mode = RightClickHoldBreathMode;
            Model.RightClickHoldBreath.DelayMilliseconds = RightClickHoldBreathDelay;
            Model.AutoRun.IsEnabled = AutoRunEnabled;
            Model.AutoRun.TriggerModifier = AutoRunTriggerModifier;
            Model.AutoRun.TriggerKey = AutoRunTriggerKey;
            Model.AutoRun.SprintEnabled = AutoRunSprintEnabled;
            Model.AutoRun.SprintKey = AutoRunSprintKey;
            Model.AutoRun.SprintMode = AutoRunSprintMode;
            Model.AutoRun.SendMode = AutoRunSendMode;
            Model.AntiAfk.IsEnabled = AntiAfkEnabled;
            Model.AntiAfk.IntervalMinutes = AntiAfkIntervalMinutes;
            Model.CapsLock.IsEnabled = CapsLockEnabled;
            Model.CapsLock.Mode = CapsLockMode;
            Model.CapsLock.RemapTarget = CapsLockRemapKey;
            Model.WindowsLauncher.IsEnabled = WindowsLauncherEnabled;
            Model.Executable = Executable;
            Model.ColorSettings.IsEnabled = ColorSettings.IsEnabled;

            Model.CombinedMappings.IsEnabled = CombinedKeyMappingsEnabled;

            // Build-and-swap, never Clear+Add in place: the pool-thread autosave serializer and the
            // hook thread enumerate this list concurrently with UI edits.
            var mappings = new List<CombinedMappingEntry>(CombinedMappings.Count);
            foreach (var vm in CombinedMappings)
            {
                mappings.Add(new CombinedMappingEntry
                {
                    SourceKey = vm.SourceKey,
                    TargetKey = vm.TargetKey,
                    SuppressOriginalKey = vm.SuppressOriginalKey,
                    RightClickOnly = vm.RightClickOnly
                });
            }

            Model.CombinedMappings.Mappings = mappings;
        }
        finally
        {
            _isSyncing = false;
        }

        ProfileChanged?.Invoke(this, new ProfileChangedEventArgs(changeKind));
    }
}
