using System.Windows.Input;

namespace sWinShortcuts.Services;

/// <summary>
/// Native synthetic-input boundary. InputHookService calls this only from its single FIFO executor.
/// </summary>
public interface IInputSender
{
    bool SendKey(Key key, bool isKeyDown);

    bool SendVirtualKeyTap(int virtualKey);

    bool SendDummyKey();
}
