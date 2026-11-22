using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using sWinShortcuts.Interop;

namespace sWinShortcuts.Services;

internal static class WindowsProcessExitWatch
{
    internal static IDisposable? TryWatch(uint processId, Func<bool> stillOwnsWindow, Action onExit, ILoggerService? logger)
    {
        // Waiting for exit only requires SYNCHRONIZE access.
        var handle = NativeMethods.OpenProcess(NativeMethods.SYNCHRONIZE, bInheritHandle: false, processId);
        int error = Marshal.GetLastWin32Error();
        if (handle.IsInvalid)
        {
            handle.Dispose();
            logger?.Log($"[Color] Keep color active: cannot watch pid={processId} (OpenProcess error {error}).");
            return null;
        }

        var waitHandle = new ProcessWaitHandle(handle);
        try
        {
            if (stillOwnsWindow())
            {
                var registration = ThreadPool.RegisterWaitForSingleObject(
                    waitHandle, static (state, _) => ((Action)state!).Invoke(), onExit, Timeout.Infinite, executeOnlyOnce: true);
                return new Watch(registration, waitHandle);
            }
        }
        catch (Exception ex)
        {
            waitHandle.Dispose();
            logger?.Log($"[Color] Keep color active: cannot watch pid={processId} ({ex.Message}).");
            return null;
        }

        waitHandle.Dispose();
        logger?.Log($"[Color] Keep color active: cannot watch pid={processId} (window ownership changed).");
        return null;
    }

    internal static IDisposable? TryRegister(IntPtr windowHandle, uint processId, Action onExit, ILoggerService? logger)
    {
        if (windowHandle == IntPtr.Zero || processId == 0)
        {
            logger?.Log($"[Color] Keep color active: cannot watch pid={processId} (missing window handle or process ID).");
            return null;
        }

        return TryWatch(
            processId,
            stillOwnsWindow: () => NativeMethods.GetWindowThreadProcessId(windowHandle, out var owner) != 0 && owner == processId,
            onExit,
            logger);
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeWaitHandle handle)
        {
            SafeWaitHandle = handle;
        }
    }

    private sealed class Watch(RegisteredWaitHandle registration, ProcessWaitHandle waitHandle) : IDisposable
    {
        private readonly RegisteredWaitHandle _registration = registration;
        private readonly ProcessWaitHandle _waitHandle = waitHandle;

        public void Dispose()
        {
            // Unregister before releasing the process handle owned by this watch.
            _registration.Unregister(null);
            _waitHandle.Dispose();
        }
    }
}
