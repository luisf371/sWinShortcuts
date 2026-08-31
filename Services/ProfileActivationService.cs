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

public sealed class ProfileActivationService : IHostedService, IProfileRuntimeService
{
    private sealed record ForegroundSnapshot(
        long Generation,
        IntPtr WindowHandle,
        uint ProcessId,
        string? ProcessName,
        string? NormalizedExecutable,
        Profile? Profile);

    private readonly IProfileManager _profileManager;
    private readonly IForegroundWatcher _foregroundWatcher;
    private readonly IInputHookService _inputHookService;
    private readonly ISystemTrayService _systemTrayService;
    private readonly IColorControlService _colorControlService;
    private readonly IDisplayService _displayService;
    private readonly ICrosshairService _crosshairService;
    private readonly ILoggerService _logger;
    // Foreground callbacks, live profile edits, and display/power reapply events arrive on different
    // threads. Generation allocation, identity publication, and both channel writes must be one
    // indivisible operation; otherwise generation N+1 can be queued before N and the input worker can
    // permanently regress its active generation behind the published one.
    private readonly object _publicationLock = new();
    private Channel<ForegroundSnapshot>? _inputChanges;
    private Channel<ForegroundSnapshot>? _colorChanges;
    private volatile Profile? _activeProfile;
    private volatile ForegroundSnapshot? _latestForeground;
    private CancellationTokenSource? _workerCancellation;
    private Task? _inputWorkerTask;
    private Task? _colorWorkerTask;
    private long _foregroundGeneration;
    // F-010: set at the START of StopAsync; the worker checks it before every side effect so a late-returning
    // (uncancelable) native color call can't activate input / touch the tray after shutdown has begun.
    private volatile bool _stopping;
    private ColorPlan _lastAppliedColorPlan = ColorPlan.Empty;
    // Volatile: written by the ForegroundChanged handler, read in StartAsync. Today the initial event
    // fires synchronously on the starting thread, but the handler also runs from the WinEvent pump.
    private volatile bool _initialEventFired;

    // Set by the resume/display-change handlers; consumed by the worker so a forced re-apply routes
    // through the same C1 plan-diff (a resume while color is disabled therefore never wipes calibration).
    private int _forceReapply;
    // The ColorSettings currently being applied (active app profile's, else the global Color fallback),
    // published by the worker on each foreground change. The color-toggle hook event flips THIS object at
    // PRESS TIME (thread-safe via ColorSettings._sync) — so a toggle targets the color visible at the moment
    // of the press and preserves parity (one flip per press), instead of a deferred worker flip keyed off the
    // capacity-1 DropOldest channel (which coalesces and could otherwise flip the wrong/later profile).
    private volatile ColorSettings? _activeColorSettings;
    private bool _reapplyHandlersRegistered;

    // 30s force-preview override (game-profile color editing): while set (and the forced settings
    // are color-enabled) the color worker builds plans from THIS record instead of the foreground
    // profile, so slider changes are visible live while tuning in the sWinShortcuts window.
    // Writers: the UI thread (Set/Clear) and the hook thread (variant retarget on the color-toggle
    // hotkey) — volatile reference swaps, same pattern as _activeColorSettings. Forced plans use
    // SnapshotProfiles(explicit variant); the preview NEVER mutates runtime variant state.
    private sealed record ForcedColorPreview(ColorSettings Settings, ColorVariant Variant);

    private volatile ForcedColorPreview? _forcedColorPreview;

    // Optional (trailing) so the ~10 positional test constructions keep compiling; MS DI injects
    // the registered singleton. Null (tests) disables the toast.
    private readonly ColorProfileToastService? _toastService;

