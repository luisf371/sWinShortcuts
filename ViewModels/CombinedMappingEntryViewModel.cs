using System;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;

namespace sWinShortcuts.ViewModels;

public sealed partial class CombinedMappingEntryViewModel : ViewModelBase
{
    public event EventHandler? Changed;

    public CombinedMappingEntryViewModel()
    {
        sourceKey = Key.A;
        targetKey = Key.A;
        suppressOriginalKey = true;
        rightClickOnly = false;
    }

    [ObservableProperty]
    private Key sourceKey;

    [ObservableProperty]
    private Key targetKey;

    [ObservableProperty]
    private bool suppressOriginalKey;

    [ObservableProperty]
    private bool rightClickOnly;

    partial void OnSourceKeyChanged(Key value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnTargetKeyChanged(Key value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnSuppressOriginalKeyChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnRightClickOnlyChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);
}
