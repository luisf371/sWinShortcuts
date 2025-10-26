using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using sWinShortcuts.Models;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.ViewModels;

public sealed partial class AltMouseBindingEntryViewModel : ViewModelBase
{
    public event EventHandler? Changed;

    public AltMouseBindingEntryViewModel()
    {
        button = MouseButton.Left;
        tapKey = Key.None;
        holdKey = Key.None;
    }

    public AltMouseBindingEntryViewModel(MouseButton button, Key? tapKey, Key? holdKey)
    {
        this.button = button;
        this.tapKey = tapKey ?? Key.None;
        this.holdKey = holdKey ?? Key.None;
    }

    [ObservableProperty]
    private MouseButton button;

    [ObservableProperty]
    private Key tapKey;

    [ObservableProperty]
    private Key holdKey;

    partial void OnButtonChanged(MouseButton value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnTapKeyChanged(Key value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnHoldKeyChanged(Key value) => Changed?.Invoke(this, EventArgs.Empty);
}