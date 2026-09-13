using System.Threading;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.Services.Input;

internal readonly record struct RecordedMacroEvent(
    long Timestamp, int Message, int VirtualKey, uint ScanCode,
    uint Flags, uint MouseData, int X, int Y, int WheelDelta);

/// <summary>One hook-thread writer; cancellation only closes admission. Rows are built off the hook.</summary>
internal sealed class MacroRecorder(int availableRows, long timestampFrequency)
{
    private const int CAPTURING = -1;
    private readonly RecordedMacroEvent[] _events = new RecordedMacroEvent[Math.Clamp(availableRows, 0, MacroValidation.MaxSteps)];
    private readonly int _availableRows = Math.Clamp(availableRows, 0, MacroValidation.MaxSteps);
    private readonly long _timestampFrequency = timestampFrequency > 0
        ? timestampFrequency : throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
    private readonly bool[] _heldKeys = new bool[256];
    private readonly bool[] _heldButtons = new bool[6];
    private readonly bool[] _preheldKeys = new bool[256];
    private readonly bool[] _preheldButtons = new bool[6];
    private long _start;
    private long _previous;
    private int _count;
    private int _rows;
    private int _heldCount;
    private int _writing;
    // One atomic publication closes admission and carries the first stop reason together.
    private int _stopReason = (int)MacroRecordingEndReason.Stopped;

    internal bool IsCapturing => Volatile.Read(ref _stopReason) == CAPTURING;
    internal int Count => Volatile.Read(ref _count);
    internal MacroRecordingEndReason EndReason
    {
        get
        {
            var reason = Volatile.Read(ref _stopReason);
            return reason == CAPTURING ? MacroRecordingEndReason.Stopped : (MacroRecordingEndReason)reason;
        }
    }

    internal void Begin(long timestamp, bool[] preheldKeys, bool[] preheldButtons)
    {
        if (IsCapturing) throw new InvalidOperationException("A recording is already in progress.");
        if (preheldKeys.Length != _preheldKeys.Length || preheldButtons.Length != _preheldButtons.Length)
            throw new ArgumentException("Preheld snapshots must include all virtual keys and mouse buttons.");
        Array.Copy(preheldKeys, _preheldKeys, _preheldKeys.Length);
        Array.Copy(preheldButtons, _preheldButtons, _preheldButtons.Length);
        Array.Clear(_heldKeys);
        Array.Clear(_heldButtons);
        _count = _rows = _heldCount = 0;
        _start = _previous = timestamp;
        Volatile.Write(ref _stopReason, _availableRows == 0 ? (int)MacroRecordingEndReason.RowLimit : CAPTURING);
    }

    internal void RequestStop(MacroRecordingEndReason reason)
    {
        Interlocked.CompareExchange(ref _stopReason, (int)reason, CAPTURING);
    }

    internal void CheckDuration(long timestamp)
    {
        if ((timestamp - _start) / (double)_timestampFrequency >= 600)
            RequestStop(MacroRecordingEndReason.DurationLimit);
    }

    // Called by the actual Stop button's preview interaction, after its low-level DOWN.
    internal void StopFromControl(int message, int virtualKey = 0)
    {
        if (!IsCapturing) return;
        Volatile.Write(ref _writing, 1);
        try
        {
            if (!IsCapturing) return;
            if (_count > 0)
            {
                var last = _events[_count - 1];
                var mouseGesture = message == NativeMethods.WM_LBUTTONDOWN;
                var keyGesture = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN && virtualKey is 0x0D or 0x20;
                if (last.Message == message && (mouseGesture || (keyGesture && last.VirtualKey == virtualKey)))
                    Volatile.Write(ref _count, _count - 1);
            }
            RequestStop(MacroRecordingEndReason.Stopped);
        }
        finally
        {
            Volatile.Write(ref _writing, 0);
        }
    }

    internal void Capture(in RecordedMacroEvent input)
    {
        if (!IsCapturing) return;
        Volatile.Write(ref _writing, 1);
        try
        {
            if (!IsCapturing) return;
            CheckDuration(input.Timestamp);
            if (!IsCapturing || !TryDecode(input, out var step, out var mouse)) return;

            var keyEdge = step.Kind is MacroStepKind.KeyDown or MacroStepKind.KeyUp;
            var buttonEdge = step.Kind is MacroStepKind.MouseDown or MacroStepKind.MouseUp;
            var isDown = step.Kind is MacroStepKind.KeyDown or MacroStepKind.MouseDown;
            var index = keyEdge ? KeyInteropUtilities.ToVirtualKey(step.Key) : (int)(step.MouseButton ?? 0);
            var held = keyEdge ? _heldKeys : _heldButtons;
            var preheld = keyEdge ? _preheldKeys : _preheldButtons;
            if (keyEdge || buttonEdge)
            {
                if ((uint)index >= held.Length) return;
                if (preheld[index])
                {
                    if (!isDown) preheld[index] = false;
                    return;
                }
                if (!isDown && !held[index]) return;
                if (buttonEdge && isDown && held[index]) return;
            }

            var heldDelta = keyEdge || buttonEdge ? (isDown ? (held[index] ? 0 : 1) : -1) : 0;
            var addedRows = 1 + (mouse ? 1 : 0) + (_count > 0 && GapMs(input.Timestamp, _previous) > 0 ? 1 : 0);
            if (_count == _events.Length || _rows + addedRows + _heldCount + heldDelta > _availableRows)
            {
                RequestStop(MacroRecordingEndReason.RowLimit);
                return;
            }
            _events[_count] = input;
            _rows += addedRows;
            _heldCount += heldDelta;
            if (keyEdge || buttonEdge) held[index] = isDown;
            _previous = input.Timestamp;
            Volatile.Write(ref _count, _count + 1);
            if (_rows == _availableRows || _count == _events.Length)
                RequestStop(MacroRecordingEndReason.RowLimit);
        }
        finally
        {
            Volatile.Write(ref _writing, 0);
        }
    }

