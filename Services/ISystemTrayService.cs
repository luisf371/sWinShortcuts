using System;
using System.Windows;

namespace sWinShortcuts.Services;

public interface ISystemTrayService : IDisposable
{
    void Initialize(Window mainWindow);

    void ShowBalloon(string title, string message);

    void UpdateStatus(bool isProfileActive, string? profileName);
}
