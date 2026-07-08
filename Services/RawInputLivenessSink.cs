using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using sWinShortcuts.Interop;

namespace sWinShortcuts.Services;

// P8 rework: raw-input liveness side channel for the hook-loss watchdog.
//
// GetLastInputInfo is system-global (any device), while the watchdog's liveness ticks are per-hook —
// comparing the two cannot distinguish "this device is idle while the other is active" (normal
// mouse-only gaming / keyboard-only typing) from "this hook was silently removed". That conflation
// produced a false reinstall roughly every 40s of single-device use.
//
// This sink gives the watchdog a true PER-DEVICE activity signal: a message-only window receiving
// WM_INPUT via RIDEV_INPUTSINK for exactly one device class at a time. Raw input delivery is a
// separate pipeline from WH_*_LL hooks, so "raw input arrived for this device while its hook stayed
// silent" is direct proof of hook loss. Registration is suspicion-gated by the caller (opened only
// while a hook looks stale) so the steady-state cost is zero — no WM_INPUT traffic flows on the
// dispatcher while both hooks are believed healthy, which matters at multi-kHz mouse polling rates.
//
// Threading contract: construct on the dispatcher thread (the window and its WndProc are owned by
// it, and WM_INPUT for a message-only window is pumped there). RegisterX/UnregisterX may be called
// from any thread (RegisterRawInputDevices is process-global, not thread-affine). Tick reads are
// Volatile from any thread. Dispose unregisters from any thread but destroys the window only when
// called on the owning thread — otherwise the OS reclaims it at process exit (HWNDs are
// thread-affine; DestroyWindow from another thread always fails).
internal sealed class RawInputLivenessSink : IDisposable
{
    private const string WindowClassName = "sWinShortcuts.RawInputLivenessSink";

    // Registered once per process; RegisterClassW fails on duplicate names. Lazy so a failed
    // registration surfaces on first construction rather than at type-load.
    private static readonly Lazy<ushort> ClassAtom = new(RegisterWindowClass);

    // Keeps the WndProc delegate alive for the lifetime of the window class (the class outlives any
    // single sink instance, so this must be static).
    private static NativeMethods.WndProcDelegate? _keepAliveWndProc;

    // The class-level WndProc routes to the single live instance. Only one sink exists per process
    // (InputHookService is a singleton); guarded by _instanceLock for construction/dispose races.
    private static readonly object _instanceLock = new();
    private static RawInputLivenessSink? _instance;

    private readonly IntPtr _hwnd;
    private readonly int _ownerManagedThreadId;
    private bool _disposed;

    // 0 = no raw event seen since the device sink was (re)opened. Stamped by the WndProc on the
    // dispatcher; read by the watchdog on a pool thread.
    private long _lastKeyboardRawTick;
    private long _lastMouseRawTick;

    internal long LastKeyboardRawTick => Volatile.Read(ref _lastKeyboardRawTick);
    internal long LastMouseRawTick => Volatile.Read(ref _lastMouseRawTick);

    public RawInputLivenessSink()
    {
        if (ClassAtom.Value == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassW failed for raw-input liveness sink");
        }

        lock (_instanceLock)
        {
            _hwnd = NativeMethods.CreateWindowExW(0, WindowClassName, WindowClassName, 0, 0, 0, 0, 0,
                NativeMethods.HWND_MESSAGE, IntPtr.Zero, NativeMethods.GetModuleHandleW(null), IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed for raw-input liveness sink");
            }

            _ownerManagedThreadId = Environment.CurrentManagedThreadId;
            _instance = this;
        }
    }

