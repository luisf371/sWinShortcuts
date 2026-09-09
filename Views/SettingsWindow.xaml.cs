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
    private readonly ILoggerService _logger;
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
    private Key _baselineRapidFireToggleKey;
    private bool _baselineAdvancedMode;
    private bool _baselineStartWithWindows;
    private bool _baselineStartAsAdmin;
    private bool _baselineVmStartWithWindows;
    private bool _baselineVmStartAsAdmin;
    private bool _applied;
    private bool _closed;

    public SettingsWindow(IStartupService startupService, ILoggerService loggerService, IInputHookService inputHookService)
    {
        InitializeComponent();
        BuildLabel.Text = BuildInfo.Date.Length == 0
            ? $"Build {BuildInfo.Number}"
            : $"Build {BuildInfo.Number} — {BuildInfo.Date}";
        _startupService = startupService;
        _inputHookService = inputHookService;
        _logger = loggerService;
        _vm = new SettingsViewModel(loggerService, inputHookService);
        DataContext = _vm;

        _settingsPath = AppSettings.GetSettingsPath();

        // F-016: the startup checkbox state comes from schtasks (GetState), which can take seconds — load it
        // OFF the dispatcher after the window shows, so opening Settings can't stall the LL-hook thread.
        Loaded += OnLoadedAsync;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedAsync;
        try
        {
            var document = await AppSettings.LoadAsync(_settingsPath);
            if (_closed) return;
            if (_vm.TryLoadIniState(document, out _))
            {
                _baselineColorToggleKey = _vm.ColorToggleKey;
                _baselineRapidFireToggleKey = _vm.RapidFireToggleKey;
                _baselineDebugLogging = _vm.EnableDebugLogging;
                _baselineWatchdog = _vm.HookWatchdogEnabled;
                _baselineAdvancedMode = _vm.AdvancedModeEnabled;
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"[Settings] Failed to load app settings: {ex.Message}");
        }
        if (_closed) return;

        if (!_vm.IsIniLoaded)
        {
            System.Windows.MessageBox.Show(this,
                "Could not read app settings. Settings have not been changed. Close and reopen Settings to try again.",
                "Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

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
            // Capture the POST-COERCION VM values (a non-admin session hard-coerces StartAsAdmin to
            // false): this is exactly what an untouched dialog presents at Save time, so comparing
            // against it lets Save skip the schtasks apply entirely when startup was not edited.
            _baselineVmStartWithWindows = _vm.StartWithWindows;
            _baselineVmStartAsAdmin = _vm.StartAsAdmin;
            _vm.IsStartupLoaded = true; // enables the startup checkboxes + Save now that the OS state is known
        }
        catch (Exception ex)
        {
            // Recorded before the _closed early-return so the diagnostic exists even when the window
            // closed before the await resumed (the MessageBox below is skipped in that case).
            _logger.Log($"[Settings] Failed to read startup state: {ex.Message}");
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

    private static IniDocument CaptureIniState(SettingsViewModel vm)
    {
        var ini = new IniDocument();
        ini.SetValue("App", "StartWithWindows", vm.StartWithWindows ? "true" : "false");
        ini.SetValue("App", "StartAsAdmin", vm.StartAsAdmin ? "true" : "false");
        ini.SetValue("App", "StartMinimized", vm.StartMinimized ? "true" : "false");
        ini.SetValue("App", "EnableDebugLogging", vm.EnableDebugLogging ? "true" : "false");
        ini.SetValue("App", "HookWatchdog", vm.HookWatchdogEnabled ? "true" : "false");
        ini.SetValue("App", "AdvancedMode", vm.AdvancedModeEnabled ? "true" : "false");
        // Always a literal true/false: a null/whitespace value would REMOVE the key (IniDocument
        // SetValue treats it as a delete), resurrecting the absent-key default on reload.
        ini.SetValue("App", "CheckForUpdates", vm.CheckForUpdates ? "true" : "false");
        AppSettings.SetColorToggleKey(ini, vm.ColorToggleKey);
        AppSettings.SetRapidFireToggleKey(ini, vm.RapidFireToggleKey);
        return ini;
    }

    internal static async Task<string?> SaveIniAsync(string settingsPath, IniDocument snapshot,
        IStartupService startupService, bool restoreStartup, bool baselineStartup, bool baselineAdmin)
    {
        try
        {
            await AppSettings.UpdateAsync(settingsPath, document =>
            {
                foreach (var entry in snapshot.GetSection("App"))
                {
                    document.SetValue("App", entry.Key, entry.Value);
                }
            }).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            var error = ex.Message;
            if (restoreStartup)
            {
                try
                {
                    var restoration = await Task.Run(() =>
                    {
                        var restored = startupService.Apply(baselineStartup, baselineAdmin, out var restoreError);
                        return (restored, restoreError);
                    }).ConfigureAwait(false);
                    if (!restoration.restored)
                    {
                        error += $"\n\nStartup restoration failed: {restoration.restoreError ?? "Unable to restore previous startup settings."}";
                    }
                }
                catch (Exception restoreError)
                {
                    error += $"\n\nStartup restoration failed: {restoreError.Message}";
                }
            }
            return error;
        }
    }

    // Startup options are OS state; only (re)apply them when the user actually changed one. An
    // unchanged save must not run schtasks at all — in a non-admin session with an existing elevated
    // HIGHEST task the unconditional apply always fails Access-Denied and shows a misleading "run as
    // administrator" warning for a save that never touched startup. The skip also means an unchanged
    // save no longer re-asserts startup state against external drift (e.g. the task edited in Task
    // Scheduler while the dialog was open); Apply itself keeps the Run key and the scheduled task
    // mutually exclusive whenever a change IS applied.
    internal static bool ShouldApplyStartup(
        bool currentStartWithWindows,
        bool currentStartAsAdmin,
        bool baselineStartWithWindows,
        bool baselineStartAsAdmin) =>
        currentStartWithWindows != baselineStartWithWindows ||
        currentStartAsAdmin != baselineStartAsAdmin;

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanSave)
        {
            return; // Both loads must succeed, and a save must not already be in flight.
        }

        // Both INI settings and the OS startup state are known before anything can be persisted.
        _vm.IsSaving = true; // Keep the captured settings stable throughout startup apply and INI save.
        try
        {
            // Snapshot the startup values on the dispatcher BEFORE going off-thread (codex #2); the controls
            // are disabled for the duration, so they cannot change under us. Run the schtasks apply OFF the
            // dispatcher so a multi-second scheduled-task operation can't stall the LL-hook thread.
            var startWithWindows = _vm.StartWithWindows;
            var startAsAdmin = _vm.StartAsAdmin;
            var snapshot = CaptureIniState(_vm);

            // Run the schtasks apply only when a startup option actually changed from what the dialog
            // loaded (post-coercion baseline); an untouched save skips the multi-second schtasks
            // round-trip entirely. See ShouldApplyStartup for the failure this skip removes.
            var startupApplyRan = ShouldApplyStartup(
                startWithWindows, startAsAdmin, _baselineVmStartWithWindows, _baselineVmStartAsAdmin);
            var applied = true;
            string? applyError = null;
            if (startupApplyRan)
            {
                (applied, applyError) = await Task.Run(() =>
                {
                    var ok = _startupService.Apply(startWithWindows, startAsAdmin, out var err);
                    return (ok, err);
                });
            }

            if (_closed)
            {
                return; // the user cancelled/closed the dialog while the apply was running.
            }

            // The startup apply and the INI save are DECOUPLED. A non-admin run that has a leftover elevated
            // (HIGHEST) startup task cannot remove it (schtasks Access Denied), so the apply fails — but that
            // must NOT block saving the rest of the settings (debug logging, watchdog, advanced mode, color
            // toggle, start-minimized). Surface the startup failure as a non-blocking warning AFTER the INI
            // save, instead of hard-stopping before it. When neither startup option changed the apply is
            // SKIPPED entirely, so this warning can only come from a change the user actually made.
            string? startupWarning = startupApplyRan && !applied
                ? applyError ?? "Unable to apply startup settings."
                : null;

            var saveError = await SaveIniAsync(_settingsPath, snapshot, _startupService,
                startupApplyRan && applied && (startWithWindows != _baselineStartWithWindows || startAsAdmin != _baselineStartAsAdmin),
                _baselineStartWithWindows, _baselineStartAsAdmin);
            if (saveError is not null)
            {
                _logger.Log($"[Settings] Failed to save app settings: {saveError}");

                if (_closed)
                {
                    return;
                }

                System.Windows.MessageBox.Show(this,
                    saveError,
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
        if (!_applied && _vm.IsIniLoaded)
        {
            RollBackLiveSettings();
        }
    }

    private void RollBackLiveSettings()
    {
        // The VM setters ARE the live-apply path, so reassigning the baseline reverts both the VM and the
        // underlying service (each setter no-ops if already equal). Debug logging rolls back through its
        // own non-user path so the rollback is logged with its actual cause, never "via settings".
        _vm.RollBackEnableDebugLogging(_baselineDebugLogging);
        _vm.HookWatchdogEnabled = _baselineWatchdog;
        _vm.AdvancedModeEnabled = _baselineAdvancedMode;
        _vm.ColorToggleKey = _baselineColorToggleKey;
        _vm.RapidFireToggleKey = _baselineRapidFireToggleKey;
    }
}
