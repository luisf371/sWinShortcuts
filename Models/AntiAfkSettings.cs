namespace sWinShortcuts.Models;

public sealed class AntiAfkSettings
{
    // volatile backing fields: written on the UI thread (profile edit / load), read live on the Anti-AFK
    // timer thread every tick without a lock, so a later live edit needs a happens-before guarantee.
    private volatile bool _isEnabled;
    private volatile int _intervalMinutes = 5;

    public bool IsEnabled { get => _isEnabled; set => _isEnabled = value; }

    // UI slider range is 1..15 minutes; clamped on load + use (see IniProfileStore.DeserializeAntiAfk).
    public int IntervalMinutes { get => _intervalMinutes; set => _intervalMinutes = value; }
}
