using sWinShortcuts.Services;
using Xunit;

namespace Tests;

// P8 rework: unit tests for InputHookService.DecideWatchdogAction, the pure two-stage decision
// function of the hook-loss watchdog. Stage 1 (sink closed) may only escalate suspicion to opening
// a raw-input liveness sink — never straight to a reinstall, because GetLastInputInfo is global and
// cannot distinguish "this device is idle" from "this hook is dead". Stage 2 (sink open) reinstalls
// only on proof: raw input for the device arrived while its hook stayed silent. The Win32 plumbing
// (RegisterRawInputDevices, SetWindowsHookEx swap) stays untested, consistent with the rest of this
// repo's Win32-boundary testing approach.
public class WatchdogDecisionTests
{
    private const double StaleThresholdMs = 30_000;
    private const uint FreshThresholdMs = 2_000;

    private static InputHookService.WatchdogAction Decide(double hookIdleMs, uint systemInputAgeMs, bool sinkOpen, double rawInputAgeMs)
        => InputHookService.DecideWatchdogAction(hookIdleMs, systemInputAgeMs, sinkOpen, rawInputAgeMs, StaleThresholdMs, FreshThresholdMs);

    // ---- Stage 1: sink closed — suspicion handling ----

    [Fact]
    public void SinkClosed_FreshSystemInput_StaleHook_OpensSink()
    {
        // The old design reinstalled here — the false-positive storm during mouse-only gaming.
        // Now it may only open the sink and ask the device directly.
        Assert.Equal(InputHookService.WatchdogAction.OpenSink, Decide(30_001, 0, sinkOpen: false, rawInputAgeMs: double.MaxValue));
    }

    [Fact]
    public void SinkClosed_FreshSystemInput_ActiveHook_DoesNothing()
    {
        Assert.Equal(InputHookService.WatchdogAction.None, Decide(1_000, 0, sinkOpen: false, rawInputAgeMs: double.MaxValue));
    }

    [Fact]
    public void SinkClosed_StaleSystemInput_StaleHook_DoesNothing()
    {
        // System itself hasn't seen fresh input either (e.g. lock screen, AFK) — "hook died" is
        // indistinguishable from "nobody is providing input"; not even suspicion.
        Assert.Equal(InputHookService.WatchdogAction.None, Decide(60_000, 5_000, sinkOpen: false, rawInputAgeMs: double.MaxValue));
    }

    [Fact]
    public void SinkClosed_StaleSystemInput_ActiveHook_DoesNothing()
    {
        Assert.Equal(InputHookService.WatchdogAction.None, Decide(500, 5_000, sinkOpen: false, rawInputAgeMs: double.MaxValue));
    }

    [Fact]
    public void SinkClosed_HookIdleExactlyAtThreshold_DoesNothing()
    {
        // Strict '>' — exactly-at-threshold is not yet stale.
        Assert.Equal(InputHookService.WatchdogAction.None, Decide(StaleThresholdMs, 0, sinkOpen: false, rawInputAgeMs: double.MaxValue));
    }

    [Fact]
    public void SinkClosed_HookIdleJustPastThreshold_OpensSink()
    {
        Assert.Equal(InputHookService.WatchdogAction.OpenSink, Decide(StaleThresholdMs + 0.001, 0, sinkOpen: false, rawInputAgeMs: double.MaxValue));
    }

    [Fact]
    public void SinkClosed_SystemInputAgeExactlyAtFreshThreshold_DoesNothing()
    {
        // Strict '<' — exactly-at-threshold does not count as "fresh".
        Assert.Equal(InputHookService.WatchdogAction.None, Decide(60_000, FreshThresholdMs, sinkOpen: false, rawInputAgeMs: double.MaxValue));
    }

    // ---- Stage 2: sink open — proof handling ----

