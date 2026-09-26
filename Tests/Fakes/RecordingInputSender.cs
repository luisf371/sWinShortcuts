using System.Collections.Concurrent;
using System.Windows.Input;
using sWinShortcuts.Services;
using MouseButton = sWinShortcuts.Models.MouseButton;

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
    public ConcurrentQueue<(Key Key, bool MacroRelease)> KeyReleases { get; } = new();
    public ConcurrentQueue<(MouseButton Button, bool IsDown, bool MacroRelease)> MouseTransitions { get; } = new();
    public ConcurrentQueue<(int X, int Y)> MouseMoves { get; } = new();
    public ConcurrentQueue<(int Delta, bool Horizontal)> MouseWheels { get; } = new();
    public Func<Key, bool, bool, bool>? KeyResult { get; set; }
    public Func<MouseButton, bool, bool, bool>? MouseResult { get; set; }
    public ManualResetEventSlim DummyEntered { get; } = new(false);
    public ManualResetEventSlim ReleaseDummy { get; } = new(false);
    public ConcurrentQueue<int> DummyThreadIds { get; } = new();
    public ConcurrentQueue<int> MouseClickThreadIds { get; } = new();
    public ConcurrentQueue<int> MouseHoldMilliseconds { get; } = new();
    public ManualResetEventSlim MouseEntered { get; } = new(false);
    public ManualResetEventSlim ReleaseMouse { get; } = new(false);
    public ManualResetEventSlim DownEntered { get; } = new(false);
    public ManualResetEventSlim ReleaseDown { get; } = new(false);

    public bool SendKey(Key key, bool isKeyDown, bool macroRelease = false, Func<bool>? canSend = null)
    {
        if (isKeyDown && canSend?.Invoke() == false) return false;
        Transitions.Enqueue((key, isKeyDown, Environment.CurrentManagedThreadId));
        if (!isKeyDown) KeyReleases.Enqueue((key, macroRelease));
        if (isKeyDown && Interlocked.Exchange(ref _failNextDown, 0) == 1)
        {
            return false;
        }

        if (isKeyDown && _blockFirstDown && Interlocked.CompareExchange(ref _blockedDown, 1, 0) == 0)
        {
            DownEntered.Set();
            ReleaseDown.Wait(TimeSpan.FromSeconds(2));
        }

        return KeyResult?.Invoke(key, isKeyDown, macroRelease) ?? true;
    }

    public bool SendMouseButton(MouseButton button, bool isDown, bool macroRelease = false, Func<bool>? canSend = null)
    {
        if (isDown && canSend?.Invoke() == false) return false;
        MouseTransitions.Enqueue((button, isDown, macroRelease));
        return MouseResult?.Invoke(button, isDown, macroRelease) ?? true;
    }

    public bool MoveMouseTo(int physicalX, int physicalY, Func<bool>? canSend = null)
    {
        if (canSend?.Invoke() == false) return false;
        MouseMoves.Enqueue((physicalX, physicalY));
        return true;
    }

    public bool SendMouseWheel(int delta, bool horizontal, Func<bool>? canSend = null)
    {
        if (canSend?.Invoke() == false) return false;
        MouseWheels.Enqueue((delta, horizontal));
        return true;
    }

    public bool SendVirtualKeyTap(int virtualKey) => true;

    public Func<LeftClickResult>? ClickResult { get; set; }

    public LeftClickResult SendLeftClick(int holdMilliseconds)
    {
        if (throwMouse)
        {
            throw new InvalidOperationException("Synthetic click failure");
        }

        MouseHoldMilliseconds.Enqueue(holdMilliseconds);
        MouseClickThreadIds.Enqueue(Environment.CurrentManagedThreadId);
        MouseEntered.Set();
        if (blockMouse && !ReleaseMouse.Wait(TimeSpan.FromSeconds(2))) return LeftClickResult.UpFailed;
        return ClickResult?.Invoke() ?? LeftClickResult.Sent;
    }

    public bool SendDummyKey()
    {
        DummyThreadIds.Enqueue(Environment.CurrentManagedThreadId);
        DummyEntered.Set();
        return !_blockDummy || ReleaseDummy.Wait(TimeSpan.FromSeconds(2));
    }
}
