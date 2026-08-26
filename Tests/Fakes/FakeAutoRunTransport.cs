using System.Collections.Concurrent;
using sWinShortcuts.Services.Input;

namespace Tests.Fakes;

internal sealed class FakeAutoRunTransport : IAutoRunTransport
{
    private int _failPosts;
    private int _foregroundCallCount;
    private int _blockNextForegroundRead;

    internal IntPtr ForegroundWindow { get; set; } = (IntPtr)100;
    internal IntPtr ChildWindow { get; set; }
    internal ConcurrentDictionary<IntPtr, uint> ProcessIds { get; } = new();
    internal ConcurrentDictionary<int, short> KeyStates { get; } = new();
    internal ConcurrentQueue<(IntPtr Window, uint Message, int VirtualKey, int ThreadId)> Posts { get; } = new();
    internal ManualResetEventSlim PostEntered { get; } = new(false);
    internal ManualResetEventSlim ForegroundEntered { get; } = new(false);
    internal ManualResetEventSlim ReleaseForeground { get; } = new(false);
    internal bool BlockForegroundReads
    {
        get => Volatile.Read(ref _blockNextForegroundRead) != 0;
        set => Volatile.Write(ref _blockNextForegroundRead, value ? 1 : 0);
    }
    internal int ForegroundCallCount => Volatile.Read(ref _foregroundCallCount);

    internal void FailNextPost() => Interlocked.Increment(ref _failPosts);

    public IntPtr GetForegroundWindow()
    {
        var foregroundWindow = ForegroundWindow;
        Interlocked.Increment(ref _foregroundCallCount);
        if (Interlocked.Exchange(ref _blockNextForegroundRead, 0) != 0)
        {
            ForegroundEntered.Set();
            ReleaseForeground.Wait(TimeSpan.FromSeconds(2));
        }
        return foregroundWindow;
    }

    public IntPtr GetChildWindow(IntPtr window) => ChildWindow;

    public uint GetWindowThreadProcessId(IntPtr window, out uint processId)
    {
        ProcessIds.TryGetValue(window, out processId);
        return processId == 0 ? 0u : 1u;
    }

    public uint GetCurrentThreadId() => unchecked((uint)Environment.CurrentManagedThreadId);

    public short GetAsyncKeyState(int virtualKey) => KeyStates.GetValueOrDefault(virtualKey);

    public uint MapVirtualKey(uint code, uint mapType) => code;

    public bool IsHungAppWindow(IntPtr window) => false;

    public bool GetKeyboardState(byte[] state) => true;

    public bool SetKeyboardState(byte[] state) => true;

    public bool AttachThreadInput(uint sourceThread, uint targetThread, bool attach) => true;

    public bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        Posts.Enqueue((window, message, (int)wParam, Environment.CurrentManagedThreadId));
        PostEntered.Set();
        return Interlocked.Exchange(ref _failPosts, 0) == 0;
    }
}
