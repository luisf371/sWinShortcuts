using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace sWinShortcuts.Services;

public sealed class FileLoggerService : ILoggerService, IDisposable
{
    private const int MaxQueuedEntries = 20_000;      // bound memory during high-frequency hook logging
    private const long MaxLogBytes = 2 * 1024 * 1024; // keep the newest 2 MiB in the active log

    private readonly string _logPath;
    private readonly BlockingCollection<string> _logQueue;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _writeTask;
    private volatile bool _isEnabled;
    private static readonly int ProcessId = Environment.ProcessId;

    public FileLoggerService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var rootDirectory = Path.Combine(appData, "sWinShortcuts");
        Directory.CreateDirectory(rootDirectory);
        _logPath = Path.Combine(rootDirectory, "debug.log");

        // Bounded: Log() uses TryAdd, so once full it drops newest entries instead of growing unbounded.
        _logQueue = new BlockingCollection<string>(new ConcurrentQueue<string>(), MaxQueuedEntries);
        _cancellation = new CancellationTokenSource();
        
        // Start background writer
        _writeTask = Task.Run(ProcessQueue);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public void Log(string message)
    {
        if (!_isEnabled)
        {
            return;
        }

        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var threadId = Environment.CurrentManagedThreadId;
            // Include the process id so a shared debug.log written by two instances is disambiguable
            // (a second instance is now prevented, but the tag keeps old/interleaved logs readable).
            var entry = $"[{timestamp}] [P{ProcessId}] [T{threadId:D3}] {message}";
            _logQueue.TryAdd(entry);
        }
        catch
        {
            // Ignore logging errors during enqueue
        }
    }

    private async Task ProcessQueue()
    {
        // Capture the token once: Dispose() waits only 2s for this task before disposing the CTS, so
        // a writer stuck in slow IO would otherwise fault on its next _cancellation.Token property
        // access (the loop condition sits outside the try). A captured token stays valid after the
        // source is disposed.
        var token = _cancellation.Token;
        var buffer = new List<string>();

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Wait for item or timeout
                if (_logQueue.TryTake(out var item, 1000, token))
                {
                    buffer.Add(item);

                    // Drain available items up to a limit
                    while (buffer.Count < 100 && _logQueue.TryTake(out var next))
                    {
                        buffer.Add(next);
                    }
                }

                if (buffer.Count > 0)
                {
                    TrimToNewest();
                    await File.AppendAllLinesAsync(_logPath, buffer, token).ConfigureAwait(false);
                    buffer.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Ignore file IO errors
                buffer.Clear(); // Prevent buffer from growing indefinitely on persistent error

                // S7: if shutdown cancels during the IO-error backoff, break to the final drain instead
                // of letting the OCE propagate out of ProcessQueue (which would skip the final flush).
                try
                {
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        // Flush remaining
        try
        {
            if (buffer.Count > 0)
            {
                await File.AppendAllLinesAsync(_logPath, buffer).ConfigureAwait(false);
            }
            
            while (_logQueue.TryTake(out var item))
            {
                File.AppendAllText(_logPath, item + Environment.NewLine);
            }
            TrimToNewest();
        }
        catch
        {
            // Ignore final flush errors
        }
    }

    private void TrimToNewest() => TrimLogFile(_logPath, MaxLogBytes);

    internal static void TrimLogFile(string logPath, long maxLogBytes)
    {
        try
        {
            var info = new FileInfo(logPath);
            if (info.Exists && info.Length > maxLogBytes)
            {
                var bytesToKeep = (int)Math.Min(info.Length, maxLogBytes);
                var buffer = new byte[bytesToKeep];

                using (var source = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    source.Seek(-bytesToKeep, SeekOrigin.End);
                    source.ReadExactly(buffer);
                }

                // Start at the first complete line in the retained window so the trim never leaves a
                // misleading partial record at the top of the file. If no newline exists, retain the
                // whole window; the hard byte cap still wins.
                var firstNewline = Array.IndexOf(buffer, (byte)'\n');
                var start = firstNewline >= 0 ? firstNewline + 1 : 0;
                var tempPath = logPath + ".trim";

                try
                {
                    using (var target = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        target.Write(buffer, start, buffer.Length - start);
                    }

                    File.Move(tempPath, logPath, overwrite: true);
                }
                catch
                {
                    try
                    {
                        if (File.Exists(tempPath))
                        {
                            File.Delete(tempPath);
                        }
                    }
                    catch
                    {
                        // Ignore cleanup failures.
                    }
                }
            }
        }
        catch
        {
            // Ignore trim failures — logging must never throw.
        }
    }

    public void Dispose()
    {
        // Fully exception-safe: a throw here must not prevent another container-disposed singleton
        // (e.g. SystemTrayService) from cleaning up (§14.5).
        try
        {
            _cancellation.Cancel();
        }
        catch
        {
            // Ignore
        }

        try
        {
            _writeTask.Wait(2000);
        }
        catch
        {
            // Ignore
        }

        try
        {
            _cancellation.Dispose();
        }
        catch
        {
            // Ignore
        }

        try
        {
            _logQueue.Dispose();
        }
        catch
        {
            // Ignore
        }
    }
}
