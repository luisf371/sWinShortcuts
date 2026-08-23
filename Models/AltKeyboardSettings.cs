using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class AltKeyboardSettings
{
    private volatile Dictionary<Key, AltKeyboardBinding> _bindings = new();

    public bool IsEnabled { get; set; }

    // Settable so the UI publishes edits by swapping in a fully built dictionary (copy-on-write): the
    // hook thread's TryGetValue and the pool-thread INI serializer read whatever reference they grabbed
    // as a stable snapshot, so an edit can never race them. Loading may still mutate the fresh
    // dictionary in place (pre-publication, single-threaded).
    public Dictionary<Key, AltKeyboardBinding> Bindings
    {
        get => _bindings;
        set => _bindings = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Tap-hold split threshold in milliseconds.
    /// </summary>
    public const int DefaultHoldThresholdMilliseconds = 150;

    public int HoldThresholdMilliseconds { get; set; } = DefaultHoldThresholdMilliseconds;
}

public sealed class AltKeyboardBinding
{
    public Key? TapKey { get; set; }

    public Key? HoldKey { get; set; }

    public bool SuppressOriginalWhileAltIsHeld => TapKey.HasValue || HoldKey.HasValue;
}
