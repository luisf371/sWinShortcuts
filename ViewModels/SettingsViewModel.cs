using System.ComponentModel;
using System.Runtime.CompilerServices;
using sWinShortcuts.Services;

namespace sWinShortcuts.ViewModels;

public sealed class SettingsViewModel(ILoggerService loggerService, IInputHookService inputHookService) : INotifyPropertyChanged
{
    private readonly ILoggerService _loggerService = loggerService;
    private readonly IInputHookService _inputHookService = inputHookService;
    private bool _startWithWindows;
    private bool _startAsAdmin;
    private bool _enableDebugLogging;
    private bool _hookWatchdogEnabled = true;

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

    public bool EnableDebugLogging
    {
        get => _enableDebugLogging;
        set
        {
            if (_enableDebugLogging != value)
            {
                _enableDebugLogging = value;
                _loggerService.IsEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    // Applies live, same pattern as EnableDebugLogging (the service reacts on its next watchdog
    // period); persistence happens on Save in SettingsWindow.
    public bool HookWatchdogEnabled
    {
        get => _hookWatchdogEnabled;
        set
        {
            if (_hookWatchdogEnabled != value)
            {
                _hookWatchdogEnabled = value;
                _inputHookService.HookWatchdogEnabled = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CanChooseAdmin => StartWithWindows;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

