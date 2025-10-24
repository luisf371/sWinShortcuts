using System;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;

namespace sWinShortcuts.ViewModels;

public sealed partial class RightMouseOverrideEntryViewModel : ViewModelBase
{
    public event EventHandler? Changed;

    public RightMouseOverrideEntryViewModel()
    {
        sourceKey = Key.A;
        targetKey = Key.A;
    }

    public RightMouseOverrideEntryViewModel(Key source, Key target)
    {
        sourceKey = source;
        targetKey = target;
    }

    [ObservableProperty]
    private Key sourceKey;

    [ObservableProperty]
    private Key targetKey;

    partial void OnSourceKeyChanged(Key value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnTargetKeyChanged(Key value) => Changed?.Invoke(this, EventArgs.Empty);
}
