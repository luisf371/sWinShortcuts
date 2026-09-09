using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
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
            _model.Bindings.Select(pair => new AltMouseBindingEntryViewModel(
                InputTrigger.FromMouseButton(pair.Key), pair.Value.TapKey, pair.Value.HoldKey)));
        if (_model.WheelUpKey is { } upKey && upKey != Key.None)
        {
            Bindings.Add(new AltMouseBindingEntryViewModel(InputTrigger.FromWheel(MouseWheelDirection.Up), upKey, null));
        }
        if (_model.WheelDownKey is { } downKey && downKey != Key.None)
        {
            Bindings.Add(new AltMouseBindingEntryViewModel(InputTrigger.FromWheel(MouseWheelDirection.Down), downKey, null));
        }

        Bindings.CollectionChanged += OnBindingsChanged;
        foreach (var entry in Bindings)
        {
            AttachEntry(entry);
        }

        _isEnabled = _model.IsEnabled;
        _holdThresholdMilliseconds = _model.HoldThresholdMilliseconds;

        ResetHoldThresholdCommand = new RelayCommand(
            () => HoldThresholdMilliseconds = AltMouseSettings.DefaultHoldThresholdMilliseconds);
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
        Key? wheelUpKey = null;
        Key? wheelDownKey = null;
        foreach (var entry in Bindings)
        {
            Key? tapKey = entry.TapKey == Key.None ? null : entry.TapKey;
            if (entry.Source.Kind == InputTriggerKind.MouseButton)
            {
                bindings[entry.Source.MouseButton] = new MouseButtonBinding
                {
                    TapKey = tapKey,
                    HoldKey = entry.HoldKey == Key.None ? null : entry.HoldKey
                };
            }
            else if (entry.Source.Kind == InputTriggerKind.MouseWheel)
            {
                if (entry.Source.Wheel == MouseWheelDirection.Up)
                {
                    wheelUpKey = tapKey;
                }
                else if (entry.Source.Wheel == MouseWheelDirection.Down)
                {
                    wheelDownKey = tapKey;
                }
            }
        }

        _model.Bindings = bindings;
        _model.WheelUpKey = wheelUpKey;
        _model.WheelDownKey = wheelDownKey;
    }
}
