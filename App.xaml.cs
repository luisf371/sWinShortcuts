using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using sWinShortcuts.Configuration;
using sWinShortcuts.Services;
using sWinShortcuts.ViewModels;

namespace sWinShortcuts;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    private async void OnStartup(object sender, System.Windows.StartupEventArgs e)
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(ConfigureServices)
            .Build();

        await _host.StartAsync().ConfigureAwait(false);

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
            await _host.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
    }
}
