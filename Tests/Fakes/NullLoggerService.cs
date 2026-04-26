using sWinShortcuts.Services;

namespace Tests.Fakes;

/// <summary>
/// No-op logger for testing.
/// </summary>
public sealed class NullLoggerService : ILoggerService
{
    public bool IsEnabled { get; set; }
    
    public List<string> Messages { get; } = [];

    public void Log(string message)
    {
        if (IsEnabled)
        {
            Messages.Add(message);
        }
    }
}