    public ProfileActivationService(
        IProfileManager profileManager,
        IForegroundWatcher foregroundWatcher,
        IInputHookService inputHookService,
        ISystemTrayService systemTrayService,
        IColorControlService colorControlService,
        IDisplayService displayService,
        ICrosshairService crosshairService,
        ILoggerService logger,
        ColorProfileToastService? toastService = null)
    {
        _profileManager = profileManager;
        _foregroundWatcher = foregroundWatcher;
        _inputHookService = inputHookService;
        _systemTrayService = systemTrayService;
        _colorControlService = colorControlService;
        _displayService = displayService;
        _crosshairService = crosshairService;
        _logger = logger;
        _toastService = toastService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _profileManager.InitializeAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Channel<ForegroundSnapshot> inputChanges;
            Channel<ForegroundSnapshot> colorChanges;
            CancellationTokenSource workerCancellation;
            lock (_publicationLock)
            {
                if (_inputWorkerTask is { IsCompleted: false } ||
                    _colorWorkerTask is { IsCompleted: false })
                {
                    throw new InvalidOperationException(
                        "The previous activation workers are still shutting down; retry Start after they exit.");
                }

                _workerCancellation?.Dispose();
                _workerCancellation = null;
                _inputWorkerTask = null;
                _colorWorkerTask = null;
                _inputChanges = null;
                _colorChanges = null;
                _stopping = false;
                _initialEventFired = false;
                // A Set racing a full StopAsync can leave a record assigned after StopAsync's own
                // clear (its _stopping check passed just before the flag flipped) — a fresh run
                // must never inherit a stale forced preview.
                _forcedColorPreview = null;
                inputChanges = Channel.CreateUnbounded<ForegroundSnapshot>(
                    new UnboundedChannelOptions
                    {
                        SingleReader = true,
                        SingleWriter = false,
                        AllowSynchronousContinuations = false
                    });
                colorChanges = Channel.CreateBounded<ForegroundSnapshot>(
                    new BoundedChannelOptions(1)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true,
                        SingleWriter = false,
                        AllowSynchronousContinuations = false
                    });
                workerCancellation = new CancellationTokenSource();
                _inputChanges = inputChanges;
                _colorChanges = colorChanges;
                _workerCancellation = workerCancellation;
            }

            _inputHookService.SetWindowsProfile(_profileManager.WindowsProfile);
            StartInputHookOnDispatcher();

            // Capture this run's immutable channel/token state. A failed start may clear the service
            // fields before Task.Run schedules its delegate; field-capturing here could otherwise make
            // the retired worker dereference null or consume a later restart's channel.
            var workerToken = workerCancellation.Token;
            _inputWorkerTask = Task.Run(
                () => ProcessInputChangesAsync(inputChanges, workerToken),
                CancellationToken.None);
            _colorWorkerTask = Task.Run(
                () => ProcessColorChangesAsync(colorChanges, workerToken),
                CancellationToken.None);

            _foregroundWatcher.ForegroundChanged += OnForegroundChanged;
            _profileManager.ProfileAdded += OnProfileAdded;
            _profileManager.ProfileRemoved += OnProfileRemoved;
            _inputHookService.ActiveProfileChanged += OnActiveProfileChanged;
            _inputHookService.ColorVariantToggleRequested += OnColorVariantToggleRequested;
            SystemEvents.DisplaySettingsChanged += OnReapplyRequested;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _reapplyHandlersRegistered = true;
            _foregroundWatcher.Start();

            if (!_initialEventFired)
            {
                PublishForeground(IntPtr.Zero, 0, null);
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

    private void StartInputHookOnDispatcher()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(_inputHookService.Start);
            return;
        }

