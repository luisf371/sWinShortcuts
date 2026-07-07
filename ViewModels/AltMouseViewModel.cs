using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using sWinShortcuts.Models;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.ViewModels;

public sealed class AltMouseViewModel : ViewModelBase
{
    private readonly AltMouseSettings _model;
    private bool _isEnabled;
    private int _holdThresholdMilliseconds;

    public event EventHandler? Changed;

    public AltMouseViewModel(AltMouseSettings model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        
        Bindings = new ObservableCollection<AltMouseBindingEntryViewModel>(
            _model.Bindings.Select(pair => new AltMouseBindingEntryViewModel(pair.Key, pair.Value.TapKey, pair.Value.HoldKey)));
        Bindings.CollectionChanged += OnBindingsChanged;
        foreach (var entry in Bindings)
        {
            AttachEntry(entry);
        }

        _isEnabled = _model.IsEnabled;
        _holdThresholdMilliseconds = _model.HoldThresholdMilliseconds;
    }

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

    public ObservableCollection<AltMouseBindingEntryViewModel> Bindings { get; }

    private void OnBindingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (AltMouseBindingEntryViewModel item in e.NewItems)
            {
                AttachEntry(item);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (AltMouseBindingEntryViewModel item in e.OldItems)
            {
                DetachEntry(item);
            }
        }

        SyncToModel();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void AttachEntry(AltMouseBindingEntryViewModel entry)
    {
        entry.Changed += OnChildChanged;
    }

    private void DetachEntry(AltMouseBindingEntryViewModel entry)
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
        var bindings = new System.Collections.Generic.Dictionary<MouseButton, MouseButtonBinding>();
        foreach (var entry in Bindings)
        {
            bindings[entry.Button] = new MouseButtonBinding
            {
                TapKey = entry.TapKey == System.Windows.Input.Key.None ? null : entry.TapKey,
                HoldKey = entry.HoldKey == System.Windows.Input.Key.None ? null : entry.HoldKey
            };
        }

        _model.Bindings = bindings;
    }
}
