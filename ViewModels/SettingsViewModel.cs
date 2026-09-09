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
    private IReadOnlyList<Key> _colorToggleKeyOptions =
        KeyCatalog.SortKeys(new[] { Key.None }.Concat(KeyCatalog.GetCommonKeys()
            .Where(key => KeyInteropUtilities.NormalizeAppToggleKey(key).HasValue))).ToArray();
    private bool _startWithWindows;
    private bool _startAsAdmin;
    private bool _startMinimized;
    private bool _checkForUpdates;
    private bool _enableDebugLogging;
    private Key _colorToggleKey = Key.None;
    private Key _rapidFireToggleKey = Key.None;
    private bool _hookWatchdogEnabled;
    private bool _advancedModeEnabled;
    private bool _isIniLoaded;
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

    // Persisted as [App] CheckForUpdates by SettingsWindow on Save. Default OFF. MainWindow syncs
    // UpdateCheckService after the dialog closes (incl. firing one check on a false→true flip).
    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set
        {
            if (_checkForUpdates != value)
            {
                _checkForUpdates = value;
                OnPropertyChanged();
            }
        }
    }

    public bool EnableDebugLogging
    {
        get => _enableDebugLogging;
        set
        {
            if (_enableDebugLogging == value)
            {
                return;
            }

            _enableDebugLogging = value;
            if (value)
            {
                _loggerService.IsEnabled = true; // enable FIRST so the entry is not dropped
                _loggerService.Log("[Settings] Debug logging enabled via settings");
            }
            else
            {
                _loggerService.Log("[Settings] Debug logging disabled via settings"); // while still enabled
                _loggerService.IsEnabled = false;
            }

            OnPropertyChanged();
        }
    }

    // Applies hydration/cancel state without recording a user toggle.
    internal void SetEnableDebugLoggingProgrammatically(bool value)
    {
        _enableDebugLogging = value;
        _loggerService.IsEnabled = value;
        OnPropertyChanged(nameof(EnableDebugLogging));
    }

    // Logs before disabling or after enabling so the rollback entry survives.
    internal void RollBackEnableDebugLogging(bool baseline)
    {
        if (_enableDebugLogging == baseline)
        {
            return;
        }

        if (baseline)
        {
            SetEnableDebugLoggingProgrammatically(true); // enable first so the entry is recorded
            _loggerService.Log("[Settings] Debug logging re-enabled (settings dialog cancelled)");
        }
        else
        {
            _loggerService.Log("[Settings] Debug logging disabled (settings dialog cancelled)");
            SetEnableDebugLoggingProgrammatically(false);
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
            value = KeyInteropUtilities.NormalizeAppToggleKey(value) ?? Key.None;
            if (_colorToggleKey == value)
            {
                return;
            }

            EnsureToggleKeyOption(value);
            _colorToggleKey = value;
            _inputHookService.SetColorToggleKey(value == Key.None ? null : value);
            OnPropertyChanged();
        }
    }

    public Key RapidFireToggleKey
    {
        get => _rapidFireToggleKey;
        set
        {
            value = KeyInteropUtilities.NormalizeAppToggleKey(value) ?? Key.None;
            if (_rapidFireToggleKey == value)
            {
                return;
            }

            EnsureToggleKeyOption(value);
            _rapidFireToggleKey = value;
            _inputHookService.SetRapidFireToggleKey(value == Key.None ? null : value);
            OnPropertyChanged();
        }
    }

    private void EnsureToggleKeyOption(Key key)
    {
        if (_colorToggleKeyOptions.Contains(key)) return;
        _colorToggleKeyOptions = KeyCatalog.SortKeys(_colorToggleKeyOptions.Append(key)).ToArray();
        OnPropertyChanged(nameof(ColorToggleKeyOptions));
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

    internal bool TryLoadIniState(string settingsPath, out string? error)
        => TryLoadIniState(() => IniDocument.Load(settingsPath), out error);

    internal bool TryLoadIniState(IniDocument document, out string? error)
        => TryLoadIniState(() => document, out error);

    private bool TryLoadIniState(Func<IniDocument> load, out string? error)
    {
        error = null;
        IsIniLoaded = false;
        try
        {
            // Read and parse one snapshot before any setter can change a live service.
            var ini = load();
            var enableDebugLogging = ini.GetValue("App", "EnableDebugLogging") == "true";
            var hookWatchdogEnabled = ini.GetValue("App", "HookWatchdog") != "false";
            // A missing upgrade preference keeps the value MainWindow already resolved.
            var advancedRaw = ini.GetValue("App", "AdvancedMode");
            var advancedModeEnabled = advancedRaw is null
                ? _inputHookService.AdvancedModeEnabled
                : advancedRaw == "true";
            var startMinimized = ini.GetValue("App", "StartMinimized") == "true";
            var checkForUpdates = ini.GetValue("App", "CheckForUpdates") == "true";
            var colorToggleKey = KeyInteropUtilities.NormalizeAppToggleKey(ini.GetKey("App", "ColorToggleKey")) ?? Key.None;
            var rapidFireToggleKey = KeyInteropUtilities.NormalizeAppToggleKey(ini.GetKey("App", "RapidFireToggleKey")) ?? Key.None;

            SetEnableDebugLoggingProgrammatically(enableDebugLogging);
            HookWatchdogEnabled = hookWatchdogEnabled;
            AdvancedModeEnabled = advancedModeEnabled;
            StartMinimized = startMinimized;
            CheckForUpdates = checkForUpdates;
            ColorToggleKey = colorToggleKey;
            RapidFireToggleKey = rapidFireToggleKey;
            // A fresh VM's default values may already match the file while the live services differ.
            _inputHookService.HookWatchdogEnabled = hookWatchdogEnabled;
            _inputHookService.AdvancedModeEnabled = advancedModeEnabled;
            _inputHookService.SetColorToggleKey(colorToggleKey == Key.None ? null : colorToggleKey);
            _inputHookService.SetRapidFireToggleKey(rapidFireToggleKey == Key.None ? null : rapidFireToggleKey);
            IsIniLoaded = true;
            return true;
        }
        catch (Exception ex)
        {
            _loggerService.Log($"[Settings] Failed to load app settings; live settings unchanged: {ex.Message}");
            error = ex.Message;
            return false;
        }
    }

    public bool IsIniLoaded
    {
        get => _isIniLoaded;
        private set
        {
            if (_isIniLoaded != value)
            {
                _isIniLoaded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(CanEditSettings));
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
                OnPropertyChanged(nameof(CanEditSettings));
            }
        }
    }

    public bool CanEditStartup => IsStartupLoaded && !IsSaving;

    public bool CanEditSettings => IsIniLoaded && !IsSaving;

    public bool CanSave => IsIniLoaded && IsStartupLoaded && !IsSaving;

    public bool CanChooseAdmin => IsStartupLoaded && !IsSaving && StartWithWindows && IsRunningAsAdmin;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

