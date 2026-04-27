using System.Windows;
using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class FakeSystemTrayService : ISystemTrayService
{
    public List<(bool IsProfileActive, string? ProfileName)> StatusUpdates { get; } = [];

    public void Initialize(Window mainWindow)
    {
    }

    public void ShowBalloon(string title, string message)
    {
    }

    public void UpdateStatus(bool isProfileActive, string? profileName)
    {
        StatusUpdates.Add((isProfileActive, profileName));
    }

    public void SetIcon(string iconPath)
    {
    }

    public void Dispose()
    {
    }
}
