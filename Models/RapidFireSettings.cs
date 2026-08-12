namespace sWinShortcuts.Models;

public sealed class RapidFireSettings
{
    public const int MinIntervalMilliseconds = 25;
    public const int MaxIntervalMilliseconds = 250;
    public const int MaxJitterMilliseconds = 20;
    public const int DefaultIntervalMilliseconds = 90;
    public const int DefaultJitterMilliseconds = 10;

    public bool IsEnabled { get; set; }

    public int IntervalMilliseconds { get; set; } = DefaultIntervalMilliseconds;

    public int JitterMilliseconds { get; set; } = DefaultJitterMilliseconds;
}
