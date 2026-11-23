using System;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using sWinShortcuts.Models;

namespace sWinShortcuts.ViewModels;

public sealed partial class AltShortcutBindingEntryViewModel : ViewModelBase
{
    // Carries the runtime family (or families) the edit touched.
    public event EventHandler<ProfileChangeKind>? Changed;

    public AltShortcutBindingEntryViewModel(InputTrigger source, Key? tapKey, Key? holdKey)
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
                Changed?.Invoke(this, AltShortcutViewModel.Family(Source));
            }
        }
    }

    // Wheel triggers fire immediately, so they support Tap only.
    public bool CanHold => Source.Kind is InputTriggerKind.KeyboardKey or InputTriggerKind.MouseButton;

    // Set by the owning AltShortcutViewModel: the capture box rejects a trigger another row already uses.
    private Func<KeyChord, string?>? _sourceValidator;
    public Func<KeyChord, string?>? SourceValidator
    {
        get => _sourceValidator;
        set => SetProperty(ref _sourceValidator, value);
    }

    partial void OnSourceChanged(InputTrigger oldValue, InputTrigger newValue)
    {
        // Clear Hold before reporting, so the rebuilt model never holds a Hold target for a wheel.
        if (!CanHold)
        {
            SetProperty(ref _holdKey, Key.None, nameof(HoldKey));
        }

        OnPropertyChanged(nameof(CanHold));
        // Both families on a cross-family move, so the old route's pending press is cancelled too.
        Changed?.Invoke(this, AltShortcutViewModel.Family(oldValue) | AltShortcutViewModel.Family(newValue));
    }

    partial void OnTapKeyChanged(Key value) => Changed?.Invoke(this, AltShortcutViewModel.Family(Source));
}
