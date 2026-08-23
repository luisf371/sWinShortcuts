using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using sWinShortcuts.Models;

namespace sWinShortcuts.ViewModels;

public sealed class AltKeyboardViewModel : ViewModelBase
{
    private readonly AltKeyboardSettings _model;
    private bool _isEnabled;
    private int _holdThresholdMilliseconds;

    public event EventHandler? Changed;

    public AltKeyboardViewModel(AltKeyboardSettings model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));

        Bindings = new ObservableCollection<AltKeyboardBindingEntryViewModel>(
            _model.Bindings.Select(pair => new AltKeyboardBindingEntryViewModel(pair.Key, pair.Value.TapKey, pair.Value.HoldKey)));
        Bindings.CollectionChanged += OnBindingsChanged;
        foreach (var entry in Bindings)
        {
            AttachEntry(entry);
        }

        _isEnabled = _model.IsEnabled;
        _holdThresholdMilliseconds = _model.HoldThresholdMilliseconds;

        ResetHoldThresholdCommand = new RelayCommand(
            () => HoldThresholdMilliseconds = AltKeyboardSettings.DefaultHoldThresholdMilliseconds);
    }

    public ICommand ResetHoldThresholdCommand { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                _model.IsEnabled = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public int HoldThresholdMilliseconds
    {
        get => _holdThresholdMilliseconds;
        set
        {
            var sanitized = Math.Max(10, value);
            if (SetProperty(ref _holdThresholdMilliseconds, sanitized))
            {
                _model.HoldThresholdMilliseconds = sanitized;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public ObservableCollection<AltKeyboardBindingEntryViewModel> Bindings { get; }

    private void OnBindingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (AltKeyboardBindingEntryViewModel item in e.NewItems)
            {
                AttachEntry(item);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (AltKeyboardBindingEntryViewModel item in e.OldItems)
            {
                DetachEntry(item);
            }
        }

        SyncToModel();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void AttachEntry(AltKeyboardBindingEntryViewModel entry)
    {
        entry.Changed += OnChildChanged;
    }

    private void DetachEntry(AltKeyboardBindingEntryViewModel entry)
    {
        entry.Changed -= OnChildChanged;
    }

    private void OnChildChanged(object? sender, EventArgs e)
    {
        SyncToModel();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SyncToModel()
    {
        // Build-and-swap, never Clear+rebuild in place: the pool-thread autosave serializer and the
        // hook thread read this dictionary concurrently with UI edits.
        var bindings = new System.Collections.Generic.Dictionary<System.Windows.Input.Key, AltKeyboardBinding>();
        foreach (var entry in Bindings)
        {
            bindings[entry.TriggerKey] = new AltKeyboardBinding
            {
                TapKey = entry.TapKey == System.Windows.Input.Key.None ? null : entry.TapKey,
                HoldKey = entry.HoldKey == System.Windows.Input.Key.None ? null : entry.HoldKey
            };
        }

        _model.Bindings = bindings;
    }
}
