using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class FakeForegroundWatcher : IForegroundWatcher
{
    public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    public bool IsStarted { get; private set; }

    public Action? OnStart { get; set; }

    public void Start()
    {
        IsStarted = true;
        OnStart?.Invoke();
    }

    public void Stop()
    {
        IsStarted = false;
    }

    public void RaiseForegroundChanged(
        string processName,
        uint processId = 0,
        bool requiresInputReset = false,
        IntPtr windowHandle = default)
    {
        if (IsStarted)
        {
            ForegroundChanged?.Invoke(
                this,
                new ForegroundChangedEventArgs(
                    windowHandle,
                    processName,
                    processId,
                    requiresInputReset));
        }
    }

    public void Dispose()
    {
    }
}
