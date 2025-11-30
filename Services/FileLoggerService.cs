using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace sWinShortcuts.Services;

public sealed class FileLoggerService : ILoggerService, IDisposable
{
    private readonly string _logPath;
    private readonly BlockingCollection<string> _logQueue;
    private readonly CancellationTokenSource _cancellation;
    private readonly Task _writeTask;
    private volatile bool _isEnabled;

    public FileLoggerService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var rootDirectory = Path.Combine(appData, "sWinShortcuts");
        Directory.CreateDirectory(rootDirectory);
        _logPath = Path.Combine(rootDirectory, "debug.log");

        _logQueue = new BlockingCollection<string>(new ConcurrentQueue<string>());
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
            var entry = $"[{timestamp}] [T{threadId:D3}] {message}";
            _logQueue.TryAdd(entry);
        }
        catch
        {
            // Ignore logging errors during enqueue
        }
    }

    private async Task ProcessQueue()
    {
        var buffer = new List<string>();
        
        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                // Wait for item or timeout
                if (_logQueue.TryTake(out var item, 1000, _cancellation.Token))
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
                    await File.AppendAllLinesAsync(_logPath, buffer, _cancellation.Token).ConfigureAwait(false);
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
                await Task.Delay(1000, _cancellation.Token);
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
        }
        catch
        {
            // Ignore final flush errors
        }
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try
        {
            _writeTask.Wait(2000);
        }
        catch
        {
            // Ignore
        }
        
        _cancellation.Dispose();
        _logQueue.Dispose();
    }
}
