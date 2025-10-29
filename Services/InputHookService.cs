using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.Services;

public sealed class InputHookService : IInputHookService
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<Models.MouseButton, MouseButtonState> _mouseStates = new()
    {
        { Models.MouseButton.Left, new MouseButtonState() },
        { Models.MouseButton.Right, new MouseButtonState() },
        { Models.MouseButton.Middle, new MouseButtonState() },
        { Models.MouseButton.XButton1, new MouseButtonState() },
        { Models.MouseButton.XButton2, new MouseButtonState() }
    };
    private readonly Dictionary<Key, RightMouseOverrideState> _activeRightMouseOverrides = new();
    private readonly Random _random = new();

    private Profile? _activeProfile;
    private Profile? _windowsProfile;
    private bool _isRunning;
    private bool _altPressed;
    private bool _rightButtonPressed;
    private bool _capsShiftEngaged;
    private Key? _capsRemappedKey;
    private bool _holdBreathActive;
    private CancellationTokenSource? _holdBreathDelayToken;

    private NativeMethods.LowLevelKeyboardProc? _keyboardProc;
    private NativeMethods.LowLevelMouseProc? _mouseProc;
    private IntPtr _keyboardHookHandle = IntPtr.Zero;
    private IntPtr _mouseHookHandle = IntPtr.Zero;

    private static readonly double TickToMilliseconds = 1000.0 / Stopwatch.Frequency;
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "sWinShortcuts_AltMouse_Debug.log");

    public event EventHandler<Profile?>? ActiveProfileChanged;

    public void Start()
    {
        lock (_syncRoot)
        {
            if (_isRunning)
            {
                return;
            }

            _keyboardProc = KeyboardCallback;
            _mouseProc = MouseCallback;

            var user32Handle = NativeMethods.LoadLibrary("user32.dll");
            if (user32Handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to load user32.dll");
            }

            _keyboardHookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, user32Handle, 0);
            _mouseHookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, user32Handle, 0);

            if (_keyboardHookHandle == IntPtr.Zero || _mouseHookHandle == IntPtr.Zero)
            {
                Stop();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to install input hooks");
            }

            _isRunning = true;
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            if (!_isRunning)
            {
                return;
            }

            if (_keyboardHookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle);
                _keyboardHookHandle = IntPtr.Zero;
            }

            if (_mouseHookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);
                _mouseHookHandle = IntPtr.Zero;
            }

            ReleaseOverrideKeys();
            ResetMouseStates();
            _altPressed = false;
            _rightButtonPressed = false;
            ReleaseCapsState();
            ReleaseHoldBreathState();

            _isRunning = false;
        }
    }

    public void ActivateProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (_syncRoot)
        {
            if (!_isRunning)
            {
                return;
            }

            if (ReferenceEquals(_activeProfile, profile))
            {
                return;
            }

            ResetState();
            _activeProfile = profile;
        }

        ActiveProfileChanged?.Invoke(this, profile);
    }

    public void DeactivateProfile()
    {
        Profile? previous;
        lock (_syncRoot)
        {
            previous = _activeProfile;
            if (previous is null)
            {
                return;
            }

            ResetState();
            _activeProfile = null;
        }

        ActiveProfileChanged?.Invoke(this, null);
    }

    public void Dispose()
    {
        Stop();
    }

    public void SetWindowsProfile(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        lock (_syncRoot)
        {
            _windowsProfile = profile;
        }
    }

    private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_isRunning)
        {
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

        if ((data.flags & NativeMethods.KbdLlFlags.LLKHF_INJECTED) != 0 || data.dwExtraInfo == NativeMethods.INPUT_IGNORE)
        {
            return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        bool isKeyDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        bool isKeyUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
        int vkCode = (int)data.vkCode;

        if (vkCode is 0xA4 or 0xA5 or 0x12)
        {
            if (isKeyDown)
            {
                _altPressed = true;
            }
            else if (isKeyUp)
            {
                _altPressed = false;
            }
        }

        var handled = HandleCapsLock(vkCode, isKeyDown, isKeyUp) || HandleRightMouseOverride(vkCode, isKeyDown, isKeyUp);

        if (!handled)
        {
            handled = HandleWindowsLauncher(vkCode, isKeyDown);
        }

        if (handled)
        {
            return (IntPtr)1;
        }

        return NativeMethods.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || !_isRunning)
        {
            return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        var message = (int)wParam;
        var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

        if ((data.flags & NativeMethods.MouseLlFlags.LLMHF_INJECTED) != 0)
        {
            return NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
        }

        switch (message)
        {
            case NativeMethods.WM_RBUTTONDOWN:
                _rightButtonPressed = true;
                HandleRightClickHoldBreathDown();
                break;
            case NativeMethods.WM_RBUTTONUP:
                _rightButtonPressed = false;
                ReleaseOverrideKeys();
                HandleRightClickHoldBreathUp();
                break;
        }

        var handled = HandleAltMouse(message, data.mouseData);

        return handled
            ? (IntPtr)1
            : NativeMethods.CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
    }

    private bool HandleAltMouse(int message, uint mouseData)
    {
        var profile = _activeProfile;
        if (profile is null || !profile.AltMouse.IsEnabled || !_altPressed)
        {
            return false;
        }

        var button = message switch
        {
            NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP => Models.MouseButton.Left,
            NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP => Models.MouseButton.Right,
            NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP => Models.MouseButton.Middle,
            NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP => GetXButton(mouseData),
            _ => (Models.MouseButton?)null
        };

        if (!button.HasValue || !profile.AltMouse.Bindings.TryGetValue(button.Value, out var binding))
        {
            return false;
        }

        if (binding is null || (!binding.TapKey.HasValue && !binding.HoldKey.HasValue))
        {
            return false;
        }

        var state = _mouseStates[button.Value];
        var isDown = message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN;
        var isUp = message is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_RBUTTONUP or NativeMethods.WM_MBUTTONUP or NativeMethods.WM_XBUTTONUP;

        if (isDown)
        {
            CancelHoldTimer(state);
            state.DownTick = Stopwatch.GetTimestamp();
            state.AltHandled = true;
            state.HoldTriggered = false;

            LogDebug($"[{button}] Button DOWN - Tap={binding.TapKey}, Hold={binding.HoldKey}, Threshold={profile.AltMouse.HoldThresholdMilliseconds}ms");

            if (binding.HoldKey.HasValue)
            {
                var holdKey = binding.HoldKey.Value;
                var threshold = Math.Max(10, profile.AltMouse.HoldThresholdMilliseconds);
                var cts = new CancellationTokenSource();
                state.HoldToken = cts;

                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(threshold, cts.Token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        LogDebug($"[{button}] Hold timer cancelled");
                        return;
                    }

                    lock (_syncRoot)
                    {
                        var altState = _altPressed;
                        var running = _isRunning;
                        var cancelled = cts.IsCancellationRequested;
                        var handled = state.AltHandled;

                        if (!running || cancelled || !altState || !handled)
                        {
                            LogDebug($"[{button}] Hold timer fired but blocked - Running={running}, Cancelled={cancelled}, AltPressed={altState}, AltHandled={handled}");
                            return;
                        }

                        LogDebug($"[{button}] Hold timer fired - Sending {holdKey} TAP");
                        state.HoldTriggered = true;
                        // Send hold key as a quick tap, not a sustained press
                        FireTapKey(holdKey);
                    }
                });
            }
            else
            {
                state.HoldToken = null;
            }

            return true;
        }

        if (isUp && state.AltHandled)
        {
            CancelHoldTimer(state);
            var elapsedMs = state.DownTick.HasValue
                ? (Stopwatch.GetTimestamp() - state.DownTick.Value) * TickToMilliseconds
                : 0;
            state.DownTick = null;
            state.AltHandled = false;

            var threshold = profile.AltMouse.HoldThresholdMilliseconds;
            var holdKey = binding.HoldKey;

            LogDebug($"[{button}] Button UP - Elapsed={elapsedMs:F1}ms, Threshold={threshold}ms, HoldTriggered={state.HoldTriggered}");

            if (state.HoldTriggered)
            {
                // Hold was already triggered - nothing more to do on release
                LogDebug($"[{button}] Hold was triggered - Already sent tap");
            }
            else if (holdKey.HasValue && elapsedMs >= threshold)
            {
                // Hold threshold met but timer didn't fire yet (race condition)
                // Send hold key as quick tap
                LogDebug($"[{button}] Hold threshold met but timer not fired - Sending {holdKey.Value} TAP");
                FireTapKey(holdKey.Value);
            }
            else if (binding.TapKey.HasValue)
            {
                // Quick tap - send tap key
                LogDebug($"[{button}] Quick tap - Sending {binding.TapKey.Value}");
                FireTapKey(binding.TapKey.Value);
            }

            state.HoldTriggered = false;

            return true;
        }

        return false;
    }

    private Models.MouseButton GetXButton(uint mouseData)
    {
        // Extract which X button from the high-order word of mouseData
        // XBUTTON1 = 0x0001, XBUTTON2 = 0x0002
        var xButton = (mouseData >> 16) & 0xFFFF;
        
        return xButton switch
        {
            1 => Models.MouseButton.XButton1, // Mouse Button 4
            2 => Models.MouseButton.XButton2, // Mouse Button 5
            _ => Models.MouseButton.XButton1  // Default to Button 4 if unclear
        };
    }

    private void CancelHoldTimer(MouseButtonState state)
    {
        if (state.HoldToken is null)
        {
            return;
        }

        try
        {
            state.HoldToken.Cancel();
        }
        catch
        {
        }
        finally
        {
            state.HoldToken.Dispose();
            state.HoldToken = null;
        }
    }


    private bool HandleRightMouseOverride(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        var profile = _activeProfile;
        if (profile is null || !profile.RightMouseOverrides.IsEnabled)
        {
            return false;
        }

        var sourceKey = KeyInteropUtilities.FromVirtualKey(vkCode);
        if (sourceKey is null)
        {
            return false;
        }

        var overrideEntry = profile.RightMouseOverrides.Overrides.FirstOrDefault(o => o.SourceKey == sourceKey.Value);
        if (overrideEntry is null)
        {
            return false;
        }

        var suppressOriginal = overrideEntry.SuppressOriginalKey;
        var targetKey = overrideEntry.TargetKey;

        if (isKeyDown)
        {
            if (!_rightButtonPressed)
            {
                return false;
            }

            if (_activeRightMouseOverrides.ContainsKey(sourceKey.Value))
            {
                return suppressOriginal;
            }

            _activeRightMouseOverrides[sourceKey.Value] = new RightMouseOverrideState
            {
                TargetKey = targetKey,
                SuppressOriginal = suppressOriginal
            };
            SendKey(targetKey, true);
            return suppressOriginal;
        }

        if (isKeyUp)
        {
            if (_activeRightMouseOverrides.Remove(sourceKey.Value, out var state))
            {
                SendKey(state.TargetKey, false);
                return state.SuppressOriginal;
            }

            return false;
        }

        return false;
    }

    private bool HandleCapsLock(int vkCode, bool isKeyDown, bool isKeyUp)
    {
        if (vkCode != 0x14)
        {
            return false;
        }

        var settings = GetEffectiveCapsLockSettings();
        if (settings is not { IsEnabled: true })
        {
            return false;
        }

        switch (settings.Mode)
        {
            case CapsLockMode.Normal:
                return false;
            case CapsLockMode.Disabled:
                return true;
            case CapsLockMode.MomentaryShift:
                if (isKeyDown && !_capsShiftEngaged)
                {
                    _capsShiftEngaged = true;
                    SendKey(Key.LeftShift, true);
                }
                else if (isKeyUp && _capsShiftEngaged)
                {
                    _capsShiftEngaged = false;
                    SendKey(Key.LeftShift, false);
                }

                return true;
            case CapsLockMode.Remap:
                var target = settings.RemapTarget;
                if (!target.HasValue)
                {
                    return true;
                }

                if (isKeyDown)
                {
                    _capsRemappedKey = target;
                    SendKey(target.Value, true);
                }
                else if (isKeyUp && _capsRemappedKey.HasValue)
                {
                    SendKey(_capsRemappedKey.Value, false);
                    _capsRemappedKey = null;
                }

                return true;
            default:
                return false;
        }
    }

    private CapsLockSettings? GetEffectiveCapsLockSettings()
    {
        var active = _activeProfile?.CapsLock;
        if (active is { IsEnabled: true } enabledActive && enabledActive.Mode != CapsLockMode.Normal)
        {
            return enabledActive;
        }

        var global = _windowsProfile?.CapsLock;
        if (global is { IsEnabled: true } enabledGlobal && enabledGlobal.Mode != CapsLockMode.Normal)
        {
            return enabledGlobal;
        }

        if (active?.IsEnabled == true)
        {
            return active;
        }

        if (global?.IsEnabled == true)
        {
            return global;
        }

        return null;
    }

    private bool HandleWindowsLauncher(int vkCode, bool isKeyDown)
    {
        var profile = _windowsProfile;
        if (profile is null || !profile.WindowsLauncher.IsEnabled || !isKeyDown)
        {
            return false;
        }

        bool winPressed = (NativeMethods.GetAsyncKeyState(KeyInteropUtilities.ToVirtualKey(Key.LWin)) & 0x8000) != 0
            || (NativeMethods.GetAsyncKeyState(KeyInteropUtilities.ToVirtualKey(Key.RWin)) & 0x8000) != 0;

        if (!winPressed)
        {
            return false;
        }

        var key = KeyInteropUtilities.FromVirtualKey(vkCode);
        if (key is null)
        {
            return false;
        }

        if (!profile.WindowsLauncher.Launchers.TryGetValue(key.Value, out var binding))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(binding.Path))
        {
            return false;
        }

        LaunchProcess(binding);
        return true;
    }

    private static void LaunchProcess(LauncherBinding binding)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = binding.Path,
                Arguments = binding.Arguments,
                UseShellExecute = true,
                Verb = binding.RunAsAdmin ? "runas" : string.Empty
            };

            Process.Start(startInfo);
        }
        catch
        {
            // Swallow launch failures to avoid disruptive user experience.
        }
    }

    private void FireTapKey(Key key)
    {
        Task.Run(() =>
        {
            SendKey(key, true);
            Thread.Sleep(_random.Next(31, 53));
            SendKey(key, false);
        });
    }

    private void SendKey(Key key, bool isKeyDown, bool bypassHook = true)
    {
        var virtualKey = KeyInteropUtilities.ToVirtualKey(key);
        if (virtualKey == 0)
        {
            LogDebug($"    SendKey({key}, {(isKeyDown ? "DOWN" : "UP")}) - FAILED: VirtualKey=0");
            return;
        }

        var flags = isKeyDown ? 0u : NativeMethods.KeyEventFlags.KEYEVENTF_KEYUP;

        if (IsExtendedKey(key))
        {
            flags |= NativeMethods.KeyEventFlags.KEYEVENTF_EXTENDEDKEY;
        }

        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.InputType.INPUT_KEYBOARD,
            U = new NativeMethods.InputUnion
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = virtualKey,
                    wScan = (ushort)NativeMethods.MapVirtualKey(virtualKey, 0),
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = bypassHook ? NativeMethods.INPUT_IGNORE : IntPtr.Zero
                }
            }
        };

        var result = NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
        LogDebug($"    SendKey({key}, {(isKeyDown ? "DOWN" : "UP")}) - VK=0x{virtualKey:X2}, Result={result}");
    }

    private static bool IsExtendedKey(Key key)
    {
        return key is Key.RightAlt or Key.RightCtrl or Key.Insert or Key.Delete or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.Up or Key.Down or Key.Left or Key.Right
            or Key.NumLock or Key.PrintScreen or Key.Divide;
    }

    private void ReleaseOverrideKeys()
    {
        foreach (var (_, state) in _activeRightMouseOverrides)
        {
            SendKey(state.TargetKey, false);
        }

        _activeRightMouseOverrides.Clear();
    }

    private void ResetMouseStates()
    {
        foreach (var state in _mouseStates.Values)
        {
            ResetMouseState(state);
        }
    }

    private void ResetState()
    {
        ReleaseOverrideKeys();
        ResetMouseStates();
        ReleaseCapsState();
        ReleaseHoldBreathState();
        _altPressed = false;
        _rightButtonPressed = false;
    }

    private void ReleaseCapsState()
    {
        if (_capsShiftEngaged)
        {
            SendKey(Key.LeftShift, false);
            _capsShiftEngaged = false;
        }

        if (_capsRemappedKey.HasValue)
        {
            SendKey(_capsRemappedKey.Value, false);
            _capsRemappedKey = null;
        }
    }

    private void ResetMouseState(MouseButtonState state)
    {
        CancelHoldTimer(state);
        state.HoldTriggered = false;
        state.DownTick = null;
        state.AltHandled = false;
    }

    private void HandleRightClickHoldBreathDown()
    {
        var profile = _activeProfile;
        if (profile is null || !profile.RightClickHoldBreath.IsEnabled)
        {
            return;
        }

        // Cancel any pending delay
        CancelHoldBreathDelay();

        var settings = profile.RightClickHoldBreath;
        var delay = Math.Max(0, settings.DelayMilliseconds);

        if (delay > 0)
        {
            // Start delay timer
            var cts = new CancellationTokenSource();
            _holdBreathDelayToken = cts;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    return;
                }

                lock (_syncRoot)
                {
                    if (!_isRunning || cts.IsCancellationRequested || !_rightButtonPressed)
                    {
                        return;
                    }

                    ActivateHoldBreath(settings);
                }
            });
        }
        else
        {
            // No delay, activate immediately
            ActivateHoldBreath(settings);
        }
    }

    private void HandleRightClickHoldBreathUp()
    {
        // Cancel any pending delay
        CancelHoldBreathDelay();

        var profile = _activeProfile;
        if (profile is null || !profile.RightClickHoldBreath.IsEnabled || !_holdBreathActive)
        {
            return;
        }

        var settings = profile.RightClickHoldBreath;

        if (settings.Mode == HoldBreathMode.Hold)
        {
            // Release the key immediately
            SendKey(settings.HoldBreathKey, false);
            _holdBreathActive = false;
        }
        // Toggle mode does nothing on mouse up - game handles the state reset
    }

    private void ActivateHoldBreath(RightClickHoldBreathSettings settings)
    {
        if (_holdBreathActive)
        {
            return;
        }

        _holdBreathActive = true;

        if (settings.Mode == HoldBreathMode.Hold)
        {
            // Press and hold the key
            SendKey(settings.HoldBreathKey, true);
        }
        else if (settings.Mode == HoldBreathMode.Toggle)
        {
            // Toggle mode: Send a quick tap (down + up) with random delay
            Task.Run(() =>
            {
                SendKey(settings.HoldBreathKey, true);
                Thread.Sleep(_random.Next(20, 31)); // 20-30ms random delay
                SendKey(settings.HoldBreathKey, false);
            });
            _holdBreathActive = false; // Reset immediately since we're just tapping
        }
    }

    private void CancelHoldBreathDelay()
    {
        if (_holdBreathDelayToken is null)
        {
            return;
        }

        try
        {
            _holdBreathDelayToken.Cancel();
        }
        catch
        {
        }
        finally
        {
            _holdBreathDelayToken.Dispose();
            _holdBreathDelayToken = null;
        }
    }

    private void ReleaseHoldBreathState()
    {
        CancelHoldBreathDelay();

        if (_holdBreathActive)
        {
            var profile = _activeProfile;
            if (profile?.RightClickHoldBreath.IsEnabled == true)
            {
                SendKey(profile.RightClickHoldBreath.HoldBreathKey, false);
            }
            _holdBreathActive = false;
        }
    }

    private sealed class MouseButtonState
    {
        public long? DownTick { get; set; }
        public bool AltHandled { get; set; }
        public CancellationTokenSource? HoldToken { get; set; }
        public bool HoldTriggered { get; set; }
    }

    private sealed class RightMouseOverrideState
    {
        public Key TargetKey { get; set; }
        public bool SuppressOriginal { get; set; }
    }

    private static void LogDebug(string message)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            File.AppendAllText(LogPath, $"[{timestamp}] {message}\n");
        }
        catch
        {
            // Ignore logging errors
        }
    }
}
