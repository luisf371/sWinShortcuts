using System.Windows.Input;

namespace sWinShortcuts.Services;

/// <summary>
/// Native synthetic-input boundary. Key work uses one FIFO executor; Rapid Fire may call
/// SendLeftClick concurrently from its one-shot timer callback.
/// </summary>
public interface IInputSender
{
    bool SendKey(Key key, bool isKeyDown);

    bool SendVirtualKeyTap(int virtualKey);

    bool SendLeftClick();

    bool SendDummyKey();
}
