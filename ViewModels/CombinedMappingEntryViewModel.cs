using System;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;
using System.Collections.Generic;
using sWinShortcuts.Models;

namespace sWinShortcuts.ViewModels;

public sealed partial class CombinedMappingEntryViewModel : ViewModelBase
{
    public event EventHandler? Changed;

    public CombinedMappingEntryViewModel()
    {
        source = InputTrigger.FromKey(Key.A);
        targetKey = Key.A;
        suppressOriginalKey = true;
        rightClickOnly = false;
    }

    [ObservableProperty]
    private InputTrigger source;

    [ObservableProperty]
    private Key targetKey;

    [ObservableProperty]
    private bool suppressOriginalKey;

    [ObservableProperty]
    private bool rightClickOnly;

    private IEnumerable<InputTrigger> _selectableSources = [];
    public IEnumerable<InputTrigger> SelectableSources
    {
        get => _selectableSources;
        set => SetProperty(ref _selectableSources, value);
    }

    partial void OnSourceChanged(InputTrigger value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnTargetKeyChanged(Key value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnSuppressOriginalKeyChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnRightClickOnlyChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);
}
