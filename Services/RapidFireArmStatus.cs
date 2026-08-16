namespace sWinShortcuts.Services;

/// <summary>
/// Snapshot of the Rapid Fire arm for status UI. "Not ready" is deliberately broader than
/// "another app focused": a foreground publication ahead of profile activation, or a
/// same-profile republish still in flight, also mean "armed, but it will not click right now".
/// </summary>
public enum RapidFireArmStatus
{
    /// <summary>No live arm (disarmed, or the owning profile/runtime was torn down).</summary>
    Off = 0,

    /// <summary>Armed, but the owning profile is not the settled active profile — presses will not click.</summary>
    ArmedNotReady = 1,

    /// <summary>Armed and the owning profile is the settled active profile — presses will click.</summary>
    Ready = 2
}
