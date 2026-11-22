using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace sWinShortcuts.ViewModels;

/// <summary>
/// ViewModel for the Color Settings section.
/// Manages the master enable toggle and a collection of per-display ViewModels.
/// </summary>
public sealed class ColorSettingsViewModel : ViewModelBase, IDisposable
{
    private const int ForcePreviewSeconds = 30;

    private readonly ColorSettings _model;
    private readonly IColorControlService _colorService;
    private readonly IDisplayService _displayService;
    private readonly SynchronizationContext? _ownerContext;
    private IReadOnlyList<DisplayInfo> _displaySnapshot = [];
    private int _displayRefreshVersion;
    private int _displayRefreshScheduled;
    private readonly bool _allowLiveUpdates;
    private readonly Func<bool>? _parentEnabledCheck;
    // Owns the preview UI state (checkbox + countdown); ProfileActivationService owns the forced
    // runtime state (what the worker applies). Null (tests / no runtime) hides the feature.
    private readonly IProfileRuntimeService? _runtimeService;
    private bool _isEnabled;
    private bool _keepColorActive;
    private volatile bool _disposed;
    private ColorVariant _editingVariant = ColorVariant.Primary;

    // Force-preview UI state. The DispatcherTimer is created ONLY when a dispatcher exists —
    // headless tests drive PreviewTick directly (see StartForcePreview).
    private bool _isForcePreviewActive;
    private int _forcePreviewRemainingSeconds;
    private DispatcherTimer? _previewCountdownTimer;

    public event EventHandler? Changed;

