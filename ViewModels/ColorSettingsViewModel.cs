using System;
using System.Collections.ObjectModel;
using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace sWinShortcuts.ViewModels;

/// <summary>
/// ViewModel for the Color Settings section.
/// Manages the master enable toggle and a collection of per-display ViewModels.
/// </summary>
public sealed class ColorSettingsViewModel : ViewModelBase
{
    private readonly ColorSettings _model;
    private readonly IColorControlService _colorService;
    private readonly IDisplayService _displayService;
    private readonly bool _allowLiveUpdates;
    private bool _isEnabled;

    public event EventHandler? Changed;

    public ColorSettingsViewModel(
        ColorSettings model,
        IDisplayService displayService,
        IColorControlService colorService,
        bool allowLiveUpdates = false,
        Func<bool>? parentEnabledCheck = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _displayService = displayService ?? throw new ArgumentNullException(nameof(displayService));
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));
        _allowLiveUpdates = allowLiveUpdates;

        _isEnabled = model.IsEnabled;

        // Create a ViewModel for each detected display
        foreach (var display in _displayService.GetDisplays())
        {
            var profile = _model.GetOrCreateProfile(display.Id);
            var displayVm = new DisplayColorSettingsViewModel(
                display,
                profile,
                _colorService,
                () => IsEnabled && (parentEnabledCheck?.Invoke() ?? true),
                _allowLiveUpdates);

            displayVm.Changed += OnDisplayChanged;
            DisplayViewModels.Add(displayVm);
        }
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

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        Changed?.Invoke(this, e);
    }
}
