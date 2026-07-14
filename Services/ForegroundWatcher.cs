using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using sWinShortcuts.Interop;

namespace sWinShortcuts.Services;

public sealed class ForegroundWatcher : IForegroundWatcher
{
    private readonly object _lifecycleLock = new();
    private NativeMethods.WinEventDelegate? _callback;
    private IntPtr _hookHandle;
    private IntPtr _lastWindow;
    private Thread? _hookThread;
    private Dispatcher? _hookDispatcher;
    private bool _shutdownRequested;
    private bool _disposed;

    public event EventHandler<ForegroundChangedEventArgs>? ForegroundChanged;

    public void Start()
    {
        Task startup;
        Thread thread;

        lock (_lifecycleLock)
        {
            ThrowIfDisposed();

            if (_hookThread is { IsAlive: true })
            {
                if (_shutdownRequested)
                {
                    throw new InvalidOperationException(
                        "The previous foreground watcher is still shutting down; retry Start after it exits.");
                }

                return;
            }

            _hookThread = null;
            _hookDispatcher = null;
            _hookHandle = IntPtr.Zero;
            _lastWindow = IntPtr.Zero;
            _shutdownRequested = false;

            var started = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            startup = started.Task;
            thread = new Thread(() => RunHookThread(started))
            {
                IsBackground = true,
                Name = "ForegroundWinEvent"
            };
            _hookThread = thread;
            thread.Start();
        }

        try
        {
            startup.GetAwaiter().GetResult();
        }
        catch
        {
            thread.Join(2000);
            throw;
        }
    }

    public void Stop()
    {
        Thread? thread;
        Dispatcher? dispatcher;

        lock (_lifecycleLock)
        {
            thread = _hookThread;
            if (thread is null)
            {
                return;
            }

            _shutdownRequested = true;
            dispatcher = _hookDispatcher;
        }

        // BeginInvokeShutdown is thread-safe. The hook thread's finally block performs UnhookWinEvent,
        // satisfying Win32's same-installing-thread requirement even when hosted-service Stop runs on
        // a pool thread.
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }

        if (ReferenceEquals(Thread.CurrentThread, thread))
        {
            return;
        }

        // A callback can briefly be resolving a process while shutdown is requested. Keep the thread and
        // hook references if the bounded join expires; the queued dispatcher shutdown still makes it exit,
        // and Start refuses to create a second watcher alongside it.
        thread.Join(2000);
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }

    private void RunHookThread(TaskCompletionSource<bool> started)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        IntPtr hookHandle = IntPtr.Zero;

        try
        {
            _callback = OnWinEvent;
            hookHandle = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _callback,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT);

            if (hookHandle == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Failed to register foreground window hook.");
            }

            bool shutdownRequested;
            lock (_lifecycleLock)
            {
                _hookHandle = hookHandle;
                _hookDispatcher = dispatcher;
                shutdownRequested = _shutdownRequested;
            }

            FireInitialForeground();
            started.TrySetResult(true);

            if (shutdownRequested)
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }

            // SetWinEventHook requires a message loop, and WINEVENT_OUTOFCONTEXT callbacks are delivered
            // on this installing thread.
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            started.TrySetException(ex);
        }
        finally
        {
            if (hookHandle != IntPtr.Zero)
            {
                // Must run on this same thread. If it still fails, ending the client thread also removes
                // the hook according to the Win32 contract.
                NativeMethods.UnhookWinEvent(hookHandle);
            }

            lock (_lifecycleLock)
            {
                if (ReferenceEquals(_hookThread, Thread.CurrentThread))
                {
                    _hookHandle = IntPtr.Zero;
                    _hookDispatcher = null;
                    _hookThread = null;
                    _lastWindow = IntPtr.Zero;
                    _callback = null;
                    _shutdownRequested = false;
                }
            }
        }
    }

    private void FireInitialForeground()
    {
        var current = NativeMethods.GetForegroundWindow();
        if (current == IntPtr.Zero)
        {
            return;
        }

        _lastWindow = current;
        var processName = ResolveProcessName(current, out var processId);
        ForegroundChanged?.Invoke(
            this,
            new ForegroundChangedEventArgs(current, processName, processId));
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        try
        {
            if (hwnd == IntPtr.Zero || hwnd == _lastWindow)
            {
                return;
            }

            _lastWindow = hwnd;
            var processName = ResolveProcessName(hwnd, out var processId);
            ForegroundChanged?.Invoke(
                this,
                new ForegroundChangedEventArgs(hwnd, processName, processId));
        }
        catch
        {
            // Never let a managed subscriber exception escape a native WinEvent callback.
        }
    }

    private static string ResolveProcessName(IntPtr hwnd, out uint processId)
    {
        processId = 0;
        try
        {
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            if (processId == 0)
            {
                return string.Empty;
            }

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            processId = 0;
            return string.Empty;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ForegroundWatcher));
        }
    }
}
