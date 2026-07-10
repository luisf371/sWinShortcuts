using System;

namespace sWinShortcuts.Services;

public sealed class ForegroundChangedEventArgs(IntPtr windowHandle, string processName, uint processId) : EventArgs
{
    public IntPtr WindowHandle { get; } = windowHandle;

    public string ProcessName { get; } = processName;

    // Owning process id of WindowHandle (0 if unresolved). Lets the input hook cache a foreground
    // identity {hwnd, pid, exe} off-hook so Auto-Run activation can fail-closed with a cheap live
    // HWND/PID compare instead of a Process.GetProcessById on the low-level hook thread (A1).
    public uint ProcessId { get; } = processId;
}

public interface IForegroundWatcher : IDisposable
{
    event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    void Start();

    void Stop();
}
