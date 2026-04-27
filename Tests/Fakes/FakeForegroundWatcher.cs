using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class FakeForegroundWatcher : IForegroundWatcher
{
    public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    public bool IsStarted { get; private set; }

    public void Start()
    {
        IsStarted = true;
    }

    public void Stop()
    {
        IsStarted = false;
    }

    public void RaiseForegroundChanged(string processName)
    {
        if (IsStarted)
        {
            ForegroundChanged?.Invoke(this, new ForegroundChangedEventArgs(IntPtr.Zero, processName));
        }
    }

    public void Dispose()
    {
    }
}
