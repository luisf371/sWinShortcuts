using System;

namespace sWinShortcuts.Services;

public sealed class ForegroundChangedEventArgs(IntPtr windowHandle, string processName) : EventArgs
{
    public IntPtr WindowHandle { get; } = windowHandle;

    public string ProcessName { get; } = processName;
}

public interface IForegroundWatcher : IDisposable
{
    event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    void Start();

    void Stop();
}
