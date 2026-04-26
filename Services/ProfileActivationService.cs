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
        // Offload to a background thread to prevent blocking the hook/UI thread
        Task.Run(() =>
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
        });
    }

    private void ApplyColorProfile(Profile? activeProfile)
    {
        ColorSettings? settingsToApply = null;

        // 1. Try active profile's color settings
        // Check if profile is active AND the Master Color Toggle is ON
        if (activeProfile is not null && activeProfile.IsEnabled && activeProfile.ColorSettings.IsEnabled)
        {
            settingsToApply = activeProfile.ColorSettings;
        }
        // 2. Fallback to Global Color Profile
        else
        {
            var globalColorProfile = _profileManager.ColorProfile;
            if (globalColorProfile is not null && globalColorProfile.IsEnabled && globalColorProfile.ColorSettings.IsEnabled)
            {
                settingsToApply = globalColorProfile.ColorSettings;
            }
        }

        // 3. Apply settings for ALL detected displays
        foreach (var display in _displayService.GetDisplays())
        {
            DisplayColorProfile? displayProfileToApply = null;

            if (settingsToApply is not null)
            {
                // Try to find custom settings for this display
                if (settingsToApply.DisplayProfiles.TryGetValue(display.Id, out var existingProfile))
                {
                    // Only use if the individual monitor toggle is ON
                    if (existingProfile.IsEnabled)
                    {
                        displayProfileToApply = existingProfile;
                    }
                }
            }

            // If we have a profile to apply, apply it.
            // If NOT (settingsToApply was null, OR no profile for this monitor, OR individual toggle OFF), apply DEFAULTS (Revert).
            if (displayProfileToApply is not null)
            {
                _colorControlService.Apply(display, displayProfileToApply);
            }
            else
            {
                // Revert to default neutral values
                _colorControlService.Apply(display, new DisplayColorProfile
                {
                    DisplayId = display.Id,
                    IsEnabled = false,
                    Brightness = DisplayColorProfile.DefaultBrightness,
                    Contrast = DisplayColorProfile.DefaultContrast,
                    Gamma = DisplayColorProfile.DefaultGamma,
                    DigitalVibrance = DisplayColorProfile.DefaultDigitalVibrance
                });
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
