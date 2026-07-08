using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using sWinShortcuts.Utilities;
using sWinShortcuts.ViewModels;

namespace sWinShortcuts;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Services.IStartupService _startupService;
    private readonly Services.ILoggerService _logger;
    private readonly Services.IInputHookService _inputHook;
    private readonly string _settingsPath;
    private bool _isLoaded;
    private bool _allowClose;
    private bool _isMinimizingToTray;
    private bool _isInitializingViewModel;
    private bool _startMinimized;
    private string? _startupProfileName;
    private string? _lastProfileName;
    private WindowState _previousWindowState = WindowState.Normal;
    private System.Windows.Threading.DispatcherTimer? _windowStateSaveTimer;
    private const int WM_NCLBUTTONDBLCLK = 0x00A3; // Non-client double-click message

    public MainWindow(MainViewModel viewModel, Services.IStartupService startupService, Services.ILoggerService logger, Services.IInputHookService inputHook)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _startupService = startupService;
        _logger = logger;
        _inputHook = inputHook;
        Loaded += OnLoaded;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // OS shutdown/restart must persist the REAL state, not route through MinimizeToTray (which would
        // flip StartMinimized on). See OnSessionEnding (S5).
        if (System.Windows.Application.Current is not null)
        {
            System.Windows.Application.Current.SessionEnding += OnSessionEnding;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var rootDirectory = Path.Combine(appData, "sWinShortcuts");
        _settingsPath = Path.Combine(rootDirectory, "sWinShortcuts.ini");
        
        // Initialize logger + watchdog state from settings
        try
        {
             var ini = IniDocument.Load(_settingsPath);
             _logger.IsEnabled = ini.GetValue("App", "EnableDebugLogging") == "true";
             // Default-on: only the literal "false" disables (missing key = enabled).
             _inputHook.HookWatchdogEnabled = ini.GetValue("App", "HookWatchdog") != "false";
        }
        catch
        {
             // ignore
        }

        LoadWindowState();
        
        // Add logging for mouse events
        this.MouseLeftButtonDown += (s, e) =>
            _logger.Log($"MouseLeftButtonDown at: X={e.GetPosition(this).X}, Y={e.GetPosition(this).Y}, ClickCount={e.ClickCount}");
            
        // Add logging for window state changes
        this.StateChanged += OnWindowStateChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _isLoaded = true;
        var restoredProfile = false;
        _isInitializingViewModel = true;
        try
        {
            await _viewModel.InitializeAsync();

            if (!string.IsNullOrWhiteSpace(_startupProfileName))
            {
                var match = _viewModel.Profiles.FirstOrDefault(p =>
                    string.Equals(p.Name, _startupProfileName, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                {
                    _viewModel.SelectedProfile = match;
                    restoredProfile = true;
                }
            }

            _lastProfileName = _viewModel.SelectedProfile?.Name;
        }
        finally
        {
            _isInitializingViewModel = false;
        }

        if (!restoredProfile && !string.IsNullOrWhiteSpace(_startupProfileName))
        {
            SaveAppSettings();
        }

        if (_startMinimized)
        {
            MinimizeToTray();
        }
    }

    private void LoadWindowState()
    {
        try
        {
            var ini = IniDocument.Load(_settingsPath);

            _startupProfileName = ini.GetValue("App", "LastProfile")?.Trim();
            _lastProfileName = _startupProfileName;

            if (bool.TryParse(ini.GetValue("Window", "StartMinimized"), out var startMinimized))
            {
                _startMinimized = startMinimized;
            }

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
                // Validate against the ORIGIN-AWARE virtual desktop so a window on a monitor left of /
                // above the primary (negative coords) still restores; otherwise center it.
                var vsLeft = SystemParameters.VirtualScreenLeft;
                var vsTop = SystemParameters.VirtualScreenTop;
                var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
                var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

                if (WindowRestore.IntersectsVirtualDesktop(l, t, Width, Height, vsLeft, vsTop, vsRight, vsBottom))
                {
                    Left = l;
                    Top = t;
                    WindowStartupLocation = WindowStartupLocation.Manual;
                }
                else
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen;
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
        // An immediate save supersedes any pending debounced one.
        _windowStateSaveTimer?.Stop();

        UpdateSettings(ini =>
        {
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
            ApplySharedSettings(ini);
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        MinimizeToTray();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var wnd = new Views.SettingsWindow(_startupService, _logger, _inputHook)
        {
            Owner = this
        };
        wnd.ShowDialog();
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
            QueueWindowStateSave();
    }

    private void Window_LocationChanged(object sender, EventArgs e)
    {
        if (_isLoaded && WindowState == WindowState.Normal)
            QueueWindowStateSave();
    }

    // Dragging/resizing fires SizeChanged/LocationChanged once per tick; a synchronous full-file
    // INI load+rewrite each tick is a UI-thread write storm. Trailing debounce instead — the
    // explicit saves on Closing/ExitFromTray/SessionEnding remain immediate and cancel the timer.
    private void QueueWindowStateSave()
    {
        if (_windowStateSaveTimer is null)
        {
            _windowStateSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _windowStateSaveTimer.Tick += (_, _) =>
            {
                _windowStateSaveTimer!.Stop();
                SaveWindowState();
            };
        }

        _windowStateSaveTimer.Stop();
        _windowStateSaveTimer.Start();
    }

    protected override void OnMouseDoubleClick(System.Windows.Input.MouseButtonEventArgs e)
    {
        // Check if the double-click is within the title bar area
        var position = e.GetPosition(this);
        _logger.Log($"Double-click detected at position: X={position.X}, Y={position.Y}");
        
        if (position.Y <= 32) // Title bar height is 32
        {
            _logger.Log("Double-click within title bar area - preventing maximize");
            // Prevent double-click from maximizing the window
            e.Handled = true;
            return;
        }
        
        _logger.Log("Double-click outside title bar area - allowing default behavior");
        // Allow default behavior for clicks outside title bar
        base.OnMouseDoubleClick(e);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var position = e.GetPosition(this);
        _logger.Log($"TitleBar_MouseLeftButtonDown at: X={position.X}, Y={position.Y}, ClickCount={e.ClickCount}");
        
        if (e.ClickCount == 2)
        {
            _logger.Log("Double-click detected on title bar - preventing maximize");
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
            _logger.Log("Intercepted WM_NCLBUTTONDBLCLK - preventing maximize");
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
            _startMinimized = true;
            SaveAppSettings();

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
        _logger.Log($"Window state changed to: {WindowState}");

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

        _startMinimized = false;
        SaveAppSettings();

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

    private void OnSessionEnding(object? sender, System.Windows.SessionEndingCancelEventArgs e)
    {
        // Persist the user's real preference (StartMinimized as-is) and allow the forced close to proceed
        // WITHOUT the tray path. Guarded by _allowClose so it can't double-run with the normal close path.
        if (_allowClose)
        {
            return;
        }

        _allowClose = true;
        SaveWindowState();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(MainViewModel.SelectedProfile), StringComparison.Ordinal))
        {
            return;
        }

        if (_isInitializingViewModel)
        {
            return;
        }

        _lastProfileName = _viewModel.SelectedProfile?.Name;
        SaveAppSettings();
    }

    private void SaveAppSettings()
    {
        if (!_isLoaded)
        {
            return;
        }

        UpdateSettings(ApplySharedSettings);
    }

    private void UpdateSettings(Action<IniDocument> updater)
    {
        try
        {
            var document = IniDocument.Load(_settingsPath);
            updater(document);
            document.Save(_settingsPath);
        }
        catch
        {
            // Ignore errors updating settings
        }
    }

    private void ApplySharedSettings(IniDocument ini)
    {
        // Prefer the live selected-profile name so a rename (same instance, no selection change) is not
        // persisted stale (§7/M2); fall back to the cached name when nothing is selected.
        var currentName = _viewModel.SelectedProfile?.Name ?? _lastProfileName;
        ini.SetValue("App", "LastProfile", string.IsNullOrWhiteSpace(currentName) ? null : currentName);
        ini.SetValue("Window", "StartMinimized", _startMinimized ? "true" : "false");
    }
}
