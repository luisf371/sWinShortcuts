using System.Windows.Input;

namespace sWinShortcuts.Services;

/// <summary>
/// Native synthetic-input boundary. Key work uses one FIFO executor; Rapid Fire may call
/// SendLeftClick concurrently from its one-shot timer callback.
/// Optional admission callbacks run after native preparation, immediately before new input.
/// Already-owed key/button UPs bypass admission and may carry the macro release tag.
/// </summary>
public interface IInputSender
{
    bool SendKey(Key key, bool isKeyDown, bool macroRelease = false, Func<bool>? canSend = null);

    bool SendMouseButton(Models.MouseButton button, bool isDown, bool macroRelease = false, Func<bool>? canSend = null);

    bool MoveMouseTo(int physicalX, int physicalY, Func<bool>? canSend = null);

    bool SendMouseWheel(int delta, bool horizontal, Func<bool>? canSend = null);

    bool SendVirtualKeyTap(int virtualKey);

    LeftClickResult SendLeftClick(int holdMilliseconds);

    bool SendDummyKey();
}

/// <summary>
/// Which half of a synthetic left click failed. Only <see cref="UpFailed"/> can leave the logical
/// button held, because its DOWN was delivered.
/// </summary>
public enum LeftClickResult
{
    Sent,
    DownFailed,
    UpFailed
}
