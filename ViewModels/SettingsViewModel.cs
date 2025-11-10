using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace sWinShortcuts.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private bool _startWithWindows;
    private bool _startAsAdmin;

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (_startWithWindows != value)
            {
                _startWithWindows = value;
                if (!value)
                {
                    StartAsAdmin = false; // keep consistent
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanChooseAdmin));
            }
        }
    }

    public bool StartAsAdmin
    {
        get => _startAsAdmin;
        set
        {
            if (_startAsAdmin != value)
            {
                _startAsAdmin = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CanChooseAdmin => StartWithWindows;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

