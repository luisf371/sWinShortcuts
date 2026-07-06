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
            var key = KeyInterop.KeyFromVirtualKey(virtualKey);
            // Treat an unmapped virtual key as "no key" (null), consistent with Deserialize("None") => null.
            return key == Key.None ? null : key;
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
