using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using sWinShortcuts.Services;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.ViewModels;

public sealed class SettingsViewModel(ILoggerService loggerService, IInputHookService inputHookService) : INotifyPropertyChanged
{
    private readonly ILoggerService _loggerService = loggerService;
    private readonly IInputHookService _inputHookService = inputHookService;
    private readonly IReadOnlyList<Key> _colorToggleKeyOptions =
        KeyCatalog.SortKeys(new[] { Key.None }.Concat(KeyCatalog.GetCommonKeys())).ToArray();
    private bool _startWithWindows;
    private bool _startAsAdmin;
    private bool _startMinimized;
    private bool _enableDebugLogging;
    private Key _colorToggleKey = Key.None;
    private bool _hookWatchdogEnabled = true;
    private bool _advancedModeEnabled;
    private bool _isStartupLoaded;
    private bool _isSaving;

    // Whether the current process is elevated. Captured once per dialog (it cannot change while the dialog
    // is open). When false, the "Start as administrator" option is grayed out because a non-elevated user
    // cannot create or remove the HIGHEST scheduled task that option requires (schtasks returns Access
    // Denied). This is UI awareness only — it does NOT implement the deferred F-004/F-005 security tier.
    public bool IsRunningAsAdmin { get; set; } = Elevation.IsRunningAsAdmin();

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows != value)
            {
                _startWithWindows = value;
                if (!value)
                {
                    StartAsAdmin = false; // keep consistent
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanChooseAdmin));
            }
        }
    }

    public bool StartAsAdmin
    {
        get => _startAsAdmin;
        set
        {
            // A non-admin process cannot manage the HIGHEST scheduled task. Hard-coerce the value off so a
            // stale saved setting (or a programmatic assignment) can never report a phantom enabled state
            // while the checkbox is grayed out and the option is unusable.
            if (!IsRunningAsAdmin)
            {
                value = false;
            }

            if (_startAsAdmin != value)
            {
                _startAsAdmin = value;
                OnPropertyChanged();
            }
        }
    }

    // Persists as [App] StartMinimized. On launch the main window hides to the tray when this is set, so
    // the app starts silently. This is an explicit, STICKY user preference: minimizing-to-tray or restoring
    // during a session does NOT change it (only this toggle does), so "start minimized" stays on until the
    // user turns it off here.
    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            if (_startMinimized != value)
            {
                _startMinimized = value;
                OnPropertyChanged();
            }
        }
    }

    public bool EnableDebugLogging
    {
        get => _enableDebugLogging;
        set
        {
            if (_enableDebugLogging != value)
            {
                _enableDebugLogging = value;
                _loggerService.IsEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    public IReadOnlyList<Key> ColorToggleKeyOptions => _colorToggleKeyOptions;

    /// <summary>
    /// The app-wide key that flips the active profile between its Primary and Secondary color presets.
    /// The hook receives the update immediately; SettingsWindow persists it when the user saves.
    /// </summary>
    public Key ColorToggleKey
    {
        get => _colorToggleKey;
        set
        {
            if (_colorToggleKey == value)
            {
                return;
            }

            _colorToggleKey = value;
            _inputHookService.SetColorToggleKey(value == Key.None ? null : value);
            OnPropertyChanged();
        }
    }

    // Applies live, same pattern as EnableDebugLogging (the service reacts on its next watchdog
    // period); persistence happens on Save in SettingsWindow.
    public bool HookWatchdogEnabled
    {
        get => _hookWatchdogEnabled;
        set
        {
            if (_hookWatchdogEnabled != value)
            {
                _hookWatchdogEnabled = value;
                _inputHookService.HookWatchdogEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    // Applies live to the service (same pattern as HookWatchdogEnabled); persistence happens on Save
    // in SettingsWindow. Turning it off makes the service release any held gated state immediately.
    public bool AdvancedModeEnabled
    {
        get => _advancedModeEnabled;
        set
        {
            if (_advancedModeEnabled != value)
            {
                _advancedModeEnabled = value;
                _inputHookService.AdvancedModeEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    // F-016: the startup checkbox state loads async off the dispatcher (schtasks GetState). Until it loads,
    // the startup controls AND Save are disabled so a premature Save can't apply/delete a startup task from
    // unknown state. While a save runs, the same controls are disabled so their values can't change mid-apply.
    public bool IsStartupLoaded
    {
        get => _isStartupLoaded;
        set
        {
            if (_isStartupLoaded != value)
            {
                _isStartupLoaded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEditStartup));
                OnPropertyChanged(nameof(CanChooseAdmin));
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        set
        {
            if (_isSaving != value)
            {
                _isSaving = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanEditStartup));
                OnPropertyChanged(nameof(CanChooseAdmin));
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }

    public bool CanEditStartup => IsStartupLoaded && !IsSaving;

    public bool CanSave => IsStartupLoaded && !IsSaving;

    public bool CanChooseAdmin => IsStartupLoaded && !IsSaving && StartWithWindows && IsRunningAsAdmin;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

