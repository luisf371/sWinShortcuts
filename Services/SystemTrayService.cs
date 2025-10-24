using System;
using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;

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
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "sWinShortcuts"
        };

        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowMainWindow());
        menu.Items.Add("Exit", null, (_, _) => _application.Dispatcher.Invoke(() => _application.Shutdown()));
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
                _notifyIcon.Icon = SystemIcons.Application;
                _customIcon?.Dispose();
                _customIcon = null;
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
            _notifyIcon.Icon = SystemIcons.Application;
            _customIcon?.Dispose();
            _customIcon = null;
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

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SystemTrayService));
        }
    }
}
