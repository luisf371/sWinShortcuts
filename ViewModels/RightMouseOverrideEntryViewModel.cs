using System;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;
using sWinShortcuts.Models;

namespace sWinShortcuts.ViewModels;

public sealed partial class RightMouseOverrideEntryViewModel : ViewModelBase
{
    public event EventHandler? Changed;

    public RightMouseOverrideEntryViewModel()
    {
        sourceKey = Key.A;
        targetKey = Key.A;
        suppressOriginalKey = true;
    }

    public RightMouseOverrideEntryViewModel(RightMouseOverrideEntry model)
    {
        sourceKey = model.SourceKey;
        targetKey = model.TargetKey;
        suppressOriginalKey = model.SuppressOriginalKey;
    }

    [ObservableProperty]
    private Key sourceKey;

    [ObservableProperty]
    private Key targetKey;

    [ObservableProperty]
    private bool suppressOriginalKey;

    partial void OnSourceKeyChanged(Key value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnTargetKeyChanged(Key value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnSuppressOriginalKeyChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    public void UpdateModel(RightMouseOverrideEntry model)
    {
        model.SourceKey = SourceKey;
        model.TargetKey = TargetKey;
        model.SuppressOriginalKey = SuppressOriginalKey;
    }
}
