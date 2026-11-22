using System.Collections.Concurrent;

namespace Tests.Fakes;

public sealed class FakeProcessExitWatch
{
    private readonly ConcurrentQueue<FakeWatch> _watches = new();
    private int _attempts;
    private int _releases;

    public bool Deny { get; set; }
    public ManualResetEventSlim RegistrationEntered { get; } = new(false);
    public ManualResetEventSlim? BlockRegistration { get; set; }
    public bool RunExitBeforeReturn { get; set; }

    public int Attempts => Volatile.Read(ref _attempts);
    public int Registrations => _watches.Count;
    public int Releases => Volatile.Read(ref _releases);
    public IReadOnlyList<FakeWatch> Watches => _watches.ToArray();

    public IDisposable? TryWatch(IntPtr windowHandle, uint processId, Action onExit)
    {
        Interlocked.Increment(ref _attempts);
        RegistrationEntered.Set();
        if (BlockRegistration is { } gate && !gate.Wait(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("Test did not release the blocked process watch registration.");
        }

        if (Deny)
        {
            return null;
        }

        var watch = new FakeWatch(this, windowHandle, processId, onExit);
        if (RunExitBeforeReturn)
        {
            watch.FireExit();
        }

        _watches.Enqueue(watch);
        return watch;
    }

    public sealed class FakeWatch(FakeProcessExitWatch owner, IntPtr windowHandle, uint processId, Action onExit) : IDisposable
    {
        private int _disposed;

        public IntPtr WindowHandle { get; } = windowHandle;
        public uint ProcessId { get; } = processId;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void FireExit() => onExit();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Increment(ref owner._releases);
            }
        }
    }
}
