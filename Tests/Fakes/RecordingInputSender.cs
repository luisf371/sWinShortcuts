using System.Collections.Concurrent;
using System.Windows.Input;
using sWinShortcuts.Services;

namespace Tests.Fakes;

internal sealed class RecordingInputSender(
    bool blockDummy = false,
    bool failFirstDown = false,
    bool blockMouse = false,
    bool throwMouse = false,
    bool blockFirstDown = false) : IInputSender
{
    private readonly bool _blockDummy = blockDummy;
    private readonly bool _blockFirstDown = blockFirstDown;
    private int _failNextDown = failFirstDown ? 1 : 0;
    private int _blockedDown;

    public ConcurrentQueue<(Key Key, bool IsDown, int ThreadId)> Transitions { get; } = new();
    public ManualResetEventSlim DummyEntered { get; } = new(false);
    public ManualResetEventSlim ReleaseDummy { get; } = new(false);
    public ConcurrentQueue<int> DummyThreadIds { get; } = new();
    public ConcurrentQueue<int> MouseClickThreadIds { get; } = new();
    public ConcurrentQueue<int> MouseHoldMilliseconds { get; } = new();
    public ManualResetEventSlim MouseEntered { get; } = new(false);
    public ManualResetEventSlim ReleaseMouse { get; } = new(false);
    public ManualResetEventSlim DownEntered { get; } = new(false);
    public ManualResetEventSlim ReleaseDown { get; } = new(false);

    public bool SendKey(Key key, bool isKeyDown)
    {
        Transitions.Enqueue((key, isKeyDown, Environment.CurrentManagedThreadId));
        if (isKeyDown && Interlocked.Exchange(ref _failNextDown, 0) == 1)
        {
            return false;
        }

        if (isKeyDown && _blockFirstDown && Interlocked.CompareExchange(ref _blockedDown, 1, 0) == 0)
        {
            DownEntered.Set();
            ReleaseDown.Wait(TimeSpan.FromSeconds(2));
        }

        return true;
    }

    public bool SendVirtualKeyTap(int virtualKey) => true;

    public bool SendLeftClick(int holdMilliseconds)
    {
        if (throwMouse)
        {
            throw new InvalidOperationException("Synthetic click failure");
        }

        MouseHoldMilliseconds.Enqueue(holdMilliseconds);
        MouseClickThreadIds.Enqueue(Environment.CurrentManagedThreadId);
        MouseEntered.Set();
        return !blockMouse || ReleaseMouse.Wait(TimeSpan.FromSeconds(2));
    }

    public bool SendDummyKey()
    {
        DummyThreadIds.Enqueue(Environment.CurrentManagedThreadId);
        DummyEntered.Set();
        return !_blockDummy || ReleaseDummy.Wait(TimeSpan.FromSeconds(2));
    }
}
