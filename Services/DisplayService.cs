using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public sealed class DisplayService : IDisplayService, IDisposable
{
    private readonly object _lock = new();
    private List<DisplayInfo>? _cachedDisplays;
    private bool _disposed;

    public DisplayService()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        lock (_lock)
        {
            if (_cachedDisplays is not null)
            {
                return _cachedDisplays;
            }

            _cachedDisplays = RefreshDisplays();
            return _cachedDisplays;
        }
    }

    private static List<DisplayInfo> RefreshDisplays()
    {
        var list = new List<DisplayInfo>();

        foreach (var screen in Screen.AllScreens)
        {
            var friendlyName = GetFriendlyName(screen.DeviceName);
            
            // Fallback if empty or generic, though EnumDisplayDevices usually provides "Generic PnP Monitor" at worst
            if (string.IsNullOrWhiteSpace(friendlyName))
            {
                friendlyName = screen.DeviceName;
            }
            
            // Add primary indicator for clarity
            if (screen.Primary)
            {
                friendlyName += " (Primary)";
            }

            list.Add(new DisplayInfo
            {
                Id = screen.DeviceName,
                DeviceName = screen.DeviceName,
                Name = friendlyName,
                IsPrimary = screen.Primary
            });
        }

        return list;
    }

    private static string GetFriendlyName(string adapterDeviceName)
    {
        try
        {
            var device = new NativeMethods.DISPLAY_DEVICE();
            device.Initialize();

            // We need to query the monitor attached to the adapter.
            // Screen.DeviceName gives us the adapter name (e.g. \\.\DISPLAY1).
            // Calling EnumDisplayDevices with that name and iDevNum=0 usually gives the first monitor.
            if (NativeMethods.EnumDisplayDevices(adapterDeviceName, 0, ref device, 0))
            {
                if (!string.IsNullOrWhiteSpace(device.DeviceString))
                {
                    return device.DeviceString;
                }
            }
        }
        catch
        {
            // Fail safely to return fallback
        }

        return string.Empty;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _cachedDisplays = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _disposed = true;
    }
}