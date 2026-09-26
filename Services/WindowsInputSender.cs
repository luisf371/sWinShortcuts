using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Drawing;
using sWinShortcuts.Interop;
using sWinShortcuts.Utilities;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.Services;

public sealed class WindowsInputSender : IInputSender
{
    private static readonly int InputStructSize = Marshal.SizeOf<NativeMethods.INPUT>();

    private readonly ILoggerService _logger;
    private readonly Func<NativeMethods.INPUT[], uint> _sendInput;
    private readonly Func<Rectangle[]> _getMonitorBounds;

    public WindowsInputSender(ILoggerService logger) : this(logger, SendNativeInput, GetPhysicalMonitorBounds)
    {
    }

    internal WindowsInputSender(ILoggerService logger, Func<NativeMethods.INPUT[], uint> sendInput,
        Func<Rectangle[]> getMonitorBounds)
    {
        _logger = logger;
        _sendInput = sendInput;
        _getMonitorBounds = getMonitorBounds;
    }

    public bool SendKey(Key key, bool isKeyDown, bool macroRelease = false, Func<bool>? canSend = null)
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

        var input = CreateKeyboardInput((ushort)virtualKey, scanCode, flags, macroRelease && !isKeyDown);
        return (!isKeyDown || canSend?.Invoke() != false) &&
            SendInputLogged([input], SendInputKind.KeyEvent, key, isKeyDown);
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

    public LeftClickResult SendLeftClick(int holdMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(holdMilliseconds);

        var down = CreateMouseInput(NativeMethods.MouseEventFlags.MOUSEEVENTF_LEFTDOWN);
        var up = CreateMouseInput(NativeMethods.MouseEventFlags.MOUSEEVENTF_LEFTUP);

        if (!SendInputLogged([down], SendInputKind.LeftButtonDown))
        {
            return LeftClickResult.DownFailed;
        }

        var released = false;
        try
        {
            Thread.Sleep(holdMilliseconds);
        }
        finally
        {
            released = _sendInput([up]) == 1;
            if (!released)
            {
                // A failed UP is the dangerous half of a split click; retry once so a transient failure
                // cannot leave the logical mouse button held after the physical button is released.
                // Only a retry that also fails is logged — a first attempt the retry recovers stays
                // silent (per-action noise discipline).
                released = SendInputLogged([up], SendInputKind.LeftButtonUpRetry);
            }
        }

        return released ? LeftClickResult.Sent : LeftClickResult.UpFailed;
    }

    public bool SendDummyKey()
    {
        var input = CreateKeyboardInput(
            0xFF,
            0,
            NativeMethods.KeyEventFlags.KEYEVENTF_KEYUP);
        return SendInputLogged([input], SendInputKind.DummyKey);
    }

    public bool SendMouseButton(MouseButton button, bool isDown, bool macroRelease = false, Func<bool>? canSend = null)
    {
        var flags = button switch
        {
            MouseButton.Left => isDown ? NativeMethods.MouseEventFlags.MOUSEEVENTF_LEFTDOWN : NativeMethods.MouseEventFlags.MOUSEEVENTF_LEFTUP,
            MouseButton.Right => isDown ? NativeMethods.MouseEventFlags.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MouseEventFlags.MOUSEEVENTF_RIGHTUP,
            MouseButton.Middle => isDown ? NativeMethods.MouseEventFlags.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MouseEventFlags.MOUSEEVENTF_MIDDLEUP,
            MouseButton.XButton1 or MouseButton.XButton2 => isDown ? NativeMethods.MouseEventFlags.MOUSEEVENTF_XDOWN : NativeMethods.MouseEventFlags.MOUSEEVENTF_XUP,
            _ => (NativeMethods.MouseEventFlags)0
        };
        if (flags == 0) return false;
        var input = CreateMouseInput(flags, macroRelease && !isDown);
        input.U.mi.mouseData = button == MouseButton.XButton1 ? 1u : button == MouseButton.XButton2 ? 2u : 0u;
        return (!isDown || canSend?.Invoke() != false) &&
            SendInputLogged([input], SendInputKind.MouseButton, isKeyDown: isDown, virtualKey: (int)button);
    }

    public bool SendMouseWheel(int delta, bool horizontal, Func<bool>? canSend = null)
    {
        if (delta is 0 or < short.MinValue or > short.MaxValue) return false;
        var input = CreateMouseInput(horizontal
            ? NativeMethods.MouseEventFlags.MOUSEEVENTF_HWHEEL
            : NativeMethods.MouseEventFlags.MOUSEEVENTF_WHEEL);
        input.U.mi.mouseData = unchecked((uint)delta);
        return canSend?.Invoke() != false && SendInputLogged([input], SendInputKind.MouseWheel, virtualKey: delta);
    }

