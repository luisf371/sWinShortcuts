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

    public Key? TapKey
    {
        get => _model.TapKey;
        set
        {
            if (_model.TapKey != value)
            {
                _model.TapKey = value;
                OnPropertyChanged();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public Key? HoldKey
    {
        get => _model.HoldKey;
        set
        {
            if (_model.HoldKey != value)
            {
                _model.HoldKey = value;
                OnPropertyChanged();
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
