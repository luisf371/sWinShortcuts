using System;
using System.IO;
using System.Windows;
using sWinShortcuts.Utilities;
using sWinShortcuts.ViewModels;

namespace sWinShortcuts;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly string _settingsPath;
    private bool _isLoaded;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += OnLoaded;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var rootDirectory = Path.Combine(appData, "sWinShortcuts");
        _settingsPath = Path.Combine(rootDirectory, "sWinShortcuts.ini");
        
        LoadWindowState();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _isLoaded = true;
        await _viewModel.InitializeAsync();
    }

    private void LoadWindowState()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return;

            var ini = IniDocument.Load(_settingsPath);
            
            var width = ini.GetValue("Window", "Width");
            var height = ini.GetValue("Window", "Height");
            var left = ini.GetValue("Window", "Left");
            var top = ini.GetValue("Window", "Top");
            var state = ini.GetValue("Window", "State");

            if (double.TryParse(width, out var w) && w >= MinWidth && w <= 3840)
                Width = w;
            
            if (double.TryParse(height, out var h) && h >= MinHeight && h <= 2160)
                Height = h;
            
            if (double.TryParse(left, out var l) && double.TryParse(top, out var t))
            {
                // Validate position is on screen
                if (l >= 0 && t >= 0 && l < SystemParameters.VirtualScreenWidth && t < SystemParameters.VirtualScreenHeight)
                {
                    Left = l;
                    Top = t;
                    WindowStartupLocation = WindowStartupLocation.Manual;
                }
            }

            if (Enum.TryParse<WindowState>(state, out var ws))
                WindowState = ws;
        }
        catch
        {
            // Ignore errors loading window state
        }
    }

    private void SaveWindowState()
    {
        try
        {
            var ini = new IniDocument();
            
            // Save normal bounds even when maximized
            double width, height, left, top;
            if (WindowState == WindowState.Normal)
            {
                width = Width;
                height = Height;
                left = Left;
                top = Top;
            }
            else
            {
                width = RestoreBounds.Width;
                height = RestoreBounds.Height;
                left = RestoreBounds.Left;
                top = RestoreBounds.Top;
            }

            ini.SetValue("Window", "Width", width.ToString("F0"));
            ini.SetValue("Window", "Height", height.ToString("F0"));
            ini.SetValue("Window", "Left", left.ToString("F0"));
            ini.SetValue("Window", "Top", top.ToString("F0"));
            ini.SetValue("Window", "State", WindowState.ToString());

            ini.Save(_settingsPath);
        }
        catch
        {
            // Ignore errors saving window state
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveWindowState();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isLoaded && WindowState == WindowState.Normal)
            SaveWindowState();
    }

    private void Window_LocationChanged(object sender, EventArgs e)
    {
        if (_isLoaded && WindowState == WindowState.Normal)
            SaveWindowState();
    }
}
