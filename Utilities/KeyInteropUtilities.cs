using System.Windows.Input;

namespace sWinShortcuts.Utilities;

public static class KeyInteropUtilities
{
    public static Key? FromVirtualKey(int virtualKey)
    {
        if (virtualKey <= 0)
        {
            return null;
        }

        try
        {
            return KeyInterop.KeyFromVirtualKey(virtualKey);
        }
        catch
        {
            return null;
        }
    }

    public static ushort ToVirtualKey(Key key)
    {
        return (ushort)KeyInterop.VirtualKeyFromKey(key);
    }
}
