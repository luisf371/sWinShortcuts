using sWinShortcuts.Utilities;

namespace sWinShortcuts.Services;

/// <summary>
/// Receives the physical key edges of a running keybind capture. Called on the keyboard-hook thread (the UI
/// dispatcher thread, inside the hook callback): implementations may only update their own capture state and
/// post work to the dispatcher. They must never touch bindings, view models, or the hook service.
/// </summary>
public interface IKeyCaptureSink
{
    KeyCaptureStatus OnKey(int virtualKey, bool isDown);
}

/// <summary>
/// Routes physical keystrokes exclusively to a keybind capture, so app features, remaps and shell shortcuts
/// don't fire while the user is assigning a key.
/// </summary>
public interface IKeyCaptureSource
{
    /// <summary>
    /// Starts routing physical keys to <paramref name="sink"/>. Returns false when the hooks are not running,
    /// the caller is not on the hook dispatcher thread, or a macro owns the keyboard; callers then fall back
    /// to ordinary WPF key events.
    /// </summary>
    bool TryBeginKeyCapture(IKeyCaptureSink sink);

    /// <summary>Stops the capture if <paramref name="sink"/> still owns it. Safe to call repeatedly.</summary>
    void EndKeyCapture(IKeyCaptureSink sink);
}
