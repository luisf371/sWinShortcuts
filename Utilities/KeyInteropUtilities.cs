using System.Windows.Input;

namespace sWinShortcuts.Utilities;

public static class KeyInteropUtilities
{
    public static Key? NormalizeAppToggleKey(Key? key)
    {
        var vk = key.HasValue ? ToVirtualKey(key.Value) : 0;
        // App toggles cannot reserve modifiers: hooks still need their physical-state reconstruction.
        return vk is 0 or 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C
            ? null
            : key;
    }

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