    internal (MacroStep[] Steps, bool Balanced) BuildSteps()
    {
        if (IsCapturing) throw new InvalidOperationException("Finish recording before building its steps.");
        SpinWait.SpinUntil(() => Volatile.Read(ref _writing) == 0);
        var rows = new List<MacroStep>(_availableRows);
        var keys = new HashSet<Key>();
        var buttons = new HashSet<MouseButton>();
        long previous = 0;
        for (var i = 0; i < _count; i++)
        {
            var input = _events[i];
            if (!TryDecode(input, out var step, out var mouse)) continue;
            var gap = i == 0 ? 0 : GapMs(input.Timestamp, previous);
            if (gap > 0) rows.Add(new MacroStep { Kind = MacroStepKind.Wait, DurationMs = gap });
            if (mouse) rows.Add(new MacroStep { Kind = MacroStepKind.MoveTo, X = input.X, Y = input.Y });
            rows.Add(step);
            if (step.Kind == MacroStepKind.KeyDown) keys.Add(step.Key);
            else if (step.Kind == MacroStepKind.KeyUp) keys.Remove(step.Key);
            else if (step.Kind == MacroStepKind.MouseDown) buttons.Add(step.MouseButton.GetValueOrDefault());
            else if (step.Kind == MacroStepKind.MouseUp) buttons.Remove(step.MouseButton.GetValueOrDefault());
            previous = input.Timestamp;
        }
        var balanced = keys.Count != 0 || buttons.Count != 0;
        foreach (var key in keys) rows.Add(new MacroStep { Kind = MacroStepKind.KeyUp, Key = key });
        foreach (var button in buttons) rows.Add(new MacroStep { Kind = MacroStepKind.MouseUp, MouseButton = button });
        return (rows.ToArray(), balanced);
    }

    private int GapMs(long next, long previous) =>
        (int)Math.Clamp(Math.Round((next - previous) * 1000.0 / _timestampFrequency), 0, MacroValidation.MaxDurationMs);

    private static bool TryDecode(in RecordedMacroEvent input, out MacroStep step, out bool mouse)
    {
        mouse = false;
        step = default;
        if (input.Message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN or NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
        {
            if (input.VirtualKey is < 1 or > 254 or 0x7B ||
                (input.Flags & (uint)(NativeMethods.KbdLlFlags.LLKHF_INJECTED | NativeMethods.KbdLlFlags.LLKHF_LOWER_IL_INJECTED)) != 0) return false;
            var key = KeyInteropUtilities.FromVirtualKey(input.VirtualKey);
            if (!key.HasValue) return false;
            step = new MacroStep
            {
                Kind = input.Message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN ? MacroStepKind.KeyDown : MacroStepKind.KeyUp,
                Key = key.Value
            };
            return true;
        }
        if ((input.Flags & (uint)(NativeMethods.MouseLlFlags.LLMHF_INJECTED | NativeMethods.MouseLlFlags.LLMHF_LOWER_IL_INJECTED)) != 0)
            return false;
        mouse = true;
        if (input.Message is NativeMethods.WM_MOUSEWHEEL or NativeMethods.WM_MOUSEHWHEEL)
        {
            if (input.WheelDelta is 0 or < short.MinValue or > short.MaxValue) return false;
            step = new MacroStep { Kind = MacroStepKind.MouseWheel, WheelDelta = input.WheelDelta,
                HorizontalWheel = input.Message == NativeMethods.WM_MOUSEHWHEEL };
            return true;
        }
        if (!TryDecodeButton(input.Message, input.MouseData, out var button, out var down)) return false;
        step = new MacroStep { Kind = down ? MacroStepKind.MouseDown : MacroStepKind.MouseUp, MouseButton = button };
        return true;
    }

    internal static bool TryDecodeButton(int message, uint mouseData, out MouseButton button, out bool down)
    {
        down = message is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_XBUTTONDOWN;
        button = message switch
        {
            NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP => MouseButton.Left,
            NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP => MouseButton.Right,
            NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP => MouseButton.Middle,
            NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP when (mouseData >> 16) == 1 => MouseButton.XButton1,
            NativeMethods.WM_XBUTTONDOWN or NativeMethods.WM_XBUTTONUP when (mouseData >> 16) == 2 => MouseButton.XButton2,
            _ => 0
        };
        return button != 0;
    }
}
