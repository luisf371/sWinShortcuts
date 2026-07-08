using System;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public interface IInputHookService : IDisposable
{
    event EventHandler<Profile?>? ActiveProfileChanged;

    /// <summary>
    /// Enables the hook-loss watchdog (default true). When false, the watchdog neither probes nor
    /// re-installs hooks — a troubleshooting switch to rule it out as an interference source.
    /// Live-togglable; takes effect on the next watchdog period.
    /// </summary>
    bool HookWatchdogEnabled { get; set; }

    void Start();

    void Stop();

    void ActivateProfile(Profile profile);

    void DeactivateProfile();

    void SetWindowsProfile(Profile profile);
}
