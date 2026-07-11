using System;
using System.Windows.Input;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public interface IInputHookService : IDisposable
{
    event EventHandler<Profile?>? ActiveProfileChanged;

    /// <summary>
    /// Raised on the hook thread when the user presses the configured GLOBAL color-variant toggle key.
    /// Fires exactly once per physical press (typematic repeats are ignored). The subscriber flips the
    /// active profile's applied color preset (Primary&lt;-&gt;Secondary) and re-applies.
    /// </summary>
    event EventHandler? ColorVariantToggleRequested;

    /// <summary>
    /// Sets (or clears, when null) the GLOBAL key that toggles the active profile's color preset. Detected
    /// on the low-level keyboard hook and suppressed from applications. Live-updatable.
    /// </summary>
    void SetColorToggleKey(Key? key);

    /// <summary>
    /// Enables the hook-loss watchdog (default true). When false, the watchdog neither probes nor
    /// re-installs hooks — a troubleshooting switch to rule it out as an interference source.
    /// Live-togglable; takes effect on the next watchdog period.
    /// </summary>
    bool HookWatchdogEnabled { get; set; }

    /// <summary>
    /// Global gate for non-1:1 automation (Auto-Run, Anti-AFK, Hold-Breath, and un-suppressed key
    /// mappings). When false those features are inert and any held gated state is released; every
    /// mapping is forced 1:1. Live-togglable from Settings; persisted as [App] AdvancedMode.
    /// </summary>
    bool AdvancedModeEnabled { get; set; }

    void Start();

    void Stop();

    void ActivateProfile(Profile profile);

    void DeactivateProfile();

    /// <summary>
    /// Releases any active FOREGROUND Auto-Run. Called on a foreground change that leaves the active
    /// profile, BEFORE color work, so a held W can't briefly leak into the incoming window (profile
    /// deactivation also releases it, but only after that work). No-op if no Auto-Run is active.
    /// </summary>
    void ReleaseForegroundAutoRun();

    /// <summary>
    /// Publishes the current foreground window identity (HWND + owning PID + normalized exe), resolved
    /// OFF the low-level hook thread by the foreground watcher. Lets Auto-Run activation fail closed with
    /// a cheap live HWND/PID compare against this snapshot instead of a Process.GetProcessById on the hook
    /// thread (A1). Cheap and thread-safe; call on every foreground change.
    /// </summary>
    void SetForegroundIdentity(IntPtr windowHandle, uint processId, string? normalizedExecutable);

    void SetWindowsProfile(Profile profile);
}