        _inputHookService.Start();
    }

    private void CleanupAfterFailedStart()
    {
        CancellationTokenSource? workerCancellation;
        lock (_publicationLock)
        {
            _stopping = true;
            _inputChanges?.Writer.TryComplete();
            _colorChanges?.Writer.TryComplete();
            workerCancellation = _workerCancellation;
        }

        try { _foregroundWatcher.ForegroundChanged -= OnForegroundChanged; } catch { /* best effort */ }
        try { _profileManager.ProfileAdded -= OnProfileAdded; } catch { /* best effort */ }
        try { _profileManager.ProfileRemoved -= OnProfileRemoved; } catch { /* best effort */ }
        try { _inputHookService.ActiveProfileChanged -= OnActiveProfileChanged; } catch { /* best effort */ }
        try { _inputHookService.ColorVariantToggleRequested -= OnColorVariantToggleRequested; } catch { /* best effort */ }

        if (_reapplyHandlersRegistered)
        {
            try { SystemEvents.DisplaySettingsChanged -= OnReapplyRequested; } catch { /* best effort */ }
            try { SystemEvents.PowerModeChanged -= OnPowerModeChanged; } catch { /* best effort */ }
            _reapplyHandlersRegistered = false;
        }

        try { _foregroundWatcher.Stop(); } catch { /* best effort */ }
        workerCancellation?.Cancel();
        try { _inputHookService.Stop(); } catch { /* best effort */ }
        try { _crosshairService.ApplyProfile(null, IntPtr.Zero); } catch { /* best effort */ }

        lock (_publicationLock)
        {
            if ((_inputWorkerTask?.IsCompleted ?? true) &&
                (_colorWorkerTask?.IsCompleted ?? true))
            {
                workerCancellation?.Dispose();
                _workerCancellation = null;
                _inputWorkerTask = null;
                _colorWorkerTask = null;
                _inputChanges = null;
                _colorChanges = null;
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // F-010: signal the worker to make any in-flight/late foreground change side-effect-free — a native
        // color call that can't be canceled must not activate input or touch the tray after we stop below.
        CancellationTokenSource? workerCancellation;
        Task? inputWorkerTask;
        Task? colorWorkerTask;
        lock (_publicationLock)
        {
            _stopping = true;
            _inputChanges?.Writer.TryComplete();
            _colorChanges?.Writer.TryComplete();
            workerCancellation = _workerCancellation;
            inputWorkerTask = _inputWorkerTask;
            colorWorkerTask = _colorWorkerTask;
        }

        _foregroundWatcher.ForegroundChanged -= OnForegroundChanged;
        _profileManager.ProfileAdded -= OnProfileAdded;
        _profileManager.ProfileRemoved -= OnProfileRemoved;
        _inputHookService.ActiveProfileChanged -= OnActiveProfileChanged;
        _inputHookService.ColorVariantToggleRequested -= OnColorVariantToggleRequested;
        if (_reapplyHandlersRegistered)
        {
            SystemEvents.DisplaySettingsChanged -= OnReapplyRequested;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _reapplyHandlersRegistered = false;
        }
        _foregroundWatcher.Stop();
        workerCancellation?.Cancel();

        // Input teardown must never wait behind a slow, non-cancelable native color call.
        _inputHookService.Stop();
        _crosshairService.ApplyProfile(null, IntPtr.Zero);
        var inputWorkerCompleted =
            await WaitForWorkerAsync(inputWorkerTask, cancellationToken).ConfigureAwait(false);
        var colorWorkerCompleted =
            await WaitForWorkerAsync(colorWorkerTask, cancellationToken).ConfigureAwait(false);

        _activeProfile = null;
        _activeColorSettings = null;
        _forcedColorPreview = null; // no restore queued (F-010) — shutdown leaves the last plan on screen
        _lastAppliedColorPlan = ColorPlan.Empty;
        lock (_publicationLock)
        {
            _latestForeground = null;
            if (inputWorkerCompleted && colorWorkerCompleted)
            {
                workerCancellation?.Dispose();
                _workerCancellation = null;
                _inputWorkerTask = null;
                _colorWorkerTask = null;
                _inputChanges = null;
                _colorChanges = null;
            }
        }
    }

    private static async Task<bool> WaitForWorkerAsync(
        Task? worker,
        CancellationToken cancellationToken)
    {
        if (worker is null)
        {
            return true;
        }

        try
        {
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Pending work was canceled; an in-flight native color call completes synchronously.
            return worker.IsCompleted;
        }
    }

    private void OnForegroundChanged(object? sender, ForegroundChangedEventArgs e)
    {
        _initialEventFired = true;
        PublishForeground(
            e.WindowHandle,
            e.ProcessId,
            e.ProcessName,
            e.RequiresInputReset);
    }

    private void PublishForeground(
        IntPtr windowHandle,
        uint processId,
        string? processName,
        bool requiresInputReset = false)
    {
        lock (_publicationLock)
        {
            if (_stopping)
            {
                return;
            }

            PublishForegroundLocked(
                windowHandle,
                processId,
                processName,
                requiresInputReset);
        }
    }

    // Caller holds _publicationLock. Keeping generation allocation through both writes under one gate
    // makes channel order identical to generation order for every publisher.
    private void PublishForegroundLocked(
        IntPtr windowHandle,
        uint processId,
        string? processName,
        bool requiresInputReset = false)
    {
        var normalizedExecutable = string.IsNullOrWhiteSpace(processName)
            ? null
            : Utilities.ExecutableName.Normalize(processName);
        var profile = string.IsNullOrWhiteSpace(processName)
            ? null
            : _profileManager.FindByExecutable(processName);
        var generation = ++_foregroundGeneration;
        var snapshot = new ForegroundSnapshot(
            generation,
            windowHandle,
            processId,
            processName,
            normalizedExecutable,
            profile);

        // Publish identity + generation before queuing activation. Until the lossless input worker catches
        // up, InputHookService rejects only NEW profile-scoped presses; recorded releases still drain.
        var previousForeground = _latestForeground;
        _latestForeground = snapshot;
        _inputHookService.SetForegroundIdentity(windowHandle, processId, normalizedExecutable, generation);

        // A delayed callback can reveal that focus briefly left and returned before delivery. Release
        // the old profile's state now, but queue only the live snapshot so stale mappings/color never run.
        if (requiresInputReset)
        {
            _inputHookService.ReleaseForegroundState();
        }

        // A Foreground AutoRun belongs to the exact focused window, not merely to a profile/exe.
        // The same game can replace its HWND or restart under a new PID while still resolving to the
        // same Profile instance. End that run synchronously before the activation worker catches up.
        var foregroundIdentityChanged = previousForeground is null ||
                                        previousForeground.WindowHandle != windowHandle ||
                                        previousForeground.ProcessId != processId;
        if (foregroundIdentityChanged || !ReferenceEquals(profile, _activeProfile))
        {
            _inputHookService.ReleaseForegroundAutoRun();
        }

        _inputChanges?.Writer.TryWrite(snapshot);
        _colorChanges?.Writer.TryWrite(snapshot);
    }

    private void RepublishLatestForeground()
    {
        lock (_publicationLock)
        {
            if (_stopping)
            {
                return;
            }

            var latest = _latestForeground;
            if (latest is not null)
            {
                PublishForegroundLocked(latest.WindowHandle, latest.ProcessId, latest.ProcessName);
            }
        }
    }

    public void NotifyProfileChanged(Profile profile, ProfileChangeKind changeKind)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (_stopping || changeKind == ProfileChangeKind.None)
        {
            return;
        }

        if ((changeKind & (ProfileChangeKind.Master | ProfileChangeKind.Identity | ProfileChangeKind.Removed)) != 0)
        {
            // Publish the invalidating generation first. The hook immediately fails open for new
            // profile-scoped presses while reconciliation releases already-owned state.
            RepublishLatestForeground();
            _inputHookService.ReconcileProfileSettings(profile, changeKind);

            if ((changeKind & ProfileChangeKind.Removed) != 0)
            {
                // A removed ACTIVE profile's overlay must not outlive it. The republish above
                // re-resolves the foreground asynchronously; this immediate hide closes the gap
                // without touching a different profile's overlay.
                if (ReferenceEquals(_activeProfile, profile))
                {
                    _crosshairService.ApplyProfile(null, IntPtr.Zero);
                }
            }
            else
            {
                ReapplyCrosshairFor(profile);
            }

            return;
        }

        _inputHookService.ReconcileProfileSettings(profile, changeKind);

        if ((changeKind & ProfileChangeKind.Crosshair) != 0)
        {
            ReapplyCrosshairFor(profile);
        }

        if ((changeKind & ProfileChangeKind.Color) != 0)
        {
            QueueLatestColor(force: true);
        }
    }

    // Live crosshair edits and master toggles re-apply the ACTIVE profile's overlay immediately
    // (background-profile edits are ignored — only the active profile's crosshair is ever shown).
    private void ReapplyCrosshairFor(Profile editedProfile)
    {
        var active = _activeProfile;
        if (active is not null && !ReferenceEquals(active, editedProfile))
        {
            return;
        }

        _crosshairService.ApplyProfile(
            active is { IsEnabled: true } enabled ? enabled : null,
            _latestForeground?.WindowHandle ?? IntPtr.Zero);
    }

    private void OnProfileAdded(object? sender, Profile profile)
    {
        RepublishLatestForeground();
    }

    public void SetForcedColorPreview(ColorSettings settings, ColorVariant variant)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (_stopping)
        {
            return;
        }

        _forcedColorPreview = new ForcedColorPreview(settings, variant);
        // Routes through _publicationLock + the _stopping fence — never the channel directly.
        QueueLatestColor(force: true);
    }

    public void ClearForcedColorPreview()
    {
        _forcedColorPreview = null;

        if (_stopping)
        {
            // F-010: no restore is queued during shutdown — same acceptance as every other color
            // path at exit (the last-applied plan simply stays on screen).
            return;
        }

        // The auto-restore: force skips the _lastAppliedColorPlan dedup, so the
        // foreground-appropriate plan re-applies even if its values coincide with the forced one.
        QueueLatestColor(force: true);
    }

    private void OnProfileRemoved(object? sender, Profile profile)
    {
        NotifyProfileChanged(profile, ProfileChangeKind.Removed);
    }

    private void OnReapplyRequested(object? sender, EventArgs e)
    {
        QueueLatestColor(force: true);
    }

    private void OnColorVariantToggleRequested(object? sender, EventArgs e)
    {
        // Hook thread: flip the CURRENTLY-APPLIED color preset at PRESS TIME (thread-safe; internally a no-op
        // without a populated Secondary), then request a re-apply. Flipping here — not deferred to the worker
        // off the coalescing channel — preserves parity (one flip per press) and targets the color that was
        // visible at the instant of the press.
        var settings = _activeColorSettings;
        if (settings is not null)
        {
            var before = settings.ActiveVariant;
            var after = settings.ToggleVariant();
            if (before != after)
            {
                // Force preview active on exactly these settings -> RETARGET it to the flipped
                // variant so toast, screen, and preview stay consistent: the hotkey flips the
                // color visible on screen, which during a preview IS the forced profile. (When
                // forced.Settings.IsEnabled == false, ProcessColorChange's publication left
                // _activeColorSettings at the GLOBAL settings, so this branch simply doesn't
                // fire — the plan and its publication never disagree.)
                var forced = _forcedColorPreview;
                if (forced is not null && ReferenceEquals(forced.Settings, settings))
                {
                    _forcedColorPreview = new ForcedColorPreview(settings, after);
                }

                // Real flip -> toast the new variant. Raised HERE (press time, post-flip) because it is the
                // only path that encodes hotkey intent; a post-apply raise would fire spurious toasts on
                // foreground swaps between profiles with different active variants. Show is enqueue-only, so
                // this is safe on the hook thread. Trade-off: if the subsequent apply fails, the toast names
                // the queued variant; the failed apply stays un-deduped and retries, so screen and toast
                // converge.
                _toastService?.Show(after);
            }
        }

        QueueLatestColor(force: true);
    }

    private void QueueLatestColor(bool force)
    {
        lock (_publicationLock)
        {
            if (_stopping)
            {
                return;
            }

            if (force)
            {
                Interlocked.Exchange(ref _forceReapply, 1);
            }

            var latest = _latestForeground;
            if (latest is not null)
            {
                _colorChanges?.Writer.TryWrite(latest);
            }
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            OnReapplyRequested(sender, EventArgs.Empty);
        }
    }

    private async Task ProcessInputChangesAsync(
        Channel<ForegroundSnapshot> channel,
        CancellationToken cancellationToken)
    {
        await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                if (_stopping)
                {
                    continue;
                }

                if (snapshot.Profile is { IsEnabled: true } profile)
                {
                    _logger.Log(
                        $"[Input] Foreground decision=activate generation={snapshot.Generation} " +
                        $"hwnd=0x{snapshot.WindowHandle.ToInt64():X} pid={snapshot.ProcessId} " +
                        $"process={snapshot.ProcessName ?? "<empty>"} " +
                        $"normalized={snapshot.NormalizedExecutable ?? "<empty>"} profile={profile.Name}");
                    _inputHookService.ActivateProfile(profile, snapshot.Generation);
                    _crosshairService.ApplyProfile(profile, snapshot.WindowHandle);
                }
                else
                {
                    var decision = snapshot.Profile is null ? "no-match" : "profile-disabled";
                    _logger.Log(
                        $"[Input] Foreground decision={decision} generation={snapshot.Generation} " +
                        $"hwnd=0x{snapshot.WindowHandle.ToInt64():X} pid={snapshot.ProcessId} " +
                        $"process={snapshot.ProcessName ?? "<empty>"} " +
                        $"normalized={snapshot.NormalizedExecutable ?? "<empty>"} " +
                        $"profile={snapshot.Profile?.Name ?? "<none>"}");
                    _inputHookService.DeactivateProfile(snapshot.Generation);
                    _crosshairService.ApplyProfile(null, IntPtr.Zero);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Log($"[Input] Foreground profile transition failed: {ex}");
            }
        }
    }

    private async Task ProcessColorChangesAsync(
        Channel<ForegroundSnapshot> channel,
        CancellationToken cancellationToken)
    {
        await foreach (var snapshot in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                ProcessColorChange(snapshot);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Log($"[Color] ProcessColorChange failed: {ex}");
            }
        }
    }

    private void ProcessColorChange(ForegroundSnapshot snapshot)
    {
        var latest = _latestForeground;
        if (_stopping || latest is null || latest.Generation != snapshot.Generation)
        {
            return;
        }

        // SINGLE read per iteration: a second volatile read could race a concurrent Set/Clear/
        // retarget and make the plan build and the publication below disagree. forcedIsActive gates
        // BOTH — preserving the publication invariant that _activeColorSettings names the settings
        // whose plan is actually on screen: when the forced settings are color-disabled the
        // rendered plan degenerates to the global fallback, so the hotkey must target the GLOBAL
        // settings, not the disabled game settings.
        var forced = _forcedColorPreview;
        var forcedIsActive = forced is { Settings.IsEnabled: true };

        var displays = _displayService.GetDisplays();
        var plan = forcedIsActive
            ? BuildColorPlan(forced!.Settings, forced.Variant, displays, _profileManager)
            : BuildColorPlan(snapshot.Profile, displays, _profileManager);

        var force = Interlocked.Exchange(ref _forceReapply, 0) == 1;
        var planOnScreen = true;
        if (!_stopping && (force || !_lastAppliedColorPlan.Equals(plan))) // F-010: skip color once stopping
        {
            // Advance the dedup baseline ONLY when every enabled display actually applied (§14.1):
            // a failed enabled apply stays un-deduped and retries on the next foreground/resume event.
            if (ApplyColorPlan(plan, _lastAppliedColorPlan, displays))
            {
                _lastAppliedColorPlan = plan;
            }
            else
            {
                planOnScreen = false; // apply failed -> this plan is NOT on screen; keep the old toggle target
            }
        }

        // Publish AFTER a SUCCESSFUL/current apply so the color-toggle hook event only ever flips the
        // ColorSettings whose plan is actually on screen — not a profile whose color failed to apply or hasn't
        // been applied yet (codex). On failure _activeColorSettings keeps the old (still-visible) target.
        latest = _latestForeground;
        if (planOnScreen && latest is not null && latest.Generation == snapshot.Generation)
        {
            _activeColorSettings = forcedIsActive
                ? forced!.Settings
                : ResolveColorSettings(snapshot.Profile, _profileManager);
        }
    }

    internal static ColorPlan BuildColorPlan(
        Profile? activeProfile,
        IReadOnlyList<DisplayInfo> displays,
        IProfileManager profileManager)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(profileManager);

        var overrides = activeProfile is not null && activeProfile.IsEnabled && activeProfile.ColorSettings.IsEnabled
            ? activeProfile.ColorSettings.SnapshotProfiles()
            : null;

        return BuildColorPlanCore(overrides, GlobalColorFallbacks(profileManager), displays);
    }

    // Forced-preview plan: the EDITED profile's settings at the explicitly chosen variant (never
    // SetActiveVariant — the preview must not mutate runtime variant state). Unchecking the
    // profile's color mid-preview degenerates overrides to null, so the global look is previewed —
    // faithful to what real activation would render. Fallbacks stay global: displays the profile
    // doesn't own show the global values (same semantics as the active-profile path).
    internal static ColorPlan BuildColorPlan(
        ColorSettings forcedSettings,
        ColorVariant forcedVariant,
        IReadOnlyList<DisplayInfo> displays,
        IProfileManager profileManager)
    {
        ArgumentNullException.ThrowIfNull(forcedSettings);
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(profileManager);

        var overrides = forcedSettings.IsEnabled
            ? forcedSettings.SnapshotProfiles(forcedVariant)
            : null;

        return BuildColorPlanCore(overrides, GlobalColorFallbacks(profileManager), displays);
    }

    private static IReadOnlyDictionary<string, DisplayColorProfile>? GlobalColorFallbacks(IProfileManager profileManager)
    {
        // The merged "Window [Default]" built-in supplies the global fallback (the retired Color
        // profile's role); its sidebar enable is the master switch for the whole default profile.
        var globalProfile = profileManager.WindowsProfile;
        return globalProfile.IsEnabled && globalProfile.ColorSettings.IsEnabled
            ? globalProfile.ColorSettings.SnapshotProfiles()
            : null;
    }

    private static ColorPlan BuildColorPlanCore(
        IReadOnlyDictionary<string, DisplayColorProfile>? overrides,
        IReadOnlyDictionary<string, DisplayColorProfile>? fallbacks,
        IReadOnlyList<DisplayInfo> displays)
    {
        var plans = displays
            .OrderBy(display => display.Id, StringComparer.OrdinalIgnoreCase)
            .Select(display => BuildDisplayColorPlan(display.Id, overrides, fallbacks))
            .ToImmutableArray();

        return plans.IsEmpty ? ColorPlan.Empty : new ColorPlan(plans);
    }

    private static ColorSettings? ResolveColorSettings(Profile? activeProfile, IProfileManager profileManager)
    {
        if (activeProfile is not null && activeProfile.IsEnabled && activeProfile.ColorSettings.IsEnabled)
        {
            return activeProfile.ColorSettings;
        }

        var globalProfile = profileManager.WindowsProfile;
        if (globalProfile.IsEnabled && globalProfile.ColorSettings.IsEnabled)
        {
            return globalProfile.ColorSettings;
        }

        return null;
    }

    private static DisplayColorPlan BuildDisplayColorPlan(
        string displayId,
        IReadOnlyDictionary<string, DisplayColorProfile>? overrides,
        IReadOnlyDictionary<string, DisplayColorProfile>? fallbacks)
    {
        DisplayColorProfile? profile = null;
        if (overrides is not null &&
            overrides.TryGetValue(displayId, out var overrideProfile) &&
            overrideProfile.IsEnabled)
        {
            profile = overrideProfile;
        }
        else if (fallbacks is not null &&
                 fallbacks.TryGetValue(displayId, out var fallbackProfile) &&
                 fallbackProfile.IsEnabled)
        {
            profile = fallbackProfile;
        }

        if (profile is not null)
        {
            return new DisplayColorPlan(
                displayId,
                profile.IsEnabled,
                profile.Brightness,
                profile.Contrast,
                profile.Gamma,
                profile.DigitalVibrance);
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
                _logger.Log($"[Color] Display '{displayPlan.DisplayId}' is in the plan but absent from hardware; will retry on the next event.");
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
                _logger.Log($"[Color] Apply failed for display '{displayPlan.DisplayId}'; will retry on the next event.");
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