    public ColorSettingsViewModel(
        ColorSettings model,
        IDisplayService displayService,
        IColorControlService colorService,
        bool allowLiveUpdates = false,
        Func<bool>? parentEnabledCheck = null,
        IProfileRuntimeService? profileRuntimeService = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _displayService = displayService ?? throw new ArgumentNullException(nameof(displayService));
        _ownerContext = SynchronizationContext.Current;
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));
        _allowLiveUpdates = allowLiveUpdates;
        _parentEnabledCheck = parentEnabledCheck;
        _runtimeService = profileRuntimeService;

        _isEnabled = model.IsEnabled;
        _keepColorActive = model.KeepColorActive;

        // Rebuild when monitors are hot-plugged/removed. DisplayService is a singleton that outlives
        // this (transient) VM, so we MUST unsubscribe in Dispose or the handler leaks.
        _displayService.DisplaysChanged += OnDisplaysChanged;
        _model.VariantToggled += OnVariantToggled;
        ScheduleDisplayRefresh();
    }

    // The color hotkey cycled this profile's preset on the keyboard-hook thread: queue the editor
    // update to the owner thread (same marshaling as ScheduleDisplayRefresh) and never throw into the hook.
    private void OnVariantToggled(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_ownerContext is not null && !ReferenceEquals(SynchronizationContext.Current, _ownerContext))
            {
                _ownerContext.Post(_ => FollowActiveVariant(), null);
                return;
            }

            FollowActiveVariant();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Color] Failed to follow the hotkey preset: {ex.Message}");
        }
    }

    // Reads the preset when it runs, so a burst of presses settles on the latest one. The hotkey has
    // already retargeted any Force Preview, so following only moves the editor.
    private void FollowActiveVariant()
    {
        if (!_disposed)
        {
            ShowVariant(_model.ActiveVariant);
        }
    }

    private void BuildDisplayViewModels()
    {
        // Inherit Primary calibration before GetOrCreateProfile fills any remaining gaps with disabled rows.
        _model.EnsureVariantInitialized(_editingVariant);

        foreach (var display in _displaySnapshot)
        {
            var profile = _model.GetOrCreateProfile(display.Id, _editingVariant);
            var displayVm = new DisplayColorSettingsViewModel(
                display,
                profile,
                _model,
                _colorService,
                () => IsEnabled && (_parentEnabledCheck?.Invoke() ?? true),
                _editingVariant,
                allowLiveUpdates: _allowLiveUpdates && _runtimeService is null);

            displayVm.Changed += OnDisplayChanged;
            DisplayViewModels.Add(displayVm);
        }
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _displayRefreshVersion);
        ScheduleDisplayRefresh();
    }

    private void ScheduleDisplayRefresh()
    {
        if (_disposed || Interlocked.CompareExchange(ref _displayRefreshScheduled, 1, 0) != 0)
        {
            return;
        }

        if (_ownerContext is not null && !ReferenceEquals(SynchronizationContext.Current, _ownerContext))
        {
            _ownerContext.Post(_ => StartDisplayRefresh(), null);
            return;
        }

        StartDisplayRefresh();
    }

    internal Task DisplayRefreshTask { get; private set; } = Task.CompletedTask;

    private void StartDisplayRefresh() => DisplayRefreshTask = RefreshDisplaySnapshotAsync();

    private async Task RefreshDisplaySnapshotAsync()
    {
        var version = Volatile.Read(ref _displayRefreshVersion);
        try
        {
            while (!_disposed)
            {
                version = Volatile.Read(ref _displayRefreshVersion);
                var displays = await _displayService.GetDisplaysAsync();
                if (_disposed) return;
                if (version != Volatile.Read(ref _displayRefreshVersion)) continue;

                _displaySnapshot = displays;
                RebuildDisplayViewModels();
                return;
            }
        }
        catch (Exception ex)
        {
            // Keep the last completed rows; another topology notification can retry the snapshot.
            System.Diagnostics.Debug.WriteLine($"[Color] Failed to refresh display rows: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _displayRefreshScheduled, 0);
            if (!_disposed && version != Volatile.Read(ref _displayRefreshVersion))
            {
                ScheduleDisplayRefresh();
            }
        }
    }

    private void RebuildDisplayViewModels()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var displayVm in DisplayViewModels)
        {
            displayVm.Changed -= OnDisplayChanged;
            displayVm.Dispose();
        }

        DisplayViewModels.Clear();
        BuildDisplayViewModels();

        // F-006: a rebuild is a topology change, not a user toggle — refresh the UI enabled-state ONLY.
        // The old NotifyMasterEnabledChanged() also wrote hardware, pushing a neutral gamma/DVC to disabled
        // displays the user never opted into. Owned displays are re-applied by ProfileActivationService's
        // DisplaySettingsChanged handler (whose plan-diff leaves never-owned displays untouched).
        foreach (var displayVm in DisplayViewModels)
        {
            displayVm.NotifyControlsEnabledChanged();
        }

        OnPropertyChanged(nameof(HasDisplays));
    }

    /// <summary>
    /// Force updates the master enabled state logic (used when parent profile toggle changes).
    /// UI-state only (F-006 / codex P1): the hardware transition for a master toggle is owned by
    /// ProfileActivationService's plan-diff, which restores only previously-owned displays.
    /// </summary>
    public void RefreshMasterEnabledState()
    {
        foreach (var displayVm in DisplayViewModels)
        {
            displayVm.NotifyControlsEnabledChanged();
        }
    }

    /// <summary>
    /// Collection of per-display ViewModels
    /// </summary>
    public ObservableCollection<DisplayColorSettingsViewModel> DisplayViewModels { get; } = [];

    /// <summary>
    /// Whether any displays are available
    /// </summary>
    public bool HasDisplays => DisplayViewModels.Count > 0;

    /// <summary>
    /// Master toggle for all color settings
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                _model.IsEnabled = value;

                if (!value)
                {
                    // Unchecking grays the whole panel (XAML MultiDataTrigger), so the user can no
                    // longer uncheck Force preview manually — cancel it here instead.
                    EndForcePreview();
                }

                // Notify all display VMs so they can update their AreControlsEnabled. UI-state only
                // (F-006 / codex P1): the Changed event routes to the activation worker, whose
                // plan-diff performs the hardware transition for previously-owned displays only.
                foreach (var displayVm in DisplayViewModels)
                {
                    displayVm.NotifyControlsEnabledChanged();
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool KeepColorActive
    {
        get => _keepColorActive;
        set
        {
            if (SetProperty(ref _keepColorActive, value))
            {
                _model.KeepColorActive = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasSecondary
    {
        get => _model.HasSecondary;
        set => SetOptionalVariantEnabled(ColorVariant.Secondary, value);
    }

    public bool HasTertiary
    {
        get => _model.HasTertiary;
        set => SetOptionalVariantEnabled(ColorVariant.Tertiary, value);
    }

    private bool IsVariantEnabled(ColorVariant variant) => variant switch
    {
        ColorVariant.Primary => true,
        ColorVariant.Secondary => HasSecondary,
        ColorVariant.Tertiary => HasTertiary,
        _ => false
    };

    private void SetOptionalVariantEnabled(ColorVariant variant, bool enabled)
    {
        if (IsVariantEnabled(variant) == enabled)
        {
            return;
        }

        var isSecondary = variant == ColorVariant.Secondary;

        if (enabled)
        {
            _model.EnsureVariantInitialized(variant);
        }

        if (isSecondary)
        {
            _model.HasSecondary = enabled;
        }
        else
        {
            _model.HasTertiary = enabled;
        }

        if (!enabled)
        {
            // The hotkey can retarget the preview independently of the editor selection.
            EndForcePreview();
            if (EditingVariant == variant)
            {
                EditingVariant = ColorVariant.Primary;
            }
        }

        OnPropertyChanged(isSecondary ? nameof(HasSecondary) : nameof(HasTertiary));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The preset shown in the per-display sliders. Changing it retargets an active Force Preview.</summary>
    public ColorVariant EditingVariant
    {
        get => _editingVariant;
        set
        {
            if (ShowVariant(value) && _isForcePreviewActive)
            {
                _runtimeService?.SetForcedColorPreview(_model, value);
            }
        }
    }

    // Points the sliders at an enabled preset. Returns false when nothing changed.
    private bool ShowVariant(ColorVariant value)
    {
        if (!IsVariantEnabled(value) || _editingVariant == value)
        {
            return false;
        }

        _editingVariant = value;
        OnPropertyChanged(nameof(EditingVariant));
        OnPropertyChanged(nameof(IsEditingPrimary));
        OnPropertyChanged(nameof(IsEditingSecondary));
        OnPropertyChanged(nameof(IsEditingTertiary));
        RebuildDisplayViewModels();
        return true;
    }

    // Radio-button projections of EditingVariant; clearing one is ignored because choosing another sets the preset.
    public bool IsEditingPrimary { get => _editingVariant == ColorVariant.Primary; set { if (value) EditingVariant = ColorVariant.Primary; } }
    public bool IsEditingSecondary { get => _editingVariant == ColorVariant.Secondary; set { if (value) EditingVariant = ColorVariant.Secondary; } }
    public bool IsEditingTertiary { get => _editingVariant == ColorVariant.Tertiary; set { if (value) EditingVariant = ColorVariant.Tertiary; } }

    // ── 30 s force preview (game profiles) ───────────────────────────────────────────────────────────
    // A game profile's colors normally apply only while its exe is foreground; while editing, the
    // GLOBAL fallback (the Window [Default] built-in) is what's on screen. The preview force-applies
    // the edited profile so slider drags are visible live, then auto-restores the
    // foreground-appropriate colors at expiry/cancel. The runtime owns the forced override (never
    // mutates runtime variant state); this VM owns only the checkbox + countdown.

    /// <summary>Game profiles only — the Window [Default] built-in's global color is already live
    /// (<c>allowLiveUpdates</c>).</summary>
    public bool IsForcePreviewAvailable => _runtimeService is not null && !_allowLiveUpdates;

    /// <summary>Bound two-way to the Force preview checkbox. Checking starts (or keeps) the preview;
    /// unchecking cancels it and restores the foreground-appropriate colors.</summary>
    public bool IsForcePreviewEnabled
    {
        get => _isForcePreviewActive;
        set
        {
            if (value)
            {
                StartForcePreview();
            }
            else
            {
                EndForcePreview();
            }
        }
    }

    /// <summary>Countdown label, e.g. "12s"; empty while no preview is active.</summary>
    public string ForcePreviewCountdown => _isForcePreviewActive ? $"{_forcePreviewRemainingSeconds}s" : string.Empty;

    private void StartForcePreview()
    {
        if (!IsForcePreviewAvailable || _isForcePreviewActive)
        {
            return;
        }

        _forcePreviewRemainingSeconds = ForcePreviewSeconds;
        _isForcePreviewActive = true;
        OnPropertyChanged(nameof(IsForcePreviewEnabled));
        OnPropertyChanged(nameof(IsForcePreviewAvailable));
        OnPropertyChanged(nameof(ForcePreviewCountdown));

        _runtimeService!.SetForcedColorPreview(_model, _editingVariant);

        // Headless tests drive PreviewTick directly — never construct a DispatcherTimer without a
        // dispatcher (it would throw, and its ticks could never fire anyway). DispatcherObject
        // affinity: created and stopped only on this (UI) thread.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            if (_previewCountdownTimer is null)
            {
                _previewCountdownTimer = new DispatcherTimer(DispatcherPriority.Render)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _previewCountdownTimer.Tick += (_, _) => PreviewTick();
            }

            _previewCountdownTimer.Stop();
            _previewCountdownTimer.Start();
        }
    }

    // Test seam for the countdown: one tick per second while a preview is active.
    internal void PreviewTick()
    {
        if (!_isForcePreviewActive)
        {
            return;
        }

        _forcePreviewRemainingSeconds--;
        OnPropertyChanged(nameof(ForcePreviewCountdown));

        if (_forcePreviewRemainingSeconds <= 0)
        {
            EndForcePreview(); // raises IsForcePreviewEnabled, so the bound checkbox unchecks
        }
    }

    /// <summary>Cancels the preview (manual uncheck, expiry, profile/color disable, selection change,
    /// removal, dispose) — the runtime then re-applies the foreground-appropriate colors.</summary>
    public void EndForcePreview()
    {
        if (!_isForcePreviewActive)
        {
            return;
        }

        _previewCountdownTimer?.Stop();
        _isForcePreviewActive = false;
        _forcePreviewRemainingSeconds = 0;
        OnPropertyChanged(nameof(IsForcePreviewEnabled));
        OnPropertyChanged(nameof(IsForcePreviewAvailable));
        OnPropertyChanged(nameof(ForcePreviewCountdown));

        // The service performs the auto-restore.
        _runtimeService?.ClearForcedColorPreview();
    }


    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        Changed?.Invoke(this, e);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Cancel an active preview BEFORE the _disposed fence: stop the countdown timer (this is
        // its dispatcher thread) and restore the foreground-appropriate colors.
        EndForcePreview();

        _disposed = true;
        _displayService.DisplaysChanged -= OnDisplaysChanged;
        _model.VariantToggled -= OnVariantToggled;

        foreach (var displayVm in DisplayViewModels)
        {
            displayVm.Changed -= OnDisplayChanged;
            displayVm.Dispose();
        }
    }
}
