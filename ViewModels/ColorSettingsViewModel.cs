using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace sWinShortcuts.ViewModels;

public sealed class ColorSettingsViewModel : ViewModelBase
{
    private readonly ColorSettings _model;
    private readonly IColorControlService _colorService;
    private readonly ObservableCollection<DisplayInfo> _displays = [];
    private bool _suppressUpdates;
    private DisplayInfo? _selectedDisplay;
    private int _brightness = DisplayColorProfile.DefaultBrightness;
    private int _contrast = DisplayColorProfile.DefaultContrast;
    private double _gamma = DisplayColorProfile.DefaultGamma;
    private int _digitalVibrance = DisplayColorProfile.DefaultDigitalVibrance;

    public event EventHandler? Changed;

    public ColorSettingsViewModel(ColorSettings model, IDisplayService displayService, IColorControlService colorService)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        ArgumentNullException.ThrowIfNull(displayService);
        _colorService = colorService ?? throw new ArgumentNullException(nameof(colorService));

        foreach (var display in displayService.GetDisplays())
        {
            _displays.Add(display);
        }

        ResetBrightnessCommand = new RelayCommand(() => Brightness = DisplayColorProfile.DefaultBrightness);
        ResetContrastCommand = new RelayCommand(() => Contrast = DisplayColorProfile.DefaultContrast);
        ResetGammaCommand = new RelayCommand(() => Gamma = DisplayColorProfile.DefaultGamma);
        ResetDigitalVibranceCommand = new RelayCommand(() => DigitalVibrance = DisplayColorProfile.DefaultDigitalVibrance);

        _suppressUpdates = true;
        SelectedDisplay = ResolveInitialSelection(_model.SelectedDisplayId);
        if (_selectedDisplay is null && _displays.Count > 0)
        {
            SelectedDisplay = _displays[0];
        }
        _suppressUpdates = false;

        if (_model.SelectedDisplayId == string.Empty && _selectedDisplay is not null)
        {
            _model.SelectedDisplayId = _selectedDisplay.Id;
        }

        if (_selectedDisplay is not null)
        {
            LoadDisplayProfile(_selectedDisplay);
        }
        else
        {
            LoadDisplayProfile(null);
        }
    }

    public ObservableCollection<DisplayInfo> Displays => _displays;

    public DisplayInfo? SelectedDisplay
    {
        get => _selectedDisplay;
        set
        {
            if (SetProperty(ref _selectedDisplay, value))
            {
                LoadDisplayProfile(value);
                OnPropertyChanged(nameof(HasDisplaySelection));

                if (!_suppressUpdates)
                {
                    _model.SelectedDisplayId = value?.Id ?? string.Empty;
                    ApplyToHardware();
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    public bool HasDisplaySelection => _selectedDisplay is not null;

    public int Brightness
    {
        get => _brightness;
        set
        {
            var clamped = ClampPercent(value);
            if (SetProperty(ref _brightness, clamped))
            {
                UpdateCurrentProfile(p => p.Brightness = clamped);
                ApplyToHardware();
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
                UpdateCurrentProfile(p => p.Contrast = clamped);
                ApplyToHardware();
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
                UpdateCurrentProfile(p => p.Gamma = clamped);
                ApplyToHardware();
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
                UpdateCurrentProfile(p => p.DigitalVibrance = clamped);
                ApplyToHardware();
            }
        }
    }

    public string SelectedDisplayId => SelectedDisplay?.Id ?? string.Empty;

    public ICommand ResetBrightnessCommand { get; }

    public ICommand ResetContrastCommand { get; }

    public ICommand ResetGammaCommand { get; }

    public ICommand ResetDigitalVibranceCommand { get; }

    private void LoadDisplayProfile(DisplayInfo? display)
    {
        var profile = display is null
            ? new DisplayColorProfile()
            : _model.GetOrCreateProfile(display.Id);

        _brightness = ClampPercent(profile.Brightness);
        _contrast = ClampPercent(profile.Contrast);
        _gamma = ClampGamma(profile.Gamma);
        _digitalVibrance = ClampDigitalVibrance(profile.DigitalVibrance);

        OnPropertyChanged(nameof(Brightness));
        OnPropertyChanged(nameof(Contrast));
        OnPropertyChanged(nameof(Gamma));
        OnPropertyChanged(nameof(DigitalVibrance));
    }

    private void UpdateCurrentProfile(Action<DisplayColorProfile> updater)
    {
        if (_suppressUpdates || _selectedDisplay is null)
        {
            return;
        }

        var profile = _model.GetOrCreateProfile(_selectedDisplay.Id);
        updater(profile);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private DisplayInfo? ResolveInitialSelection(string storedDisplayId)
    {
        if (string.IsNullOrWhiteSpace(storedDisplayId))
        {
            return _displays.FirstOrDefault();
        }

        return _displays.FirstOrDefault(d =>
            string.Equals(d.Id, storedDisplayId, StringComparison.OrdinalIgnoreCase));
    }

    private static int ClampPercent(int value) => Math.Clamp(value, 0, 100);

    private static int ClampDigitalVibrance(int value) => Math.Clamp(value, DisplayColorProfile.DefaultDigitalVibrance, 100);

    private static double ClampGamma(double value) => Math.Clamp(value, 0.5, 3.0);

    private void ApplyToHardware()
    {
        if (_selectedDisplay is null)
        {
            return;
        }

        _colorService.Apply(_selectedDisplay, new DisplayColorProfile
        {
            DisplayId = _selectedDisplay.Id,
            Brightness = _brightness,
            Contrast = _contrast,
            Gamma = _gamma,
            DigitalVibrance = _digitalVibrance
        });
    }
}
