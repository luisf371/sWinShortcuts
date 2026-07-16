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

    public void RaiseForegroundChanged(
        string processName,
        uint processId = 0,
        bool requiresInputReset = false)
    {
        if (IsStarted)
        {
            ForegroundChanged?.Invoke(
                this,
                new ForegroundChangedEventArgs(
                    IntPtr.Zero,
                    processName,
                    processId,
                    requiresInputReset));
        }
    }

    public void Dispose()
    {
    }
}
