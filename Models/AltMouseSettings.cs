using System;
using System.Collections.Generic;
using System.Windows.Input;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.Models;

public sealed class AltMouseSettings
{
    private volatile Dictionary<MouseButton, MouseButtonBinding> _bindings = new();

    public bool IsEnabled { get; set; }

    // Settable so the UI publishes edits by swapping in a fully built dictionary (copy-on-write): the
    // hook thread's TryGetValue and the pool-thread INI serializer read whatever reference they grabbed
    // as a stable snapshot, so an edit can never race them. Loading may still mutate the fresh
    // dictionary in place (pre-publication, single-threaded).
    public Dictionary<MouseButton, MouseButtonBinding> Bindings
    {
        get => _bindings;
        set => _bindings = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Tap-hold split threshold in milliseconds.
    /// </summary>
    public int HoldThresholdMilliseconds { get; set; } = 50;
}

public sealed class MouseButtonBinding
{
    public Key? TapKey { get; set; }

    public Key? HoldKey { get; set; }

    public bool SuppressOriginalWhileAltIsHeld => TapKey.HasValue || HoldKey.HasValue;
}
