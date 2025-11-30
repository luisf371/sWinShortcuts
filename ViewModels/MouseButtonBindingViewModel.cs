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
            // System.Diagnostics.Debug.WriteLine($"[DEBUG] TapKey setter called with value: {value}");
            var newValue = value == Key.None ? null : (Key?)value;
            if (_model.TapKey != newValue)
            {
                // System.Diagnostics.Debug.WriteLine($"[DEBUG] TapKey changing from {(_model.TapKey?.ToString() ?? "NULL")} to {(newValue?.ToString() ?? "NULL")}");
                _model.TapKey = newValue;
                OnPropertyChanged();
                Changed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // System.Diagnostics.Debug.WriteLine($"[DEBUG] TapKey value unchanged: {value}");
            }
        }
    }

    public Key HoldKey
    {
        get => _model.HoldKey ?? Key.None;
        set
        {
            // System.Diagnostics.Debug.WriteLine($"[DEBUG] HoldKey setter called with value: {value}");
            var newValue = value == Key.None ? null : (Key?)value;
            if (_model.HoldKey != newValue)
            {
                // System.Diagnostics.Debug.WriteLine($"[DEBUG] HoldKey changing from {(_model.HoldKey?.ToString() ?? "NULL")} to {(newValue?.ToString() ?? "NULL")}");
                _model.HoldKey = newValue;
                OnPropertyChanged();
                Changed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // System.Diagnostics.Debug.WriteLine($"[DEBUG] HoldKey value unchanged: {value}");
            }
        }
    }
}