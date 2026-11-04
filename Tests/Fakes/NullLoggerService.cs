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
            // The input executor logs from its worker thread while tests log from theirs.
            lock (Messages)
            {
                Messages.Add(message);
            }
        }
    }
}
