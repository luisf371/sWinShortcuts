using System;
using System.Windows.Input;
using sWinShortcuts.Models;

namespace sWinShortcuts.ViewModels;

public sealed class MouseButtonBindingViewModel : ViewModelBase
{
    private readonly MouseButtonBinding _model;

    public MouseButtonBindingViewModel(MouseButtonBinding model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    public event EventHandler? Changed;

    public Key TapKey
    {
        get => _model.TapKey ?? Key.None;
        set
        {
            var newValue = value == Key.None ? null : (Key?)value;
            if (_model.TapKey != newValue)
            {
                _model.TapKey = newValue;
                OnPropertyChanged();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public Key HoldKey
    {
        get => _model.HoldKey ?? Key.None;
        set
        {
            var newValue = value == Key.None ? null : (Key?)value;
            if (_model.HoldKey != newValue)
            {
                _model.HoldKey = newValue;
                OnPropertyChanged();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}