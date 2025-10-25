using System;
using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using sWinShortcuts;

namespace sWinShortcuts.Services;

public sealed class SystemTrayService : ISystemTrayService
{
    private readonly System.Windows.Application _application;
    private Forms.NotifyIcon? _notifyIcon;
    private Window? _mainWindow;
    private bool _disposed;
    private Icon? _customIcon;

    public SystemTrayService()
    {
        _application = System.Windows.Application.Current ?? throw new InvalidOperationException("Application must be initialized.");
    }

    public void Initialize(Window mainWindow)
    {
        ThrowIfDisposed();

        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));

        if (_notifyIcon != null)
        {
            return;
        }

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "sWinShortcuts"
        };

        ApplyDefaultIcon();
        _notifyIcon.Visible = true;

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _notifyIcon.ContextMenuStrip = menu;
    }

    public void SetIcon(string iconPath)
    {
        ThrowIfDisposed();

        if (_notifyIcon is null)
        {
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
            {
                ApplyDefaultIcon();
                return;
            }

            var newIcon = new Icon(iconPath);
            _customIcon?.Dispose();
            _customIcon = newIcon;
            _notifyIcon.Icon = _customIcon;
        }
        catch
        {
            // Fallback to default icon on error
            ApplyDefaultIcon();
        }
    }

    public void ShowBalloon(string title, string message)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.ShowBalloonTip(3000, title, message, Forms.ToolTipIcon.Info);
    }

    public void UpdateStatus(bool isProfileActive, string? profileName)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var status = isProfileActive && !string.IsNullOrWhiteSpace(profileName)
            ? $"Active profile: {profileName}"
            : "Idle";

        _notifyIcon.Text = $"sWinShortcuts - {status}";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _customIcon?.Dispose();
        _customIcon = null;
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _application.Dispatcher.Invoke(() =>
        {
            if (_mainWindow is MainWindow mainWindow)
            {
                mainWindow.RestoreFromTray();
                return;
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Show();
            _mainWindow.Activate();
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
            _mainWindow.Focus();
        });
    }

    private void ExitApplication()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _application.Dispatcher.Invoke(() =>
        {
            if (_mainWindow is MainWindow mainWindow)
            {
                mainWindow.ExitFromTray();
                return;
            }

            _mainWindow.Show();
            _mainWindow.Activate();
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
            _mainWindow.Focus();
            _application.Shutdown();
        });
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SystemTrayService));
        }
    }

    private void ApplyDefaultIcon()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var defaultIcon = LoadDefaultIcon();
        if (defaultIcon is null)
        {
            _notifyIcon.Icon = SystemIcons.Application;
            _customIcon?.Dispose();
            _customIcon = null;
            return;
        }

        _customIcon?.Dispose();
        _customIcon = defaultIcon;
        _notifyIcon.Icon = _customIcon;
    }

    private static Icon? LoadDefaultIcon()
    {
        try
        {
            var resourceUri = new Uri("pack://application:,,,/Icons/Icon.ico", UriKind.Absolute);
            var resourceInfo = System.Windows.Application.GetResourceStream(resourceUri);
            if (resourceInfo?.Stream is null)
            {
                return null;
            }

            using var stream = resourceInfo.Stream;
            return new Icon(stream);
        }
        catch
        {
            return null;
        }
    }
}
