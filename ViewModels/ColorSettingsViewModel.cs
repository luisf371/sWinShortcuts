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
    private readonly bool _allowLiveUpdates;
    private readonly Func<bool>? _parentEnabledCheck;
    // Owns the preview UI state (checkbox + countdown); ProfileActivationService owns the forced
    // runtime state (what the worker applies). Null (tests / no runtime) hides the feature.
    private readonly IProfileRuntimeService? _runtimeService;
    private bool _isEnabled;
    private bool _disposed;
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
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));
        _allowLiveUpdates = allowLiveUpdates;
        _parentEnabledCheck = parentEnabledCheck;
        _runtimeService = profileRuntimeService;

        _isEnabled = model.IsEnabled;

        BuildDisplayViewModels();

        // Rebuild when monitors are hot-plugged/removed. DisplayService is a singleton that outlives
        // this (transient) VM, so we MUST unsubscribe in Dispose or the handler leaks.
        _displayService.DisplaysChanged += OnDisplaysChanged;
    }

    private void BuildDisplayViewModels()
    {
        if (_editingVariant == ColorVariant.Secondary)
        {
            // Seed any missing Secondary display from Primary BEFORE GetOrCreateProfile below would otherwise
            // materialize a blank (disabled) one that a toggle could later apply as a neutral plan (codex).
            _model.EnsureSecondaryInitialized();
        }

        foreach (var display in _displayService.GetDisplays())
        {
            var profile = _model.GetOrCreateProfile(display.Id, _editingVariant);
            var displayVm = new DisplayColorSettingsViewModel(
                display,
                profile,
                _model,
                _colorService,
                () => IsEnabled && (_parentEnabledCheck?.Invoke() ?? true),
                _editingVariant,
                _allowLiveUpdates);

            displayVm.Changed += OnDisplayChanged;
            DisplayViewModels.Add(displayVm);
        }
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(RebuildDisplayViewModels);
            return;
        }

        RebuildDisplayViewModels();
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

    // ── Primary / Secondary variant editing ──────────────────────────────────────────────────────────
    // Both presets share ONE set of per-display sliders; IsEditingSecondary chooses which variant they bind
    // to. Runtime application of a variant is driven by the global toggle key, not by the editor.

    /// <summary>Whether a Secondary preset is configured for this profile (the toggle key is a no-op without it).</summary>
    public bool HasSecondary
    {
        get => _model.HasSecondary;
        set
        {
            if (_model.HasSecondary == value)
            {
                return;
            }

            _model.HasSecondary = value;

            if (value)
            {
                // Seed Secondary from Primary so a freshly-enabled preset is a COPY of the current look, not a
                // blank/neutral plan the toggle would apply (codex CRITICAL). No-op if already populated.
                _model.EnsureSecondaryInitialized();
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEditSecondary));

            if (!value)
            {
                // Turning Secondary off snaps editing AND the applied preset back to Primary.
                IsEditingSecondary = false;
                _model.SetActiveVariant(ColorVariant.Primary);
                EndForcePreview(); // a preset that no longer exists has nothing to preview
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool CanEditSecondary => _model.HasSecondary;

    /// <summary>Which preset the per-display sliders currently edit. Switching rebinds them to that variant's
    /// saved values (no hardware apply on switch — the runtime toggle key drives live changes).</summary>
    public bool IsEditingSecondary
    {
        get => _editingVariant == ColorVariant.Secondary;
        set
        {
            var target = value ? ColorVariant.Secondary : ColorVariant.Primary;
            if (_editingVariant == target)
            {
                return;
            }

            _editingVariant = target;
            OnPropertyChanged();
            RebuildDisplayViewModels();

            if (_isForcePreviewActive)
            {
                // The preview follows the Edit-preset selection: flipping the segmented toggle
                // during a preview switches the previewed variant live.
                _runtimeService?.SetForcedColorPreview(_model, target);
            }
        }
    }

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

        foreach (var displayVm in DisplayViewModels)
        {
            displayVm.Changed -= OnDisplayChanged;
            displayVm.Dispose();
        }
    }
}
