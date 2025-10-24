using System;
using sWinShortcuts.Models;

namespace sWinShortcuts.ViewModels;

public sealed class AltMouseViewModel : ViewModelBase
{
    private readonly AltMouseSettings _model;
    private bool _isEnabled;
    private int _holdThresholdMilliseconds;

    public event EventHandler? Changed;

    public AltMouseViewModel(AltMouseSettings model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        LeftButton = new MouseButtonBindingViewModel(_model.LeftButton);
        RightButton = new MouseButtonBindingViewModel(_model.RightButton);
        MiddleButton = new MouseButtonBindingViewModel(_model.MiddleButton);

        _isEnabled = _model.IsEnabled;
        _holdThresholdMilliseconds = _model.HoldThresholdMilliseconds;

        LeftButton.Changed += OnChildChanged;
        RightButton.Changed += OnChildChanged;
        MiddleButton.Changed += OnChildChanged;
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                _model.IsEnabled = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public int HoldThresholdMilliseconds
    {
        get => _holdThresholdMilliseconds;
        set
        {
            var sanitized = Math.Max(10, value);
            if (SetProperty(ref _holdThresholdMilliseconds, sanitized))
            {
                _model.HoldThresholdMilliseconds = sanitized;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public MouseButtonBindingViewModel LeftButton { get; }

    public MouseButtonBindingViewModel RightButton { get; }

    public MouseButtonBindingViewModel MiddleButton { get; }

    private void OnChildChanged(object? sender, EventArgs e)
    {
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
