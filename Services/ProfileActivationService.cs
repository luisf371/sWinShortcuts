using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public sealed class ProfileActivationService : IHostedService
{
    private readonly IProfileManager _profileManager;
    private readonly IForegroundWatcher _foregroundWatcher;
    private readonly IInputHookService _inputHookService;
    private readonly ISystemTrayService _systemTrayService;
    private Profile? _activeProfile;

    public ProfileActivationService(
        IProfileManager profileManager,
        IForegroundWatcher foregroundWatcher,
        IInputHookService inputHookService,
        ISystemTrayService systemTrayService)
    {
        _profileManager = profileManager;
        _foregroundWatcher = foregroundWatcher;
        _inputHookService = inputHookService;
        _systemTrayService = systemTrayService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _profileManager.InitializeAsync(cancellationToken);

        _inputHookService.SetWindowsProfile(_profileManager.WindowsProfile);
        _inputHookService.Start();

        _foregroundWatcher.ForegroundChanged += OnForegroundChanged;
        _inputHookService.ActiveProfileChanged += OnActiveProfileChanged;
        _foregroundWatcher.Start();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _foregroundWatcher.ForegroundChanged -= OnForegroundChanged;
        _inputHookService.ActiveProfileChanged -= OnActiveProfileChanged;
        _foregroundWatcher.Stop();
        _inputHookService.Stop();
        _activeProfile = null;
        return Task.CompletedTask;
    }

    private void OnForegroundChanged(object? sender, ForegroundChangedEventArgs e)
    {
        var profile = _profileManager.FindByExecutable(e.ProcessName);

        if (profile is null)
        {
            _inputHookService.DeactivateProfile();
            return;
        }

        if (!profile.IsEnabled)
        {
            _inputHookService.DeactivateProfile();
            return;
        }

        _inputHookService.ActivateProfile(profile);
    }

    private void OnActiveProfileChanged(object? sender, Profile? profile)
    {
        _activeProfile = profile;
        var isActive = profile is not null && profile.IsEnabled;
        _systemTrayService.UpdateStatus(isActive, profile?.Name);
    }
}
