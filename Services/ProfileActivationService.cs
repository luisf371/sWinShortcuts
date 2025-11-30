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
    private readonly IColorControlService _colorControlService;
    private readonly IDisplayService _displayService;
    private Profile? _activeProfile;

    public ProfileActivationService(
        IProfileManager profileManager,
        IForegroundWatcher foregroundWatcher,
        IInputHookService inputHookService,
        ISystemTrayService systemTrayService,
        IColorControlService colorControlService,
        IDisplayService displayService)
    {
        _profileManager = profileManager;
        _foregroundWatcher = foregroundWatcher;
        _inputHookService = inputHookService;
        _systemTrayService = systemTrayService;
        _colorControlService = colorControlService;
        _displayService = displayService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _profileManager.InitializeAsync(cancellationToken);

        _inputHookService.SetWindowsProfile(_profileManager.WindowsProfile);
        _inputHookService.Start();

        // Apply default color profile on startup
        ApplyColorProfile(null);

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
        
        ApplyColorProfile(profile);

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

    private void ApplyColorProfile(Profile? activeProfile)
    {
        ColorSettings? settingsToApply = null;

        // 1. Try active profile's color settings
        if (activeProfile is not null && activeProfile.IsEnabled && activeProfile.ColorSettings.IsEnabled)
        {
            settingsToApply = activeProfile.ColorSettings;
        }
        // 2. Fallback to Global Color Profile
        else
        {
            var globalColorProfile = _profileManager.ColorProfile;
            // The global profile itself might be disabled (in the list), or its ColorSettings.IsEnabled might be false
            // (though we default it to true). The user requirement says:
            // "color profile will be the profile used for Windows and any non-profile apps, if disabled, it simply wont revert back"
            if (globalColorProfile is not null && globalColorProfile.IsEnabled && globalColorProfile.ColorSettings.IsEnabled)
            {
                settingsToApply = globalColorProfile.ColorSettings;
            }
        }

        if (settingsToApply is not null)
        {
            var displayId = settingsToApply.SelectedDisplayId;
            if (!string.IsNullOrEmpty(displayId) && settingsToApply.DisplayProfiles.TryGetValue(displayId, out var displayProfile))
            {
                var display = _displayService.GetDisplays().FirstOrDefault(d => d.Id == displayId);
                if (display is not null)
                {
                    _colorControlService.Apply(display, displayProfile);
                }
            }
        }
    }


    private void OnActiveProfileChanged(object? sender, Profile? profile)
    {
        _activeProfile = profile;
        var isActive = profile is not null && profile.IsEnabled;
        _systemTrayService.UpdateStatus(isActive, profile?.Name);
    }
}
