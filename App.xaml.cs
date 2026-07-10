using System;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using sWinShortcuts.Configuration;
using sWinShortcuts.Services;
using sWinShortcuts.ViewModels;

namespace sWinShortcuts;

public partial class App : System.Windows.Application
{
    private static readonly object CrashLogSync = new();
    // Per-session single-instance guard. Two instances would install independent low-level input hooks +
    // injectors and both write the shared debug.log, producing conflicting input and unreadable logs.
    private const string SingleInstanceMutexName = @"Local\sWinShortcuts_SingleInstance_9E1C0B24-3F5A-4E77-9C2D-7B2A1F6C8D40";
    private System.Threading.Mutex? _singleInstanceMutex;
    private IHost? _host;
    private bool _exceptionHandlersRegistered;

    private async void OnStartup(object sender, System.Windows.StartupEventArgs e)
    {
        RegisterExceptionHandlers();

        // Single-instance: acquire the named mutex. If a prior instance already owns it, exit immediately
        // (the OS destroys the mutex when the owning process ends/crashes, so a stale lock self-heals).
        _singleInstanceMutex = new System.Threading.Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            LogCrash("SingleInstance", new InvalidOperationException("Another instance of sWinShortcuts is already running; this instance is exiting."));
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(ConfigureServices)
            .Build();

        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        var tray = _host.Services.GetRequiredService<ISystemTrayService>();
        tray.Initialize(mainWindow);

        mainWindow.Show();
    }

    private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IProfileStore, IniProfileStore>();
        services.AddSingleton<IProfileManager, ProfileManager>();
        services.AddSingleton<IForegroundWatcher, ForegroundWatcher>();
        services.AddSingleton<IInputHookService, InputHookService>();
        services.AddSingleton<ISystemTrayService, SystemTrayService>();
        services.AddSingleton<IStartupService, StartupService>();
        services.AddSingleton<IDisplayService, DisplayService>();
        services.AddSingleton<IColorControlService, NvidiaColorControlService>();
        services.AddSingleton<ILoggerService, FileLoggerService>();
        services.AddHostedService<ProfileActivationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    private void OnExit(object sender, System.Windows.ExitEventArgs e)
    {
        try
        {
            if (_host is not null)
            {
                // Flush pending profile edits BEFORE stopping services so no debounced edit is lost (M1).
                try
                {
                    var mainViewModel = _host.Services.GetService<MainViewModel>();
                    if (mainViewModel is not null)
                    {
                        var flushTask = Task.Run(() => mainViewModel.FlushPendingSavesAsync());
                        if (!flushTask.Wait(TimeSpan.FromSeconds(3)))
                        {
                            LogCrash("OnExit.Flush", new TimeoutException("FlushPendingSavesAsync did not complete within 3s; some edits may be unsaved."));
                        }
                        else if (flushTask.Result > 0)
                        {
                            // F-014: the flush completed but could not persist every edit (e.g. a locked
                            // file). Report it rather than exiting as if everything saved.
                            LogCrash("OnExit.Flush", new InvalidOperationException($"{flushTask.Result} profile edit(s) could not be saved before exit."));
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogCrash("OnExit.Flush", ex);
                }

                // StopAsync OFF the dispatcher (avoids the sync-over-async deadlock). Dispose ON the
                // dispatcher (this thread) in finally so the tray icon is removed on its creating thread
                // and disposal is always reached even if StopAsync timed out or threw (§14.5).
                try
                {
                    var stopped = Task.Run(() => _host.StopAsync(TimeSpan.FromSeconds(2))).Wait(TimeSpan.FromSeconds(5));
                    if (!stopped)
                    {
                        LogCrash("OnExit.Stop", new TimeoutException("Host StopAsync did not complete within 5s; disposing anyway."));
                    }
                }
                finally
                {
                    _host.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            LogCrash("OnExit", ex);
        }
        finally
        {
            UnregisterExceptionHandlers();
            if (_singleInstanceMutex is not null)
            {
                try { _singleInstanceMutex.ReleaseMutex(); } catch { /* not owned / already released */ }
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
        }
    }

    private void RegisterExceptionHandlers()
    {
        if (_exceptionHandlersRegistered)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        _exceptionHandlersRegistered = true;
    }

    private void UnregisterExceptionHandlers()
    {
        if (!_exceptionHandlersRegistered)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        _exceptionHandlersRegistered = false;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogCrash("AppDomain.CurrentDomain.UnhandledException", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("Application.DispatcherUnhandledException", e.Exception);
    }

    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var rootDirectory = Path.Combine(appData, "sWinShortcuts");
            Directory.CreateDirectory(rootDirectory);

            var entry = $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}";
            lock (CrashLogSync)
            {
                File.AppendAllText(Path.Combine(rootDirectory, "crash.log"), entry);
            }
        }
        catch
        {
            // Ignore crash logging failures.
        }
    }
}
