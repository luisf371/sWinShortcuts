using System.Runtime.InteropServices;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.Services;

public sealed class WindowsInputSender : IInputSender
{
    private static readonly int InputStructSize = Marshal.SizeOf<NativeMethods.INPUT>();

    public bool SendKey(Key key, bool isKeyDown)
    {
        var virtualKey = KeyInteropUtilities.ToVirtualKey(key);
        if (virtualKey == 0)
        {
            return false;
        }

        var scanCode = (ushort)NativeMethods.MapVirtualKey((uint)virtualKey, 0);
        var flags = isKeyDown
            ? (NativeMethods.KeyEventFlags)0
            : NativeMethods.KeyEventFlags.KEYEVENTF_KEYUP;
        if (IsExtendedKey(key))
        {
            flags |= NativeMethods.KeyEventFlags.KEYEVENTF_EXTENDEDKEY;
        }

        var input = CreateKeyboardInput((ushort)virtualKey, scanCode, flags);
        return NativeMethods.SendInput(1, [input], InputStructSize) == 1;
    }

    public bool SendVirtualKeyTap(int virtualKey)
    {
        if (virtualKey is <= 0 or > ushort.MaxValue)
        {
            return false;
        }

        var scanCode = (ushort)NativeMethods.MapVirtualKey((uint)virtualKey, 0);
        var down = CreateKeyboardInput(
            (ushort)virtualKey,
            scanCode,
            (NativeMethods.KeyEventFlags)0);
        var up = CreateKeyboardInput(
            (ushort)virtualKey,
            scanCode,
            NativeMethods.KeyEventFlags.KEYEVENTF_KEYUP);
        return NativeMethods.SendInput(2, [down, up], InputStructSize) == 2;
    }

    public bool SendDummyKey()
    {
        var input = CreateKeyboardInput(
            0xFF,
            0,
            NativeMethods.KeyEventFlags.KEYEVENTF_KEYUP);
        return NativeMethods.SendInput(1, [input], InputStructSize) == 1;
    }

    private static NativeMethods.INPUT CreateKeyboardInput(
        ushort virtualKey,
        ushort scanCode,
        NativeMethods.KeyEventFlags flags)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.InputType.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = scanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = NativeMethods.INPUT_IGNORE
                }
            }
        };
    }

    private static bool IsExtendedKey(Key key)
    {
        return key is Key.RightAlt or Key.RightCtrl or Key.Insert or Key.Delete or
                      Key.Home or Key.End or Key.PageUp or Key.PageDown or
                      Key.Up or Key.Down or Key.Left or Key.Right or
                      Key.NumLock or Key.PrintScreen or Key.Divide or Key.Apps;
    }
}
