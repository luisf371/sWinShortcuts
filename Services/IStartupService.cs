using System;

namespace sWinShortcuts.Services;

public interface IStartupService
{
    StartupState GetState();
    bool Apply(bool startWithWindows, bool startAsAdmin, out string? errorMessage);
}

public sealed record StartupState(bool StartWithWindows, bool StartAsAdmin);

