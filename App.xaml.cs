using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using sWinShortcuts.Configuration;
using sWinShortcuts.Services;
using sWinShortcuts.Utilities;
using sWinShortcuts.ViewModels;

namespace sWinShortcuts;

public partial class App : System.Windows.Application
{
    // Per-session single-instance guard. Two instances would install independent low-level input hooks +
    // injectors and both write the shared debug.log, producing conflicting input and unreadable logs.
    private const string SingleInstanceMutexName = @"Local\sWinShortcuts_SingleInstance_9E1C0B24-3F5A-4E77-9C2D-7B2A1F6C8D40";
    private System.Threading.Mutex? _singleInstanceMutex;
    private IHost? _host;
    private bool _exceptionHandlersRegistered;

    public App()
    {
        // Register before anything else can fail: the generated Main runs new App() ->
        // InitializeComponent() (App.xaml BAML / merged-dictionary load) -> Run(), so failures that
        // predate OnStartup now reach crash.log too. The _exceptionHandlersRegistered guard keeps
        // double registration impossible.
        RegisterExceptionHandlers();
    }

    private async void OnStartup(object sender, System.Windows.StartupEventArgs e)
    {
        // Single-instance: acquire the named mutex. If a prior instance already owns it, exit immediately
        // (the OS destroys the mutex when the owning process ends/crashes, so a stale lock self-heals).
        _singleInstanceMutex = new System.Threading.Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            CrashReporter.Write("App.SingleInstance", new InvalidOperationException("Another instance of sWinShortcuts is already running; this instance is exiting."));
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(ConfigureServices)
            .Build();

        // The toggle keys are app-level settings. Seed the hook before hosted services start so profile
        // activation never needs to resolve them from profile state.
        var inputHook = _host.Services.GetRequiredService<IInputHookService>();
        var settingsPath = AppSettings.GetSettingsPath();

        // Each toggle-key source is independent: a read failure for one must not drop another feature's
        // key for the whole session. Never rethrow — the app and input hooks must still start.
        try { inputHook.SetColorToggleKey(AppSettings.LoadColorToggleKey(settingsPath)); }
        catch (Exception ex) { CrashReporter.Write("App.ToggleKey.Color", ex); }
        try { inputHook.SetRapidFireToggleKey(AppSettings.LoadRapidFireToggleKey(settingsPath)); }
        catch (Exception ex) { CrashReporter.Write("App.ToggleKey.RapidFire", ex); }

        // Explicit ownership BEFORE anything can instantiate an overlay: WPF auto-assigns
        // Application.MainWindow to the FIRST-created Window, so the status dot (resolved below)
        // or the crosshair could become the MainWindow — under ShutdownMode=OnMainWindowClose,
        // closing that overlay would shut the whole app down.
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;

        // Eager: the status dot subscribes RapidFireArmChanged/ActiveProfileChanged here, before
        // the host starts and any arm event can fire. Disposal is owned by the host.
        _ = _host.Services.GetRequiredService<RapidFireStatusService>();

        // Eager (symmetry with the dot): resolve before the host starts so the first color-preset
        // hotkey press never pays the window-build jank. Disposal is owned by the host.
        _ = _host.Services.GetRequiredService<ColorProfileToastService>();

        await _host.StartAsync();

        var tray = _host.Services.GetRequiredService<ISystemTrayService>();
        tray.Initialize(mainWindow);

        mainWindow.Show();

        // Delayed self-check — no-op unless [App] CheckForUpdates=true AND this is a CI-numbered
        // build ("dev" never phones home). Never blocks startup: the fetch is pool-side and the
        // result is marshaled to the dispatcher by the service. Runs after the single-instance
        // early-return above, so a duplicate instance never checks.
        _host.Services.GetRequiredService<UpdateCheckService>().Start();
    }

    private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<IProfileStore, IniProfileStore>();
        services.AddSingleton<IProfileManager, ProfileManager>();
        services.AddSingleton<IForegroundWatcher, ForegroundWatcher>();
        services.AddSingleton<IInputSender, WindowsInputSender>();
        services.AddSingleton<IInputHookService, InputHookService>();
        services.AddSingleton<ISystemTrayService, SystemTrayService>();
        services.AddSingleton<IStartupService, StartupService>();
        services.AddSingleton<IDisplayService, DisplayService>();
        services.AddSingleton<NvidiaColorControlService>();
        services.AddSingleton<AmdColorControlService>();
        services.AddSingleton<IColorControlService, CompositeColorControlService>();
        services.AddSingleton<ILoggerService, FileLoggerService>();
        services.AddSingleton<ICrosshairService, CrosshairService>();
        services.AddSingleton<RapidFireStatusService>();
        services.AddSingleton<ColorProfileToastService>();
        services.AddSingleton<ProfileActivationService>();
        services.AddSingleton<IProfileRuntimeService>(
            provider => provider.GetRequiredService<ProfileActivationService>());
        services.AddHostedService(
            provider => provider.GetRequiredService<ProfileActivationService>());
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<UpdateCheckService>();
        services.AddSingleton<MainWindow>();
    }

    private void OnExit(object sender, System.Windows.ExitEventArgs e)
    {
        // One compact [EXIT] line per termination: any exit-path failure below marks it unclean, so a
        // degraded shutdown is distinguishable from a clean one in crash.log.
        var exitClean = true;

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
                            exitClean = false;
                            CrashReporter.Write("OnExit.Flush", new TimeoutException("FlushPendingSavesAsync did not complete within 3s; some edits may be unsaved."));
                        }
                        else if (flushTask.Result > 0)
                        {
                            // F-014: the flush completed but could not persist every edit (e.g. a locked
                            // file). Report it rather than exiting as if everything saved.
                            exitClean = false;
                            CrashReporter.Write("OnExit.Flush", new InvalidOperationException($"{flushTask.Result} profile edit(s) could not be saved before exit."));
                        }
                    }
                }
                catch (Exception ex)
                {
                    exitClean = false;
                    CrashReporter.Write("OnExit.Flush", ex);
                }

                // StopAsync OFF the dispatcher (avoids the sync-over-async deadlock). Dispose ON the
                // dispatcher (this thread) in finally so the tray icon is removed on its creating thread
                // and disposal is always reached even if StopAsync timed out or threw (§14.5).
                try
                {
                    var stopped = Task.Run(() => _host.StopAsync(TimeSpan.FromSeconds(2))).Wait(TimeSpan.FromSeconds(5));
                    if (!stopped)
                    {
                        exitClean = false;
                        CrashReporter.Write("OnExit.Stop", new TimeoutException("Host StopAsync did not complete within 5s; disposing anyway."));
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
            exitClean = false;
            CrashReporter.Write("OnExit", ex);
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

            // Always-on termination marker (Exit also fires on Windows logoff, so OS-shutdown
            // terminations are covered without a separate SessionEnding handler).
            CrashReporter.WriteExitMarker(exitClean);
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
        // ExceptionObject is not guaranteed to be an Exception (it can be a native/boxed object);
        // the old 'as Exception' silently logged nothing for those — carry them as detail instead.
        CrashReporter.Write("AppDomain.UnhandledException", e.ExceptionObject as Exception,
            e.ExceptionObject is Exception ? null : e.ExceptionObject?.ToString(), fatal: e.IsTerminating);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashReporter.Write("TaskScheduler.UnobservedTaskException", e.Exception,
            "unobserved task exception (process continues)");
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // Report only — e.Handled is deliberately NOT set, preserving the existing crash semantics.
        CrashReporter.Write("Application.DispatcherUnhandledException", e.Exception, fatal: true);
    }
}
