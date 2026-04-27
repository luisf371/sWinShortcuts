using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
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
    private readonly Channel<string?> _foregroundChanges =
        Channel.CreateBounded<string?>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private Profile? _activeProfile;
    private CancellationTokenSource? _foregroundWorkerCancellation;
    private Task? _foregroundWorkerTask;
    private ColorPlan _lastAppliedColorPlan = ColorPlan.Empty;
    private bool _initialEventFired;

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
        await _profileManager.InitializeAsync(cancellationToken).ConfigureAwait(false);

        _inputHookService.SetWindowsProfile(_profileManager.WindowsProfile);
        _inputHookService.Start();

        _foregroundWorkerCancellation = new CancellationTokenSource();
        _foregroundWorkerTask = Task.Run(
            () => ProcessForegroundChangesAsync(_foregroundWorkerCancellation.Token),
            CancellationToken.None);

        _foregroundWatcher.ForegroundChanged += OnForegroundChanged;
        _inputHookService.ActiveProfileChanged += OnActiveProfileChanged;
        _foregroundWatcher.Start();

        if (!_initialEventFired)
        {
            _foregroundChanges.Writer.TryWrite(null);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _foregroundWatcher.ForegroundChanged -= OnForegroundChanged;
        _inputHookService.ActiveProfileChanged -= OnActiveProfileChanged;
        _foregroundWatcher.Stop();
        _foregroundChanges.Writer.TryComplete();
        _foregroundWorkerCancellation?.Cancel();

        if (_foregroundWorkerTask is not null)
        {
            try
            {
                await _foregroundWorkerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // StopAsync can only cancel pending work; in-flight native calls finish synchronously.
            }
        }

        _inputHookService.Stop();
        _activeProfile = null;
        _lastAppliedColorPlan = ColorPlan.Empty;
        _foregroundWorkerCancellation?.Dispose();
        _foregroundWorkerCancellation = null;
        _foregroundWorkerTask = null;
    }

    private void OnForegroundChanged(object? sender, ForegroundChangedEventArgs e)
    {
        _initialEventFired = true;
        _foregroundChanges.Writer.TryWrite(e.ProcessName);
    }

    private async Task ProcessForegroundChangesAsync(CancellationToken cancellationToken)
    {
        await foreach (var processName in _foregroundChanges.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            ProcessForegroundChange(processName);
        }
    }

    private void ProcessForegroundChange(string? processName)
    {
        var profile = string.IsNullOrWhiteSpace(processName)
            ? null
            : _profileManager.FindByExecutable(processName);

        var displays = _displayService.GetDisplays();
        var plan = BuildColorPlan(profile, displays, _profileManager);

        if (!_lastAppliedColorPlan.Equals(plan))
        {
            ApplyColorPlan(plan, displays);
            _lastAppliedColorPlan = plan;
        }

        if (profile is { IsEnabled: true })
        {
            _inputHookService.ActivateProfile(profile);
        }
        else
        {
            _inputHookService.DeactivateProfile();
        }
    }

    internal static ColorPlan BuildColorPlan(
        Profile? activeProfile,
        IReadOnlyList<DisplayInfo> displays,
        IProfileManager profileManager)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(profileManager);

        var settingsToApply = ResolveColorSettings(activeProfile, profileManager);
        var plans = displays
            .OrderBy(display => display.Id, StringComparer.OrdinalIgnoreCase)
            .Select(display => BuildDisplayColorPlan(display.Id, settingsToApply))
            .ToImmutableArray();

        return plans.IsEmpty ? ColorPlan.Empty : new ColorPlan(plans);
    }

    private static ColorSettings? ResolveColorSettings(Profile? activeProfile, IProfileManager profileManager)
    {
        if (activeProfile is not null && activeProfile.IsEnabled && activeProfile.ColorSettings.IsEnabled)
        {
            return activeProfile.ColorSettings;
        }

        var globalColorProfile = profileManager.ColorProfile;
        if (globalColorProfile.IsEnabled && globalColorProfile.ColorSettings.IsEnabled)
        {
            return globalColorProfile.ColorSettings;
        }

        return null;
    }

    private static DisplayColorPlan BuildDisplayColorPlan(string displayId, ColorSettings? settingsToApply)
    {
        if (settingsToApply is not null &&
            settingsToApply.DisplayProfiles.TryGetValue(displayId, out var existingProfile) &&
            existingProfile.IsEnabled)
        {
            return new DisplayColorPlan(
                displayId,
                existingProfile.IsEnabled,
                existingProfile.Brightness,
                existingProfile.Contrast,
                existingProfile.Gamma,
                existingProfile.DigitalVibrance);
        }

        return new DisplayColorPlan(
            displayId,
            false,
            DisplayColorProfile.DefaultBrightness,
            DisplayColorProfile.DefaultContrast,
            DisplayColorProfile.DefaultGamma,
            DisplayColorProfile.DefaultDigitalVibrance);
    }

    private void ApplyColorPlan(ColorPlan plan, IReadOnlyList<DisplayInfo> displays)
    {
        foreach (var displayPlan in plan.Displays)
        {
            var display = FindDisplay(displayPlan.DisplayId, displays);
            if (display is null)
            {
                continue;
            }

            _colorControlService.Apply(display, new DisplayColorProfile
            {
                DisplayId = displayPlan.DisplayId,
                IsEnabled = displayPlan.IsEnabled,
                Brightness = displayPlan.Brightness,
                Contrast = displayPlan.Contrast,
                Gamma = displayPlan.Gamma,
                DigitalVibrance = displayPlan.DigitalVibrance
            });
        }
    }

    private static DisplayInfo? FindDisplay(string displayId, IReadOnlyList<DisplayInfo> displays)
    {
        foreach (var display in displays)
        {
            if (string.Equals(display.Id, displayId, StringComparison.OrdinalIgnoreCase))
            {
                return display;
            }
        }

        return null;
    }

    private void OnActiveProfileChanged(object? sender, Profile? profile)
    {
        _activeProfile = profile;
        var isActive = profile is not null && profile.IsEnabled;
        _systemTrayService.UpdateStatus(isActive, profile?.Name);
    }
}
