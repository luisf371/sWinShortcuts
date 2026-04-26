using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace sWinShortcuts.ViewModels;

/// <summary>
/// ViewModel for a single display's color settings.
/// Each monitor gets its own instance with independent sliders.
/// </summary>
public sealed class DisplayColorSettingsViewModel : ViewModelBase
{
    private readonly DisplayColorProfile _profile;
    private readonly DisplayInfo _displayInfo;
    private readonly IColorControlService _colorService;
    private readonly Func<bool> _isMasterEnabled;
    private readonly bool _allowLiveUpdates;

    private bool _isEnabled;
    private int _brightness;
    private int _contrast;
    private double _gamma;
    private int _digitalVibrance;

    public event EventHandler? Changed;

    public DisplayColorSettingsViewModel(
        DisplayInfo displayInfo,
        DisplayColorProfile profile,
        IColorControlService colorService,
        Func<bool> isMasterEnabled,
        bool allowLiveUpdates = false)
    {
        _displayInfo = displayInfo ?? throw new ArgumentNullException(nameof(displayInfo));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));
        _isMasterEnabled = isMasterEnabled ?? throw new ArgumentNullException(nameof(isMasterEnabled));
        _allowLiveUpdates = allowLiveUpdates;

        // Load initial values from profile
        _isEnabled = profile.IsEnabled;
        _brightness = ClampPercent(profile.Brightness);
        _contrast = ClampPercent(profile.Contrast);
        _gamma = ClampGamma(profile.Gamma);
        _digitalVibrance = ClampDigitalVibrance(profile.DigitalVibrance);

        // Initialize commands
        ResetBrightnessCommand = new RelayCommand(() => Brightness = DisplayColorProfile.DefaultBrightness);
        ResetContrastCommand = new RelayCommand(() => Contrast = DisplayColorProfile.DefaultContrast);
        ResetGammaCommand = new RelayCommand(() => Gamma = DisplayColorProfile.DefaultGamma);
        ResetDigitalVibranceCommand = new RelayCommand(() => DigitalVibrance = DisplayColorProfile.DefaultDigitalVibrance);
        ResetAllCommand = new RelayCommand(ResetAll);
    }

    /// <summary>
    /// The display ID (e.g., \\.\DISPLAY1)
    /// </summary>
    public string DisplayId => _displayInfo.Id;

    /// <summary>
    /// Human-readable display name (e.g., "Dell U2720Q (Primary)")
    /// </summary>
    public string DisplayName => _displayInfo.Name;

    /// <summary>
    /// Whether this is the primary monitor
    /// </summary>
    public bool IsPrimary => _displayInfo.IsPrimary;

    /// <summary>
    /// Whether this individual display's color adjustments are enabled
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                _profile.IsEnabled = value;
                OnPropertyChanged(nameof(AreControlsEnabled));
                
                // When toggled OFF: revert to default colors
                // When toggled ON: apply saved slider values
                ApplyToHardwareOrRevert();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Whether the slider controls should be enabled (master AND individual enabled)
    /// </summary>
    public bool AreControlsEnabled => _isMasterEnabled() && _isEnabled;

    public int Brightness
    {
        get => _brightness;
        set
        {
            var clamped = ClampPercent(value);
            if (SetProperty(ref _brightness, clamped))
            {
                _profile.Brightness = clamped;
                ApplyToHardware();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public int Contrast
    {
        get => _contrast;
        set
        {
            var clamped = ClampPercent(value);
            if (SetProperty(ref _contrast, clamped))
            {
                _profile.Contrast = clamped;
                ApplyToHardware();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public double Gamma
    {
        get => _gamma;
        set
        {
            var clamped = ClampGamma(value);
            if (SetProperty(ref _gamma, clamped))
            {
                _profile.Gamma = clamped;
                ApplyToHardware();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public int DigitalVibrance
    {
        get => _digitalVibrance;
        set
        {
            var clamped = ClampDigitalVibrance(value);
            if (SetProperty(ref _digitalVibrance, clamped))
            {
                _profile.DigitalVibrance = clamped;
                ApplyToHardware();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public ICommand ResetBrightnessCommand { get; }
    public ICommand ResetContrastCommand { get; }
    public ICommand ResetGammaCommand { get; }
    public ICommand ResetDigitalVibranceCommand { get; }
    public ICommand ResetAllCommand { get; }

    /// <summary>
    /// Called when the master toggle changes to update the AreControlsEnabled property
    /// </summary>
    public void NotifyMasterEnabledChanged()
    {
        OnPropertyChanged(nameof(AreControlsEnabled));
        ApplyToHardwareOrRevert();
    }

    private void ResetAll()
    {
        Brightness = DisplayColorProfile.DefaultBrightness;
        Contrast = DisplayColorProfile.DefaultContrast;
        Gamma = DisplayColorProfile.DefaultGamma;
        DigitalVibrance = DisplayColorProfile.DefaultDigitalVibrance;
    }

    /// <summary>
    /// Applies saved slider values to hardware when enabled, or reverts to defaults when disabled.
    /// This allows the toggle to act as a quick on/off switch without modifying slider values.
    /// </summary>
    private void ApplyToHardwareOrRevert()
    {
        if (!_allowLiveUpdates)
        {
            return;
        }

        if (_isMasterEnabled() && _isEnabled)
        {
            // Apply saved slider values
            _colorService.Apply(_displayInfo, new DisplayColorProfile
            {
                DisplayId = _displayInfo.Id,
                IsEnabled = true,
                Brightness = _brightness,
                Contrast = _contrast,
                Gamma = _gamma,
                DigitalVibrance = _digitalVibrance
            });
        }
        else
        {
            // Revert to default values (neutral settings)
            _colorService.Apply(_displayInfo, new DisplayColorProfile
            {
                DisplayId = _displayInfo.Id,
                IsEnabled = false,
                Brightness = DisplayColorProfile.DefaultBrightness,
                Contrast = DisplayColorProfile.DefaultContrast,
                Gamma = DisplayColorProfile.DefaultGamma,
                DigitalVibrance = DisplayColorProfile.DefaultDigitalVibrance
            });
        }
    }

    private void ApplyToHardware()
    {
        if (!_allowLiveUpdates || !_isMasterEnabled() || !_isEnabled)
        {
            return;
        }

        _colorService.Apply(_displayInfo, new DisplayColorProfile
        {
            DisplayId = _displayInfo.Id,
            IsEnabled = _isEnabled,
            Brightness = _brightness,
            Contrast = _contrast,
            Gamma = _gamma,
            DigitalVibrance = _digitalVibrance
        });
    }

    private static int ClampPercent(int value) => Math.Clamp(value, 0, 100);
    private static int ClampDigitalVibrance(int value) => Math.Clamp(value, DisplayColorProfile.DefaultDigitalVibrance, 100);
    private static double ClampGamma(double value) => Math.Clamp(value, 0.5, 3.0);
}
