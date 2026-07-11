using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class RightClickHoldBreathSettings
{
    public bool IsEnabled { get; set; } = false;

    public Key HoldBreathKey { get; set; } = Key.LeftShift;

    public HoldBreathMode Mode { get; set; } = HoldBreathMode.Hold;

    public int DelayMilliseconds { get; set; } = 5;

    public InputTrigger PanicTrigger { get; set; } = InputTrigger.None;

    public bool SuppressEarlyCancelInput { get; set; } = true;
}

public enum HoldBreathMode
{
    Hold = 0,
    Toggle = 1
}