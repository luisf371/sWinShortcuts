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
    /// Raised on the hook thread with the physical right button's new state (true = down), but ONLY while
    /// armed via <see cref="SetRightButtonObservation"/>. Observation-only (AHK ~RButton semantics): the
    /// right button is never suppressed and always passes through to the focused game. Fires at human
    /// click frequency, so the null-checked invoke costs nothing on the mouse-hook path.
    /// </summary>
    event EventHandler<bool>? RightButtonStateChanged;

    /// <summary>
    /// Raised when the Rapid Fire arm or its status MAY have changed (arm/disarm/re-target, owner
    /// release, foreground publication, activation generation catch-up, session boundaries).
    /// The event carries no payload on purpose: handlers must re-query GetRapidFireArmStatus()
    /// and dedup — spurious raises are permitted. Raised from many threads (hook dispatcher,
    /// input worker, reconcile/UI thread, SystemEvents, pool); handlers must be non-blocking
    /// (enqueue-only) and exception-isolated.
    /// </summary>
    event EventHandler? RapidFireArmChanged;

    /// <summary>Current Rapid Fire arm snapshot (see <see cref="RapidFireArmStatus"/>).</summary>
    RapidFireArmStatus GetRapidFireArmStatus();

    /// <summary>
    /// Arms/disarms right-button observation for the crosshair overlay's hide-while-RMB-held mode.
    /// While disabled (the default, and whenever no crosshair profile is active) the mouse hook does
    /// zero extra work. Arming re-publishes the CURRENT physical button state once, so a button-up
    /// swallowed while disarmed cannot leave the overlay stuck hidden.
    /// </summary>
    void SetRightButtonObservation(bool enabled);

    /// <summary>
    /// Sets (or clears, when null) the GLOBAL key that toggles the active profile's color preset. Detected
    /// on the low-level keyboard hook and passed through to applications. Live-updatable.
    /// </summary>
    void SetColorToggleKey(Key? key);

    /// <summary>
    /// Sets (or clears, when null) the GLOBAL key that arms or disarms Rapid Fire for the active
    /// profile. Detected on the low-level keyboard hook and passed through to applications.
    /// </summary>
    void SetRapidFireToggleKey(Key? key);

    /// <summary>
    /// Enables the hook-loss watchdog (default true). When false, the watchdog neither probes nor
    /// re-installs hooks — a troubleshooting switch to rule it out as an interference source.
    /// Live-togglable; takes effect on the next watchdog period.
    /// </summary>
    bool HookWatchdogEnabled { get; set; }

    /// <summary>
    /// Global gate for non-1:1 automation (Auto-Run, Anti-AFK, Hold-Breath, Rapid Fire, and un-suppressed key
    /// mappings). When false those features are inert and any held gated state is released; every
    /// mapping is forced 1:1. Live-togglable from Settings; persisted as [App] AdvancedMode.
    /// </summary>
    bool AdvancedModeEnabled { get; set; }

    void Start();

    void Stop();

    void ActivateProfile(Profile profile, long foregroundGeneration);

    void DeactivateProfile(long foregroundGeneration);

    /// <summary>
    /// Reconciles live edits against already-owned runtime state. Recorded releases remain unconditional;
    /// disabling or rebinding a feature only prevents new work and releases state that feature owns.
    /// </summary>
    void ReconcileProfileSettings(Profile profile, ProfileChangeKind changeKind);

    /// <summary>
    /// Releases any active FOREGROUND Auto-Run. Called on a foreground change that leaves the active
    /// profile, BEFORE color work, so a held W can't briefly leak into the incoming window (profile
    /// deactivation also releases it, but only after that work). No-op if no Auto-Run is active.
    /// </summary>
    void ReleaseForegroundAutoRun();

    /// <summary>
    /// Releases all foreground profile-owned input state without changing the active profile. The caller
    /// publishes the final foreground generation first so new presses fail closed until activation catches up.
    /// Background Auto-Run remains active.
    /// </summary>
    void ReleaseForegroundState();

    /// <summary>
    /// Publishes the current foreground window identity (HWND + owning PID + normalized exe), resolved
    /// OFF the low-level hook thread by the foreground watcher. Lets Auto-Run activation fail closed with
    /// a cheap live HWND/PID compare against this snapshot instead of a Process.GetProcessById on the hook
    /// thread (A1). Cheap and thread-safe; call on every foreground change.
    /// </summary>
    void SetForegroundIdentity(
        IntPtr windowHandle,
        uint processId,
        string? normalizedExecutable,
        long foregroundGeneration);

    void SetWindowsProfile(Profile profile);
}