    [Fact]
    public void SinkOpen_HookAliveAgain_ClosesSink()
    {
        // A hook event landed since the sink opened: proof of life, regardless of raw input.
        Assert.Equal(InputHookService.WatchdogAction.CloseSink, Decide(1_000, 0, sinkOpen: true, rawInputAgeMs: 100));
    }

    [Fact]
    public void SinkOpen_HookIdleExactlyAtThreshold_ClosesSink()
    {
        // '<=' on the way back: at-threshold counts as alive so open/close cannot oscillate on the boundary.
        Assert.Equal(InputHookService.WatchdogAction.CloseSink, Decide(StaleThresholdMs, 0, sinkOpen: true, rawInputAgeMs: 100));
    }

    [Fact]
    public void SinkOpen_StaleHook_FreshRawInput_Reinstalls()
    {
        // THE proof case: the device demonstrably produced input and the hook saw none of it.
        Assert.Equal(InputHookService.WatchdogAction.Reinstall, Decide(60_000, 0, sinkOpen: true, rawInputAgeMs: 100));
    }

    [Fact]
    public void SinkOpen_StaleHook_NoRawInputSinceOpen_DoesNothing()
    {
        // Device genuinely idle (raw tick never stamped -> MaxValue age): idle is not death.
        Assert.Equal(InputHookService.WatchdogAction.None, Decide(60_000, 0, sinkOpen: true, rawInputAgeMs: double.MaxValue));
    }

    [Fact]
    public void SinkOpen_StaleHook_StaleRawInput_DoesNothing()
    {
        // Raw input was seen once but is old — the device went idle again; keep watching.
        Assert.Equal(InputHookService.WatchdogAction.None, Decide(60_000, 0, sinkOpen: true, rawInputAgeMs: 10_000));
    }

    [Fact]
    public void SinkOpen_RawAgeExactlyAtFreshThreshold_DoesNothing()
    {
        // Strict '<' — exactly-at-threshold raw input does not count as proof.
        Assert.Equal(InputHookService.WatchdogAction.None, Decide(60_000, 0, sinkOpen: true, rawInputAgeMs: FreshThresholdMs));
    }

    [Fact]
    public void SinkOpen_SystemInputAgeIsIrrelevant()
    {
        // Once the sink is open the per-device raw tick is strictly better evidence than the global
        // GetLastInputInfo, which is deliberately ignored (a stale system age must not veto proof).
        Assert.Equal(InputHookService.WatchdogAction.Reinstall, Decide(60_000, 50_000, sinkOpen: true, rawInputAgeMs: 100));
    }

    [Fact]
    public void MouseOnlyGamingScenario_KeyboardNeverReinstalls()
    {
        // Regression for the shipped false-positive storm: 33s of mouse-only aiming, keyboard idle.
        // Tick 1: suspicion opens the sink. Tick 2+: no keyboard raw input ever arrives, so the
        // decision stays None forever — the old design reinstalled the live hook here every ~40s.
        Assert.Equal(InputHookService.WatchdogAction.OpenSink, Decide(33_000, 0, sinkOpen: false, rawInputAgeMs: double.MaxValue));
        Assert.Equal(InputHookService.WatchdogAction.None, Decide(43_000, 0, sinkOpen: true, rawInputAgeMs: double.MaxValue));
        Assert.Equal(InputHookService.WatchdogAction.None, Decide(120_000, 0, sinkOpen: true, rawInputAgeMs: double.MaxValue));
    }

    [Fact]
    public void TrueHookLossScenario_ReinstallsOnceDeviceActive()
    {
        // Hook silently removed mid-gaming: suspicion opens the sink; the very next tick sees fresh
        // raw mouse input with the hook still silent -> reinstall.
        Assert.Equal(InputHookService.WatchdogAction.OpenSink, Decide(31_000, 0, sinkOpen: false, rawInputAgeMs: double.MaxValue));
        Assert.Equal(InputHookService.WatchdogAction.Reinstall, Decide(41_000, 0, sinkOpen: true, rawInputAgeMs: 50));
    }
}