    public bool MoveMouseTo(int physicalX, int physicalY, Func<bool>? canSend = null)
    {
        var monitors = _getMonitorBounds();
        if (monitors.Length == 0 || !monitors.Any(bounds => bounds.Contains(physicalX, physicalY))) return false;
        var left = monitors.Min(bounds => (long)bounds.Left);
        var top = monitors.Min(bounds => (long)bounds.Top);
        var width = monitors.Max(bounds => (long)bounds.Left + bounds.Width) - left;
        var height = monitors.Max(bounds => (long)bounds.Top + bounds.Height) - top;
        // Each absolute coordinate addresses a pixel interval. Aim at its center to avoid rounding
        // onto an adjacent pixel; cursor observation still determines whether the point was reached.
        // ponytail: 16-bit absolute transport cannot uniquely target wider desktops; fail closed
        // until a different native transport can still preserve exact physical endpoints.
        if (width is <= 0 or > 65536 || height is <= 0 or > 65536) return false;
        var input = CreateMouseInput(NativeMethods.MouseEventFlags.MOUSEEVENTF_MOVE |
            NativeMethods.MouseEventFlags.MOUSEEVENTF_ABSOLUTE | NativeMethods.MouseEventFlags.MOUSEEVENTF_VIRTUALDESK);
        input.U.mi.dx = (int)(((physicalX - left) * 65536 + 32768) / width);
        input.U.mi.dy = (int)(((physicalY - top) * 65536 + 32768) / height);
        // Keep the native send in physical coordinates too: system/unaware threads can have
        // absolute input quantized through a scaled monitor even after physical enumeration.
        var previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (previous == IntPtr.Zero) return false;
        try
        {
            return canSend?.Invoke() != false && SendInputLogged([input], SendInputKind.MouseMove);
        }
        finally
        {
            NativeMethods.SetThreadDpiAwarenessContext(previous);
        }
    }

    internal static bool TryGetPhysicalCursorPosition(out int x, out int y)
    {
        var succeeded = NativeMethods.GetPhysicalCursorPos(out var point);
        x = point.X;
        y = point.Y;
        return succeeded;
    }

    internal static Rectangle[] GetPhysicalMonitorBounds()
    {
        var previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if (previous == IntPtr.Zero) return [];
        try
        {
            // EnumDisplayMonitors also reports invisible mirroring-driver pseudo-monitors.
            var visibleDevices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (uint index = 0; ; index++)
            {
                var device = new NativeMethods.DISPLAY_DEVICE();
                device.Initialize();
                if (!NativeMethods.EnumDisplayDevices(null, index, ref device, 0)) break;
                if ((device.StateFlags & (NativeMethods.DisplayDeviceStateFlags.AttachedToDesktop |
                    NativeMethods.DisplayDeviceStateFlags.MirroringDriver)) == NativeMethods.DisplayDeviceStateFlags.AttachedToDesktop)
                    visibleDevices.Add(device.DeviceName);
            }
            var monitors = new List<Rectangle>();
            if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr monitor, IntPtr deviceContext, ref NativeMethods.RECT bounds, IntPtr data) =>
                {
                    var info = new NativeMethods.MONITORINFOEX { Size = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                    if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return false;
                    if (visibleDevices.Contains(info.DeviceName) && bounds.Right > bounds.Left && bounds.Bottom > bounds.Top)
                        monitors.Add(Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom));
                    return true;
                }, IntPtr.Zero)) return [];
            return monitors.OrderBy(bounds => bounds.X).ThenBy(bounds => bounds.Y)
                .ThenBy(bounds => bounds.Width).ThenBy(bounds => bounds.Height).ToArray();
        }
        finally
        {
            NativeMethods.SetThreadDpiAwarenessContext(previous);
        }
    }

    internal static bool IsPhysicalPointOnMonitor(int x, int y) =>
        GetPhysicalMonitorBounds().Any(bounds => bounds.Contains(x, y));

    private static uint SendNativeInput(NativeMethods.INPUT[] inputs) =>
        NativeMethods.SendInput((uint)inputs.Length, inputs, InputStructSize);

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
        DummyKey,
        MouseButton,
        MouseWheel,
        MouseMove
    }

    /// <summary>
    /// On a short count the last error is captured immediately (nothing but the count comparison
    /// sits between the P/Invoke and the read) and one entry records what only this boundary can
    /// observe: the inserted-event count and the captured code. Neither value identifies why input
    /// was blocked (UIPI blocking in particular is not reported), so the code is a captured
    /// diagnostic, never the rejection reason.
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
        var sent = _sendInput(inputs);
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
                SendInputKind.MouseButton => $"mouse button {(MouseButton)virtualKey} ({(isKeyDown ? "DOWN" : "UP")})",
                SendInputKind.MouseWheel => $"mouse wheel {virtualKey}",
                SendInputKind.MouseMove => "absolute mouse movement",
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
        NativeMethods.KeyEventFlags flags,
        bool macroRelease = false)
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
                    dwExtraInfo = macroRelease ? NativeMethods.INPUT_MACRO_RELEASE : NativeMethods.INPUT_IGNORE
                }
            }
        };
    }

    private static NativeMethods.INPUT CreateMouseInput(NativeMethods.MouseEventFlags flags, bool macroRelease = false)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.InputType.INPUT_MOUSE,
            U = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dwFlags = flags,
                    dwExtraInfo = macroRelease ? NativeMethods.INPUT_MACRO_RELEASE : NativeMethods.INPUT_IGNORE
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