    private static ushort RegisterWindowClass()
    {
        _keepAliveWndProc = StaticWndProc;
        var wc = new NativeMethods.WNDCLASS
        {
            lpfnWndProc = _keepAliveWndProc,
            hInstance = NativeMethods.GetModuleHandleW(null),
            lpszClassName = WindowClassName
        };
        return NativeMethods.RegisterClassW(ref wc);
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_INPUT)
        {
            // Header-only read: the watchdog needs "which device class, when" — never the payload.
            var header = new NativeMethods.RAWINPUTHEADER();
            var size = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
            if (NativeMethods.GetRawInputData(lParam, NativeMethods.RID_HEADER, ref header, ref size,
                    (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>()) != unchecked((uint)-1))
            {
                // _instance is read without the lock: WndProc only runs on the owner thread, and the
                // window is destroyed (or orphaned until exit) before _instance is cleared.
                var sink = _instance;
                if (sink is not null)
                {
                    if (header.dwType == NativeMethods.RIM_TYPEKEYBOARD)
                    {
                        Volatile.Write(ref sink._lastKeyboardRawTick, Stopwatch.GetTimestamp());
                    }
                    else if (header.dwType == NativeMethods.RIM_TYPEMOUSE)
                    {
                        Volatile.Write(ref sink._lastMouseRawTick, Stopwatch.GetTimestamp());
                    }
                }
            }
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // All public (un)registration is serialized against Dispose under _instanceLock and refused once
    // disposed: Stop() can dispose this object while a watchdog tick holding a stale reference is
    // mid-decision (Timer.Dispose does not wait for in-flight callbacks), and a post-dispose
    // RegisterX would otherwise re-register raw input against a destroyed/orphaned window right
    // after Dispose unregistered it.

    // (Re)opens the keyboard sink and resets its tick so staleness is measured from this moment.
    public bool RegisterKeyboard()
    {
        lock (_instanceLock)
        {
            if (_disposed)
            {
                return false;
            }

            Volatile.Write(ref _lastKeyboardRawTick, 0);
            return RegisterDevice(NativeMethods.HID_USAGE_GENERIC_KEYBOARD, NativeMethods.RIDEV_INPUTSINK, _hwnd);
        }
    }

    public bool RegisterMouse()
    {
        lock (_instanceLock)
        {
            if (_disposed)
            {
                return false;
            }

            Volatile.Write(ref _lastMouseRawTick, 0);
            return RegisterDevice(NativeMethods.HID_USAGE_GENERIC_MOUSE, NativeMethods.RIDEV_INPUTSINK, _hwnd);
        }
    }

    // RIDEV_REMOVE requires a NULL target window (parameter validation fails otherwise).
    public bool UnregisterKeyboard()
    {
        lock (_instanceLock)
        {
            return !_disposed && RegisterDevice(NativeMethods.HID_USAGE_GENERIC_KEYBOARD, NativeMethods.RIDEV_REMOVE, IntPtr.Zero);
        }
    }

    public bool UnregisterMouse()
    {
        lock (_instanceLock)
        {
            return !_disposed && RegisterDevice(NativeMethods.HID_USAGE_GENERIC_MOUSE, NativeMethods.RIDEV_REMOVE, IntPtr.Zero);
        }
    }

    private static bool RegisterDevice(ushort usage, uint flags, IntPtr target)
    {
        var devices = new[]
        {
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = NativeMethods.HID_USAGE_PAGE_GENERIC,
                usUsage = usage,
                dwFlags = flags,
                hwndTarget = target
            }
        };
        return NativeMethods.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());
    }

    public void Dispose()
    {
        lock (_instanceLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            // Direct core calls, not the public wrappers — those refuse once _disposed is set.
            // Best-effort: harmless if a device sink was never opened.
            RegisterDevice(NativeMethods.HID_USAGE_GENERIC_KEYBOARD, NativeMethods.RIDEV_REMOVE, IntPtr.Zero);
            RegisterDevice(NativeMethods.HID_USAGE_GENERIC_MOUSE, NativeMethods.RIDEV_REMOVE, IntPtr.Zero);

            if (Environment.CurrentManagedThreadId == _ownerManagedThreadId)
            {
                NativeMethods.DestroyWindow(_hwnd);
            }
            // else: HWNDs are thread-affine; the message-only window leaks until process exit,
            // which only happens on the App.OnExit Stop() path where exit is imminent anyway.

            _instance = null;
        }
    }
}
