using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace sWinShortcuts.ViewModels;

/// <summary>
/// ViewModel for the Color Settings section.
/// Manages the master enable toggle and a collection of per-display ViewModels.
/// </summary>
public sealed class ColorSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly ColorSettings _model;
    private readonly IColorControlService _colorService;
    private readonly IDisplayService _displayService;
    private readonly bool _allowLiveUpdates;
    private readonly Func<bool>? _parentEnabledCheck;
    private bool _isEnabled;
    private bool _disposed;
    private ColorVariant _editingVariant = ColorVariant.Primary;
    private readonly bool _canEditToggleKey;

    public event EventHandler? Changed;

    public ColorSettingsViewModel(
        ColorSettings model,
        IDisplayService displayService,
        IColorControlService colorService,
        bool allowLiveUpdates = false,
        Func<bool>? parentEnabledCheck = null,
        bool canEditToggleKey = false)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _displayService = displayService ?? throw new ArgumentNullException(nameof(displayService));
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));
        _allowLiveUpdates = allowLiveUpdates;
        _parentEnabledCheck = parentEnabledCheck;
        _canEditToggleKey = canEditToggleKey;

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
    /// Force updates the master enabled state logic (used when parent profile toggle changes)
    /// </summary>
    public void RefreshMasterEnabledState()
    {
        foreach (var displayVm in DisplayViewModels)
        {
            displayVm.NotifyMasterEnabledChanged();
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

                // Notify all display VMs so they can update their AreControlsEnabled
                foreach (var displayVm in DisplayViewModels)
                {
                    displayVm.NotifyMasterEnabledChanged();
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
        }
    }

    /// <summary>The GLOBAL color-toggle key. Editable only on the global Color profile
    /// (<see cref="CanEditToggleKey"/>); persisted and re-read by the hook on the next foreground change.</summary>
    public Key? ToggleKey
    {
        get => _model.ToggleKey;
        set
        {
            if (_model.ToggleKey == value)
            {
                return;
            }

            _model.ToggleKey = value;
            OnPropertyChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True only on the global Color profile editor — the single place the global key is set.</summary>
    public bool CanEditToggleKey => _canEditToggleKey;

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

        _disposed = true;
        _displayService.DisplaysChanged -= OnDisplaysChanged;

        foreach (var displayVm in DisplayViewModels)
        {
            displayVm.Changed -= OnDisplayChanged;
            displayVm.Dispose();
        }
    }
}
