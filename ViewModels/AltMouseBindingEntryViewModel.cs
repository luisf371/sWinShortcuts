using System;
using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using sWinShortcuts.Models;

namespace sWinShortcuts.ViewModels;

public sealed partial class AltMouseBindingEntryViewModel : ViewModelBase
{
    public event EventHandler? Changed;

    public AltMouseBindingEntryViewModel(InputTrigger source, Key? tapKey, Key? holdKey)
    {
        this.source = source;
        this.tapKey = tapKey ?? Key.None;
        _holdKey = CanHold ? holdKey ?? Key.None : Key.None;
    }

    [ObservableProperty]
    private InputTrigger source;

    [ObservableProperty]
    private Key tapKey;

    private Key _holdKey;
    public Key HoldKey
    {
        get => _holdKey;
        set
        {
            if (SetProperty(ref _holdKey, CanHold ? value : Key.None))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool CanHold => Source.Kind == InputTriggerKind.MouseButton;

    private IEnumerable<InputTrigger> _selectableSources = [];
    public IEnumerable<InputTrigger> SelectableSources
    {
        get => _selectableSources;
        set => SetProperty(ref _selectableSources, value);
    }

    partial void OnSourceChanged(InputTrigger value)
    {
        if (!CanHold)
        {
            SetProperty(ref _holdKey, Key.None, nameof(HoldKey));
        }

        OnPropertyChanged(nameof(CanHold));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnTapKeyChanged(Key value) => Changed?.Invoke(this, EventArgs.Empty);
}
