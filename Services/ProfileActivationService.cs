using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
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
    private readonly ILoggerService _logger;
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
    // Volatile: written by the ForegroundChanged handler, read in StartAsync. Today the initial event
    // fires synchronously on the starting thread, but the handler also runs from the WinEvent pump.
    private volatile bool _initialEventFired;

    // Set by the resume/display-change handlers; consumed by the worker so a forced re-apply routes
    // through the same C1 plan-diff (a resume while color is disabled therefore never wipes calibration).
    private int _forceReapply;
    private volatile string? _lastProcessName;
    private bool _reapplyHandlersRegistered;

    public ProfileActivationService(
        IProfileManager profileManager,
        IForegroundWatcher foregroundWatcher,
        IInputHookService inputHookService,
        ISystemTrayService systemTrayService,
        IColorControlService colorControlService,
        IDisplayService displayService,
        ILoggerService logger)
    {
        _profileManager = profileManager;
        _foregroundWatcher = foregroundWatcher;
        _inputHookService = inputHookService;
        _systemTrayService = systemTrayService;
        _colorControlService = colorControlService;
        _displayService = displayService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _profileManager.InitializeAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _inputHookService.SetWindowsProfile(_profileManager.WindowsProfile);
            _inputHookService.Start();

            _foregroundWorkerCancellation = new CancellationTokenSource();
            _foregroundWorkerTask = Task.Run(
                () => ProcessForegroundChangesAsync(_foregroundWorkerCancellation.Token),
                CancellationToken.None);

            _foregroundWatcher.ForegroundChanged += OnForegroundChanged;
            _inputHookService.ActiveProfileChanged += OnActiveProfileChanged;
            SystemEvents.DisplaySettingsChanged += OnReapplyRequested;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _reapplyHandlersRegistered = true;
            _foregroundWatcher.Start();

            if (!_initialEventFired)
            {
                _foregroundChanges.Writer.TryWrite(null);
            }
        }
        catch
        {
            // A failure after the first side effect must not leave hooks / static SystemEvents handlers /
            // worker state dangling until process exit. Tear down what we set up, then rethrow.
            CleanupAfterFailedStart();
            throw;
        }
    }

    private void CleanupAfterFailedStart()
    {
        try { _foregroundWatcher.ForegroundChanged -= OnForegroundChanged; } catch { /* best effort */ }
        try { _inputHookService.ActiveProfileChanged -= OnActiveProfileChanged; } catch { /* best effort */ }

        if (_reapplyHandlersRegistered)
        {
            try { SystemEvents.DisplaySettingsChanged -= OnReapplyRequested; } catch { /* best effort */ }
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { /* best effort */ }
            _reapplyHandlersRegistered = false;
        }

        try { _foregroundWatcher.Stop(); } catch { /* best effort */ }
        _foregroundChanges.Writer.TryComplete();
        _foregroundWorkerCancellation?.Cancel();
        try { _inputHookService.Stop(); } catch { /* best effort */ }

        _foregroundWorkerCancellation?.Dispose();
        _foregroundWorkerCancellation = null;
        _foregroundWorkerTask = null;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _foregroundWatcher.ForegroundChanged -= OnForegroundChanged;
        _inputHookService.ActiveProfileChanged -= OnActiveProfileChanged;
        if (_reapplyHandlersRegistered)
        {
            SystemEvents.DisplaySettingsChanged -= OnReapplyRequested;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _reapplyHandlersRegistered = false;
        }
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
        // A1: publish the foreground identity to the input hook IMMEDIATELY (on this watcher thread, off
        // the low-level hook thread), before the name-only channel write. Auto-Run activation confirms the
        // live foreground against this {hwnd,pid,exe} snapshot to fail closed without a hook-thread
        // Process.GetProcessById.
        _inputHookService.SetForegroundIdentity(e.WindowHandle, e.ProcessId, Utilities.ExecutableName.Normalize(e.ProcessName));
        // Record the newest foreground app on arrival (NOT at end-of-processing) so a resume/display
        // re-apply enqueues the genuinely-current app, never a previously-processed one (§14.2).
        _lastProcessName = e.ProcessName;
        _foregroundChanges.Writer.TryWrite(e.ProcessName);
    }

    private void OnReapplyRequested(object? sender, EventArgs e)
    {
        // Handler only touches an Interlocked flag + a thread-safe channel write; _lastAppliedColorPlan
        // stays owned by the worker thread.
        Interlocked.Exchange(ref _forceReapply, 1);
        _foregroundChanges.Writer.TryWrite(_lastProcessName);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            OnReapplyRequested(sender, EventArgs.Empty);
        }
    }

    private async Task ProcessForegroundChangesAsync(CancellationToken cancellationToken)
    {
        await foreach (var processName in _foregroundChanges.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                ProcessForegroundChange(processName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Log($"[Color] ProcessForegroundChange failed: {ex}");
            }
        }
    }

    private void ProcessForegroundChange(string? processName)
    {
        var profile = string.IsNullOrWhiteSpace(processName)
            ? null
            : _profileManager.FindByExecutable(processName);

        // Eager Auto-Run release the moment focus LEAVES the active profile — BEFORE the color work —
        // so a held Foreground W can't briefly leak into the incoming window during ApplyColorPlan. The
        // profile switch below still releases via ReleaseAllState (the hard no-stuck-key guarantee);
        // this only tightens the leak window. Same-app refocus (profile == _activeProfile) is a no-op.
        if (!ReferenceEquals(profile, _activeProfile))
        {
            _inputHookService.ReleaseForegroundAutoRun();
        }

        var displays = _displayService.GetDisplays();
        var plan = BuildColorPlan(profile, displays, _profileManager);

        var force = Interlocked.Exchange(ref _forceReapply, 0) == 1;
        if (force || !_lastAppliedColorPlan.Equals(plan))
        {
            // Advance the dedup baseline ONLY when every enabled display actually applied (§14.1):
            // a failed enabled apply stays un-deduped and retries on the next foreground/resume event.
            if (ApplyColorPlan(plan, _lastAppliedColorPlan, displays))
            {
                _lastAppliedColorPlan = plan;
            }
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
        var profiles = settingsToApply?.SnapshotProfiles();
        var plans = displays
            .OrderBy(display => display.Id, StringComparer.OrdinalIgnoreCase)
            .Select(display => BuildDisplayColorPlan(display.Id, profiles))
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

    private static DisplayColorPlan BuildDisplayColorPlan(
        string displayId,
        IReadOnlyDictionary<string, DisplayColorProfile>? profiles)
    {
        if (profiles is not null &&
            profiles.TryGetValue(displayId, out var existingProfile) &&
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

    private bool ApplyColorPlan(ColorPlan plan, ColorPlan previous, IReadOnlyList<DisplayInfo> displays)
    {
        var allApplied = true;

        foreach (var displayPlan in plan.Displays)
        {
            var prior = FindDisplayPlan(previous, displayPlan.DisplayId);

            // C1: a display that is disabled now AND was disabled/absent before was never applied, so
            // leave its hardware (ICC/Night Light/NVCP vibrance) untouched. Enabled-now and the
            // enabled->disabled restore transition both fall through and DO apply.
            if (!displayPlan.IsEnabled && (prior is null || !prior.IsEnabled))
            {
                continue;
            }

            var display = FindDisplay(displayPlan.DisplayId, displays);
            if (display is null)
            {
                // Wanted to apply/restore but the hardware isn't present — don't dedup; retry later.
                allApplied = false;
                continue;
            }

            var outcome = _colorControlService.Apply(display, new DisplayColorProfile
            {
                DisplayId = displayPlan.DisplayId,
                IsEnabled = displayPlan.IsEnabled,
                Brightness = displayPlan.Brightness,
                Contrast = displayPlan.Contrast,
                Gamma = displayPlan.Gamma,
                DigitalVibrance = displayPlan.DigitalVibrance
            });

            // Only a genuine transient failure blocks dedup; deliberate skips (NVAPI unavailable,
            // fail-closed CreateDC, unmappable DVC) count as applied to avoid a per-event retry storm.
            if (outcome == ColorApplyOutcome.Failed)
            {
                allApplied = false;
            }
        }

        return allApplied;
    }

    private static DisplayColorPlan? FindDisplayPlan(ColorPlan plan, string displayId)
    {
        foreach (var displayPlan in plan.Displays)
        {
            if (string.Equals(displayPlan.DisplayId, displayId, StringComparison.OrdinalIgnoreCase))
            {
                return displayPlan;
            }
        }

        return null;
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
