namespace sWinShortcuts.Services;

public interface ILoggerService
{
    bool IsEnabled { get; set; }
    void Log(string message);
}
