using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using sWinShortcuts.Services;
using sWinShortcuts.Utilities;
using sWinShortcuts.ViewModels;

namespace sWinShortcuts.Views;

public partial class SettingsWindow : Window
{
    private readonly IStartupService _startupService;
    private readonly IInputHookService _inputHookService;
    private readonly SettingsViewModel _vm;

    // Reuse the same INI storage path pattern used by MainWindow
    private readonly string _settingsPath;

    // F-016: the live-apply settings (debug logging, hook watchdog, advanced mode) are pushed to their
    // services by the VM setters as the user toggles them. Capture the state the dialog OPENED with so
    // Cancel / title-bar close / a failed Save can roll the SERVICES back — otherwise Cancel silently
    // leaves e.g. Advanced Mode disabled (which already released gated input state) or the watchdog off.
    private bool _baselineDebugLogging;
    private bool _baselineWatchdog;
    private Key _baselineColorToggleKey;
    private bool _baselineAdvancedMode;
    private bool _baselineStartWithWindows;
    private bool _baselineStartAsAdmin;
    private bool _applied;
    private bool _closed;

    public SettingsWindow(IStartupService startupService, ILoggerService loggerService, IInputHookService inputHookService)
    {
        InitializeComponent();
        _startupService = startupService;
        _inputHookService = inputHookService;
        _vm = new SettingsViewModel(loggerService, inputHookService);
        DataContext = _vm;

        _settingsPath = AppSettings.GetSettingsPath();

        LoadIniState();

        // Baseline = the live-apply state the dialog opened with (from INI / the live services). OnClosing
        // rolls the live services back to this on any non-Save close.
        _baselineColorToggleKey = _vm.ColorToggleKey;
        _baselineDebugLogging = _vm.EnableDebugLogging;
        _baselineWatchdog = _vm.HookWatchdogEnabled;
        _baselineAdvancedMode = _vm.AdvancedModeEnabled;

        // F-016: the startup checkbox state comes from schtasks (GetState), which can take seconds — load it
        // OFF the dispatcher after the window shows, so opening Settings can't stall the LL-hook thread.
        Loaded += OnLoadedAsync;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedAsync;
        try
        {
            var state = await Task.Run(() => _startupService.GetState());
            if (_closed)
            {
                return;
            }

            _vm.StartWithWindows = state.StartWithWindows;
            _vm.StartAsAdmin = state.StartAsAdmin;
            // F-016 (codex #3): remember the OS startup baseline so a Save whose INI persist fails can be
            // reverted, and so the dialog can never leave the OS startup state changed on a non-committed close.
            _baselineStartWithWindows = state.StartWithWindows;
            _baselineStartAsAdmin = state.StartAsAdmin;
            _vm.IsStartupLoaded = true; // enables the startup checkboxes + Save now that the OS state is known
        }
        catch
        {
            if (_closed)
            {
                return;
            }

            // F-016: surface the failure rather than treating the false/false defaults as authoritative.
            // Startup controls + Save stay disabled (IsStartupLoaded == false) so we can't delete/replace a
            // task from unknown state; the user can Cancel and reopen to retry.
            System.Windows.MessageBox.Show(this,
                "Could not read the current startup settings. Startup options are unavailable; close and reopen Settings to try again.",
                "Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void LoadIniState()
    {
        // Only the fast INI-backed live settings load here; the startup (schtasks) state is loaded async in
        // OnLoadedAsync so no scheduled-task query runs on the dispatcher/hook thread.
        try
        {
            var ini = IniDocument.Load(_settingsPath);
            _vm.EnableDebugLogging = ini.GetValue("App", "EnableDebugLogging") == "true";
            // Default-on: only the literal "false" disables (missing key = enabled).
            _vm.HookWatchdogEnabled = ini.GetValue("App", "HookWatchdog") != "false";
            // MainWindow resolves + persists [App] AdvancedMode at startup (incl. the upgrade
            // default). A PRESENT key is authoritative; but if that startup persist silently failed
            // (UpdateSettings swallows save errors), the key can be ABSENT while the service already
            // holds the resolved value — fall back to that live value, never a blind false, so saving
            // settings here can't clobber an upgrade-enabled gate (codex P2 #1).
            var advancedRaw = ini.GetValue("App", "AdvancedMode");
            _vm.AdvancedModeEnabled = advancedRaw is null
                ? _inputHookService.AdvancedModeEnabled
                : advancedRaw == "true";
            // The current [App] key is authoritative; fall back to legacy [Window] until this save migrates it.
            var startMinimizedRaw = ini.GetValue("App", "StartMinimized") ?? ini.GetValue("Window", "StartMinimized");
            _vm.StartMinimized = startMinimizedRaw == "true";
            _vm.ColorToggleKey = AppSettings.LoadColorToggleKey(_settingsPath) ?? Key.None;
        }
        catch
        {
            _vm.EnableDebugLogging = false;
            _vm.HookWatchdogEnabled = true;
            // Fall back to the live service value (never a blind false) so a read failure can't
            // silently disable an upgrade-enabled gate the service already applied.
            _vm.AdvancedModeEnabled = _inputHookService.AdvancedModeEnabled;
            _vm.StartMinimized = false;
            _vm.ColorToggleKey = Key.None;
        }
    }

    private bool SaveIni(SettingsViewModel vm, out string? error)
    {
        error = null;
        try
        {
            var ini = IniDocument.Load(_settingsPath);
            ini.SetValue("App", "StartWithWindows", vm.StartWithWindows ? "true" : "false");
            ini.SetValue("App", "StartAsAdmin", vm.StartAsAdmin ? "true" : "false");
            ini.SetValue("App", "StartMinimized", vm.StartMinimized ? "true" : "false");
            ini.SetValue("App", "EnableDebugLogging", vm.EnableDebugLogging ? "true" : "false");
            ini.SetValue("App", "HookWatchdog", vm.HookWatchdogEnabled ? "true" : "false");
            ini.SetValue("App", "AdvancedMode", vm.AdvancedModeEnabled ? "true" : "false");
            AppSettings.SetColorToggleKey(ini, vm.ColorToggleKey);
            ini.Save(_settingsPath);
            return true;
        }
        catch (Exception ex)
        {
            // F-016: no longer swallowed. The caller keeps the dialog open and shows this, so a
            // read-only/locked INI can't report "saved" and then silently revert on restart.
            error = ex.Message;
            return false;
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_vm.IsSaving)
        {
            return; // guard a double-click while the async apply is in flight.
        }

        // Save is only reachable when IsStartupLoaded (CanSave), so the snapshot below is the real OS state.
        _vm.IsSaving = true; // disables the startup controls + Save while the apply runs (codex #2)
        try
        {
            // Snapshot the startup values on the dispatcher BEFORE going off-thread (codex #2); the controls
            // are disabled for the duration, so they cannot change under us. Run the schtasks apply OFF the
            // dispatcher so a multi-second scheduled-task operation can't stall the LL-hook thread.
            var startWithWindows = _vm.StartWithWindows;
            var startAsAdmin = _vm.StartAsAdmin;

            var (applied, applyError) = await Task.Run(() =>
            {
                var ok = _startupService.Apply(startWithWindows, startAsAdmin, out var err);
                return (ok, err);
            });

            if (_closed)
            {
                return; // the user cancelled/closed the dialog while the apply was running.
            }

            // The startup apply and the INI save are DECOUPLED. A non-admin run that has a leftover elevated
            // (HIGHEST) startup task cannot remove it (schtasks Access Denied), so the apply fails — but that
            // must NOT block saving the rest of the settings (debug logging, watchdog, advanced mode, color
            // toggle, start-minimized). Surface the startup failure as a non-blocking warning AFTER the INI
            // save, instead of hard-stopping before it.
            string? startupWarning = applied ? null : (applyError ?? "Unable to apply startup settings.");

            if (!SaveIni(_vm, out var saveError))
            {
                // F-016 (codex #3): the startup Apply above committed to the OS but the INI didn't persist.
                // Revert the OS startup task to the baseline OFF the dispatcher so the OS and the (unsaved)
                // settings can't disagree — otherwise Cancel would leave startup changed. Only if it changed.
                if (applied && (startWithWindows != _baselineStartWithWindows || startAsAdmin != _baselineStartAsAdmin))
                {
                    await Task.Run(() => _startupService.Apply(_baselineStartWithWindows, _baselineStartAsAdmin, out _));
                }

                if (_closed)
                {
                    return;
                }

                System.Windows.MessageBox.Show(this,
                    saveError ?? "Unable to save settings.",
                    "Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return; // keep the dialog open; do NOT report success — user can retry or Cancel.
            }

            _applied = true; // committed — OnClosing must not roll back the live services.

            // If only the (admin-only) startup change failed, the INI settings still saved successfully.
            // Warn the user that the startup option needs an elevated run, then close normally.
            if (startupWarning is not null)
            {
                System.Windows.MessageBox.Show(this,
                    startupWarning + "\n\nYour other settings were saved. To change startup options, run sWinShortcuts as administrator.",
                    "Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            DialogResult = true;
            Close();
        }
        finally
        {
            _vm.IsSaving = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel)
        {
            return; // close vetoed by another handler — keep the previewed state.
        }

        if (_vm.IsSaving && !_applied)
        {
            // F-016: an async Apply is in flight (and this isn't the completed-Save close). Don't close
            // mid-apply — the background schtasks Apply would otherwise keep mutating startup config after
            // the dialog is gone (codex #3). The window closes once the apply finishes.
            e.Cancel = true;
            return;
        }

        _closed = true; // an in-flight async Save/Load must not touch this window after it closes.

        // F-016: any close that isn't a successful Save (Cancel/IsCancel, Esc, title-bar X, Alt+F4) must
        // undo the live-applied service previews from this dialog session. Startup state needs NO rollback:
        // it is read from the OS via GetState() (never from the write-only INI key), so an applied-but-
        // unsaved startup change stays self-consistent — AND running schtasks on this hook-owning dispatcher
        // thread could stall past LowLevelHooksTimeout and drop the LL hooks (codex CRITICAL: never run
        // scheduled-task work on the UI thread).
        if (!_applied)
        {
            RollBackLiveSettings();
        }
    }

    private void RollBackLiveSettings()
    {
        // The VM setters ARE the live-apply path, so reassigning the baseline reverts both the VM and the
        // underlying service (each setter no-ops if already equal).
        _vm.EnableDebugLogging = _baselineDebugLogging;
        _vm.HookWatchdogEnabled = _baselineWatchdog;
        _vm.AdvancedModeEnabled = _baselineAdvancedMode;
        _vm.ColorToggleKey = _baselineColorToggleKey;
    }
}
