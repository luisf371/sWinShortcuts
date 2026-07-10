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
public sealed class DisplayColorSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly DisplayColorProfile _profile;
    private readonly ColorSettings _colorSettings;
    private readonly DisplayInfo _displayInfo;
    private readonly IColorControlService _colorService;
    private readonly Func<bool> _isMasterEnabled;
    private readonly bool _allowLiveUpdates;
    private System.Windows.Threading.DispatcherTimer? _applyDebounceTimer;

    private bool _isEnabled;
    private int _brightness;
    private int _contrast;
    private double _gamma;
    private int _digitalVibrance;

    public event EventHandler? Changed;

    public DisplayColorSettingsViewModel(
        DisplayInfo displayInfo,
        DisplayColorProfile profile,
        ColorSettings colorSettings,
        IColorControlService colorService,
        Func<bool> isMasterEnabled,
        bool allowLiveUpdates = false)
    {
        _displayInfo = displayInfo ?? throw new ArgumentNullException(nameof(displayInfo));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _colorSettings = colorSettings ?? throw new ArgumentNullException(nameof(colorSettings));
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
                _colorSettings.UpdateProfile(_displayInfo.Id, p => p.IsEnabled = value); // F-018: under _sync
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
                _colorSettings.UpdateProfile(_displayInfo.Id, p => p.Brightness = clamped); // F-018: under _sync
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
                _colorSettings.UpdateProfile(_displayInfo.Id, p => p.Contrast = clamped); // F-018: under _sync
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
                _colorSettings.UpdateProfile(_displayInfo.Id, p => p.Gamma = clamped); // F-018: under _sync
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
                _colorSettings.UpdateProfile(_displayInfo.Id, p => p.DigitalVibrance = clamped); // F-018: under _sync
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

    // F-006: UI-state ONLY — refresh whether the sliders are enabled, with NO hardware side effect. Used by
    // a display-list rebuild (hot-plug): re-enumerating monitors must not write a neutral gamma/DVC to
    // displays the user never opted into. Topology re-apply for owned displays is handled by
    // ProfileActivationService's DisplaySettingsChanged handler (its plan-diff skips never-owned displays).
    public void NotifyControlsEnabledChanged()
    {
        OnPropertyChanged(nameof(AreControlsEnabled));
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

        // C9: debounce the synchronous GDI/NvAPI write so dragging a slider doesn't fire one hardware
        // apply per tick. The _profile.* field write already happened in the setter (persistence stays
        // immediate); only the hardware apply is coalesced, and the trailing value is always applied.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ApplyToHardwareNow();
            return;
        }

        if (_applyDebounceTimer is null)
        {
            _applyDebounceTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            _applyDebounceTimer.Tick += (_, _) =>
            {
                _applyDebounceTimer!.Stop();
                ApplyToHardwareNow();
            };
        }

        _applyDebounceTimer.Stop();
        _applyDebounceTimer.Start();
    }

    private void ApplyToHardwareNow()
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

    public void Dispose()
    {
        _applyDebounceTimer?.Stop();
        _applyDebounceTimer = null;
    }

    private static int ClampPercent(int value) => Math.Clamp(value, 0, 100);
    private static int ClampDigitalVibrance(int value) => Math.Clamp(value, DisplayColorProfile.DefaultDigitalVibrance, 100);
    private static double ClampGamma(double value) => Math.Clamp(value, 0.5, 3.0);
}
