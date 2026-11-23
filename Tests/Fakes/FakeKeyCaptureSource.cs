using sWinShortcuts.Services;
using sWinShortcuts.Utilities;

namespace Tests.Fakes;

/// <summary>Stands in for the keyboard hook: records the capture sink and lets tests drive its key edges.</summary>
public sealed class FakeKeyCaptureSource : IKeyCaptureSource
{
    public bool Allow { get; set; } = true;
    public IKeyCaptureSink? Sink { get; private set; }
    public int BeginCount { get; private set; }
    public int EndCount { get; private set; }

    public bool TryBeginKeyCapture(IKeyCaptureSink sink)
    {
        BeginCount++;
        if (!Allow) return false;
        Sink = sink;
        return true;
    }

    public void EndKeyCapture(IKeyCaptureSink sink)
    {
        EndCount++;
        if (ReferenceEquals(Sink, sink)) Sink = null;
    }

    public KeyCaptureStatus Press(int vk) => RequireSink().OnKey(vk, isDown: true);

    public KeyCaptureStatus Release(int vk) => RequireSink().OnKey(vk, isDown: false);

    private IKeyCaptureSink RequireSink() =>
        Sink ?? throw new InvalidOperationException("No key capture is running.");
}
