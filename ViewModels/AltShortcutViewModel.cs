using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using sWinShortcuts.Models;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.ViewModels;

// One editor for both runtime objects: the toggle and threshold always write both, and every row edit
// rebuilds both binding sets. The runtime keeps reading AltMouse and AltKeyboard unchanged.
public sealed class AltShortcutViewModel : ViewModelBase
{
    private const ProfileChangeKind BOTH_FAMILIES = ProfileChangeKind.AltMouse | ProfileChangeKind.AltKeyboard;

    private readonly AltMouseSettings _mouse;
    private readonly AltKeyboardSettings _keyboard;
    private bool _isEnabled;
    private int _holdThresholdMilliseconds;

    public event EventHandler<ProfileChangedEventArgs>? Changed;

    public AltShortcutViewModel(AltMouseSettings mouse, AltKeyboardSettings keyboard)
    {
        _mouse = mouse ?? throw new ArgumentNullException(nameof(mouse));
        _keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));

        Bindings = new ObservableCollection<AltShortcutBindingEntryViewModel>(
            _mouse.Bindings.Where(pair => Enum.IsDefined(pair.Key)).Select(pair => new AltShortcutBindingEntryViewModel(
                InputTrigger.FromMouseButton(pair.Key), pair.Value.TapKey, pair.Value.HoldKey)));
        if (_mouse.WheelUpKey is { } upKey && upKey != Key.None)
        {
            Bindings.Add(new AltShortcutBindingEntryViewModel(InputTrigger.FromWheel(MouseWheelDirection.Up), upKey, null));
        }
        if (_mouse.WheelDownKey is { } downKey && downKey != Key.None)
        {
            Bindings.Add(new AltShortcutBindingEntryViewModel(InputTrigger.FromWheel(MouseWheelDirection.Down), downKey, null));
        }
        foreach (var (key, binding) in _keyboard.Bindings)
        {
            Bindings.Add(new AltShortcutBindingEntryViewModel(InputTrigger.FromKey(key), binding.TapKey, binding.HoldKey));
        }

        foreach (var entry in Bindings)
        {
            entry.Changed += OnRowChanged;
        }
        Bindings.CollectionChanged += OnBindingsChanged;

        _isEnabled = _mouse.IsEnabled;
        _holdThresholdMilliseconds = _mouse.HoldThresholdMilliseconds;

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
                _mouse.IsEnabled = _keyboard.IsEnabled = value;
                Changed?.Invoke(this, new ProfileChangedEventArgs(BOTH_FAMILIES));
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
                _mouse.HoldThresholdMilliseconds = _keyboard.HoldThresholdMilliseconds = sanitized;
                Changed?.Invoke(this, new ProfileChangedEventArgs(BOTH_FAMILIES));
            }
        }
    }

    // Remove rows one at a time: Clear() raises Reset without OldItems and would leave handlers attached.
    public ObservableCollection<AltShortcutBindingEntryViewModel> Bindings { get; }

    internal static ProfileChangeKind Family(InputTrigger source) => source.Kind switch
    {
        InputTriggerKind.KeyboardKey => ProfileChangeKind.AltKeyboard,
        InputTriggerKind.MouseButton or InputTriggerKind.MouseWheel => ProfileChangeKind.AltMouse,
        _ => ProfileChangeKind.None
    };

    private void OnBindingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var kind = ProfileChangeKind.None;
        foreach (AltShortcutBindingEntryViewModel entry in e.NewItems ?? Array.Empty<AltShortcutBindingEntryViewModel>())
        {
            entry.Changed += OnRowChanged;
            kind |= Family(entry.Source);
        }
        foreach (AltShortcutBindingEntryViewModel entry in e.OldItems ?? Array.Empty<AltShortcutBindingEntryViewModel>())
        {
            entry.Changed -= OnRowChanged;
            kind |= Family(entry.Source);
        }

        Publish(kind);
    }

    private void OnRowChanged(object? sender, ProfileChangeKind kind) => Publish(kind);

    // Rebuild both binding sets from the rows, swap in fresh objects, then raise ONE event.
    private void Publish(ProfileChangeKind kind)
    {
        var buttons = new Dictionary<MouseButton, MouseButtonBinding>();
        var keys = new Dictionary<Key, AltKeyboardBinding>();
        Key? wheelUp = null, wheelDown = null;
        foreach (var row in Bindings)
        {
            Key? tap = row.TapKey == Key.None ? null : row.TapKey;
            Key? hold = row.HoldKey == Key.None ? null : row.HoldKey;
            switch (row.Source.Kind)
            {
                case InputTriggerKind.MouseButton:
                    buttons[row.Source.MouseButton] = new MouseButtonBinding { TapKey = tap, HoldKey = hold };
                    break;
                case InputTriggerKind.KeyboardKey:
                    keys[row.Source.Key] = new AltKeyboardBinding { TapKey = tap, HoldKey = hold };
                    break;
                case InputTriggerKind.MouseWheel when row.Source.Wheel == MouseWheelDirection.Up:
                    wheelUp = tap;
                    break;
                case InputTriggerKind.MouseWheel:
                    wheelDown = tap;
                    break;
            }
        }

        // Never clear or mutate a published dictionary: the hook thread and the autosave serializer read them.
        _mouse.Bindings = buttons;
        _mouse.WheelUpKey = wheelUp;
        _mouse.WheelDownKey = wheelDown;
        _keyboard.Bindings = keys;
        Changed?.Invoke(this, new ProfileChangedEventArgs(kind));
    }
}
