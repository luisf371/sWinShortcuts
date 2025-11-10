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
    private readonly SettingsViewModel _vm = new();

    // Reuse the same INI storage path pattern used by MainWindow
    private readonly string _settingsPath;

    public SettingsWindow(IStartupService startupService)
    {
        InitializeComponent();
        _startupService = startupService;
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
    }

    private void SaveIni(SettingsViewModel vm)
    {
        try
        {
            var ini = IniDocument.Load(_settingsPath);
            ini.SetValue("App", "StartWithWindows", vm.StartWithWindows ? "true" : "false");
            ini.SetValue("App", "StartAsAdmin", vm.StartAsAdmin ? "true" : "false");
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
