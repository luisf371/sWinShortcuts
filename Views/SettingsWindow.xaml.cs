using System;
using System.IO;
using System.Windows;
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

    public SettingsWindow(IStartupService startupService, ILoggerService loggerService, IInputHookService inputHookService)
    {
        InitializeComponent();
        _startupService = startupService;
        _inputHookService = inputHookService;
        _vm = new SettingsViewModel(loggerService, inputHookService);
        DataContext = _vm;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var rootDirectory = Path.Combine(appData, "sWinShortcuts");
        _settingsPath = Path.Combine(rootDirectory, "sWinShortcuts.ini");

        LoadState();
    }

    private void LoadState()
    {
        // Prefer actual system state; fall back to INI if needed
        var state = _startupService.GetState();
        _vm.StartWithWindows = state.StartWithWindows;
        _vm.StartAsAdmin = state.StartAsAdmin;
        
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
        }
        catch
        {
            _vm.EnableDebugLogging = false;
            _vm.HookWatchdogEnabled = true;
            // Fall back to the live service value (never a blind false) so a read failure can't
            // silently disable an upgrade-enabled gate the service already applied.
            _vm.AdvancedModeEnabled = _inputHookService.AdvancedModeEnabled;
        }
    }

    private void SaveIni(SettingsViewModel vm)
    {
        try
        {
            var ini = IniDocument.Load(_settingsPath);
            ini.SetValue("App", "StartWithWindows", vm.StartWithWindows ? "true" : "false");
            ini.SetValue("App", "StartAsAdmin", vm.StartAsAdmin ? "true" : "false");
            ini.SetValue("App", "EnableDebugLogging", vm.EnableDebugLogging ? "true" : "false");
            ini.SetValue("App", "HookWatchdog", vm.HookWatchdogEnabled ? "true" : "false");
            ini.SetValue("App", "AdvancedMode", vm.AdvancedModeEnabled ? "true" : "false");
            ini.Save(_settingsPath);
        }
        catch
        {
            // ignore persistence failures
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (!_startupService.Apply(_vm.StartWithWindows, _vm.StartAsAdmin, out var error))
        {
            System.Windows.MessageBox.Show(this,
                error ?? "Unable to apply startup settings.",
                "Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        SaveIni(_vm);
        DialogResult = true;
        Close();
    }
}
