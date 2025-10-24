using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using sWinShortcuts.Interop;

namespace sWinShortcuts.Services;

public sealed class ForegroundWatcher : IForegroundWatcher
{
    private NativeMethods.WinEventDelegate? _callback;
    private IntPtr _hookHandle = IntPtr.Zero;
    private IntPtr _lastWindow = IntPtr.Zero;
    private bool _disposed;

    public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    public void Start()
    {
        ThrowIfDisposed();

        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        _callback = OnWinEvent;
        _hookHandle = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);

        if (_hookHandle == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Failed to register foreground window hook.");
        }

        // Fire initial event for current foreground window.
        var current = NativeMethods.GetForegroundWindow();
        if (current != IntPtr.Zero)
        {
            _lastWindow = current;
            var processName = ResolveProcessName(current);
            ForegroundChanged?.Invoke(this, new ForegroundChangedEventArgs(current, processName));
        }
    }

    public void Stop()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        _lastWindow = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero || hwnd == _lastWindow)
        {
            return;
        }

        _lastWindow = hwnd;

        string processName = ResolveProcessName(hwnd);

        ForegroundChanged?.Invoke(this, new ForegroundChangedEventArgs(hwnd, processName));
    }

    private static string ResolveProcessName(IntPtr hwnd)
    {
        try
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0)
            {
                return string.Empty;
            }

            using var process = Process.GetProcessById((int)processId);
            var name = process.ProcessName;
            try
            {
                name = process.MainModule?.FileName ?? name;
            }
            catch
            {
                // Access denied or x86/x64 mismatch; fall back to process name.
            }

            return name;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ForegroundWatcher));
        }
    }
}
