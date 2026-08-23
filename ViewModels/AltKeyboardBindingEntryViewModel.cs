using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace sWinShortcuts.ViewModels;

public sealed partial class AltKeyboardBindingEntryViewModel : ViewModelBase
{
    public event EventHandler? Changed;

    public AltKeyboardBindingEntryViewModel(Key triggerKey, Key? tapKey, Key? holdKey)
    {
        this.triggerKey = triggerKey;
        this.tapKey = tapKey ?? Key.None;
        this.holdKey = holdKey ?? Key.None;
    }

    [ObservableProperty]
    private Key triggerKey;

    [ObservableProperty]
    private Key tapKey;

    [ObservableProperty]
    private Key holdKey;

    partial void OnTriggerKeyChanged(Key value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnTapKeyChanged(Key value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnHoldKeyChanged(Key value) => Changed?.Invoke(this, EventArgs.Empty);
}
