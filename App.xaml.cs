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
    private IHost? _host;
    private bool _exceptionHandlersRegistered;

    private async void OnStartup(object sender, System.Windows.StartupEventArgs e)
    {
        RegisterExceptionHandlers();

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

    private async void OnExit(object sender, System.Windows.ExitEventArgs e)
    {
        if (_host is null)
        {
            return;
        }

        using (_host)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(2));
        }

        UnregisterExceptionHandlers();
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
