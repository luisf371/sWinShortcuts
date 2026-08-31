using System.Runtime.InteropServices;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.Services;

public sealed class WindowsInputSender : IInputSender
{
    private static readonly int InputStructSize = Marshal.SizeOf<NativeMethods.INPUT>();

    private readonly ILoggerService _logger;

    public WindowsInputSender(ILoggerService logger)
    {
        _logger = logger;
    }

    public bool SendKey(Key key, bool isKeyDown)
    {
        var virtualKey = KeyInteropUtilities.ToVirtualKey(key);
        if (virtualKey == 0)
        {
            if (_logger.IsEnabled)
            {
                _logger.Log($"[Input] SendInput skipped: no virtual-key mapping for {key}");
            }

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
        return SendInputLogged([input], SendInputKind.KeyEvent, key, isKeyDown);
    }

    public bool SendVirtualKeyTap(int virtualKey)
    {
        if (virtualKey is <= 0 or > ushort.MaxValue)
        {
            if (_logger.IsEnabled)
            {
                _logger.Log($"[Input] SendInput skipped: virtual key 0x{virtualKey:X} out of range");
            }

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
        return SendInputLogged([down, up], SendInputKind.VirtualKeyTap, virtualKey: virtualKey);
    }

    public bool SendLeftClick(int holdMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(holdMilliseconds);

        var down = CreateMouseInput(NativeMethods.MouseEventFlags.MOUSEEVENTF_LEFTDOWN);
        var up = CreateMouseInput(NativeMethods.MouseEventFlags.MOUSEEVENTF_LEFTUP);

        if (!SendInputLogged([down], SendInputKind.LeftButtonDown))
        {
            return false;
        }

        var released = false;
        try
        {
            Thread.Sleep(holdMilliseconds);
        }
        finally
        {
            released = NativeMethods.SendInput(1, [up], InputStructSize) == 1;
            if (!released)
            {
                // A failed UP is the dangerous half of a split click; retry once so a transient failure
                // cannot leave the logical mouse button held after the physical button is released.
                // Only a retry that also fails is logged — a first attempt the retry recovers stays
                // silent (per-action noise discipline).
                released = SendInputLogged([up], SendInputKind.LeftButtonUpRetry);
            }
        }

        return released;
    }

    public bool SendDummyKey()
    {
        var input = CreateKeyboardInput(
            0xFF,
            0,
            NativeMethods.KeyEventFlags.KEYEVENTF_KEYUP);
        return SendInputLogged([input], SendInputKind.DummyKey);
    }

    /// <summary>
    /// Which injected-event shape a SendInput call carries. Callers pass the raw key/direction or
    /// virtual-key value instead of a pre-built description so that successful, unlogged injections
    /// perform no diagnostic string work — these calls run at injected-key frequency.
    /// </summary>
    private enum SendInputKind
    {
        KeyEvent,
        VirtualKeyTap,
        LeftButtonDown,
        LeftButtonUpRetry,
        DummyKey
    }

    /// <summary>
    /// The single SendInput boundary for every injected event. On a short count the last error is
    /// captured immediately (nothing but the count comparison sits between the P/Invoke and the
    /// read) and one entry records what only this boundary can observe: the inserted-event count
    /// and the captured code. SendInput returns the number of events successfully inserted, and
    /// neither that value nor the error code identifies why input was blocked (UIPI blocking in
    /// particular is not reported) — the code is a captured diagnostic, never the rejection reason.
    /// The count also exposes a partial insertion of the two-event virtual-key tap (sent=1/2).
    /// The description is constructed only here — after a short count, inside the IsEnabled branch.
    /// </summary>
    private bool SendInputLogged(
        NativeMethods.INPUT[] inputs,
        SendInputKind kind,
        Key key = Key.None,
        bool isKeyDown = false,
        int virtualKey = 0)
    {
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, InputStructSize);
        if (sent == (uint)inputs.Length)
        {
            return true;
        }

        var lastError = Marshal.GetLastWin32Error();
        if (_logger.IsEnabled)
        {
            var description = kind switch
            {
                SendInputKind.KeyEvent => $"key {key} ({(isKeyDown ? "DOWN" : "UP")})",
                SendInputKind.VirtualKeyTap => $"virtual-key tap 0x{virtualKey:X}",
                SendInputKind.LeftButtonDown => "left-button DOWN",
                SendInputKind.LeftButtonUpRetry => "left-button UP failed after retry",
                _ => "dummy key"
            };
            var suffix = kind == SendInputKind.LeftButtonUpRetry ? " (button may be stuck)" : string.Empty;
            _logger.Log(
                $"[Input] SendInput {description}: sent={sent}/{inputs.Length} lastError=0x{lastError:X}{suffix}");
        }

        return false;
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

    private static NativeMethods.INPUT CreateMouseInput(NativeMethods.MouseEventFlags flags)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.InputType.INPUT_MOUSE,
            U = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dwFlags = flags,
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
