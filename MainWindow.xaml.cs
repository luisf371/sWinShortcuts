using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using sWinShortcuts.Utilities;
using sWinShortcuts.ViewModels;

namespace sWinShortcuts;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly string _settingsPath;
    private bool _isLoaded;
    private bool _allowClose;
    private bool _isMinimizingToTray;
    private WindowState _previousWindowState = WindowState.Normal;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3; // Non-client double-click message

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        Loaded += OnLoaded;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var rootDirectory = Path.Combine(appData, "sWinShortcuts");
        _settingsPath = Path.Combine(rootDirectory, "sWinShortcuts.ini");
        
        LoadWindowState();
        
        // Add logging for mouse events
        this.MouseLeftButtonDown += (s, e) =>
            System.Diagnostics.Debug.WriteLine($"MouseLeftButtonDown at: X={e.GetPosition(this).X}, Y={e.GetPosition(this).Y}, ClickCount={e.ClickCount}");
            
        // Add logging for window state changes
        this.StateChanged += OnWindowStateChanged;
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
            {
                WindowState = ws == WindowState.Minimized ? WindowState.Normal : ws;
                _previousWindowState = WindowState;
            }
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

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        MinimizeToTray();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }

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

    protected override void OnMouseDoubleClick(System.Windows.Input.MouseButtonEventArgs e)
    {
        // Check if the double-click is within the title bar area
        var position = e.GetPosition(this);
        System.Diagnostics.Debug.WriteLine($"Double-click detected at position: X={position.X}, Y={position.Y}");
        
        if (position.Y <= 32) // Title bar height is 32
        {
            System.Diagnostics.Debug.WriteLine("Double-click within title bar area - preventing maximize");
            // Prevent double-click from maximizing the window
            e.Handled = true;
            return;
        }
        
        System.Diagnostics.Debug.WriteLine("Double-click outside title bar area - allowing default behavior");
        // Allow default behavior for clicks outside title bar
        base.OnMouseDoubleClick(e);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var position = e.GetPosition(this);
        System.Diagnostics.Debug.WriteLine($"TitleBar_MouseLeftButtonDown at: X={position.X}, Y={position.Y}, ClickCount={e.ClickCount}");
        
        if (e.ClickCount == 2)
        {
            System.Diagnostics.Debug.WriteLine("Double-click detected on title bar - preventing maximize");
            e.Handled = true;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        
        // Get the window handle
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        // Subscribe to window messages
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Intercept non-client double-click messages (title bar double-clicks)
        if (msg == WM_NCLBUTTONDBLCLK)
        {
            System.Diagnostics.Debug.WriteLine("Intercepted WM_NCLBUTTONDBLCLK - preventing maximize");
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void MinimizeToTray()
    {
        if (_isMinimizingToTray)
        {
            return;
        }

        _isMinimizingToTray = true;
        try
        {
            if (WindowState != WindowState.Minimized)
            {
                _previousWindowState = WindowState;
            }

            ShowInTaskbar = false;
            if (WindowState != WindowState.Minimized)
            {
                WindowState = WindowState.Minimized;
            }

            Hide();
        }
        finally
        {
            _isMinimizingToTray = false;
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"Window state changed to: {WindowState}");

        if (WindowState == WindowState.Minimized && !_isMinimizingToTray)
        {
            MinimizeToTray();
        }
        else if (WindowState != WindowState.Minimized)
        {
            _previousWindowState = WindowState;
        }
    }

    public void RestoreFromTray()
    {
        ShowInTaskbar = true;
        if (!IsVisible)
        {
            Show();
        }

        var targetState = _previousWindowState == WindowState.Minimized
            ? WindowState.Normal
            : _previousWindowState;

        WindowState = targetState;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }));
    }

    public void ExitFromTray()
    {
        SaveWindowState();
        _allowClose = true;
        Close();
    }
}
