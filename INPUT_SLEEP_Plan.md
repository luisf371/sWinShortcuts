# INPUT_SLEEP_Plan.md — Input & Sleep Path Implementation Plan

**Source:** 2026-07-07 input+sleep deep dive (codex-verified, all findings CONFIRMED; F3/F4 partial on nuance only).
**Plan review status:** CLEAN after 3 codex rounds (gpt-5.5 high, read-only). R1: 5 amendments (P7 pairing; P8 dispatcher capture, tick atomicity/init, install-before-unhook, post-reinstall ReleaseAllState). R2: P8 fail-open swap-window flags (non-suppressing mouse side effects made overlap idempotency insufficient). R3: CLEAN, no findings.
**Baseline:** `fixupv2` @ `2af631a` == `origin/main`. Build 0 errors (pre-existing CA1416 only), 83/83 tests green.
**App intent:** gaming — reliable timers and execution, zero regression, no stuck keys, bindings never silently die.
**Scope:** `Services\InputHookService.cs`, `Interop\NativeMethods.cs`, `sWinShortcuts.csproj` (one property), `App.xaml.cs`/`Services\ProfileActivationService.cs` untouched unless noted.

---

## 0. Invariants that every phase MUST preserve

- **I1 — Injected-filter-first:** both hook callbacks early-out on `LLKHF_INJECTED` / `LLMHF_INJECTED` / `INPUT_IGNORE` *before* taking any lock. This is what makes `SendInput` under a subsystem lock bounded and deadlock-free. Never reorder.
- **I2 — Every injected DOWN is recorded before or atomically with injection** (`_activeCombinedOverrides`, `_capsRemappedKey`, `_holdBreathInjectedKey`, `_transientTapKeys`) so a release path can always force the UP.
- **I3 — Release paths are unconditional** (no `IsEnabled`/profile gates on UP paths).
- **I4 — `_isRunning` flips false before `ReleaseAllState` in `Stop()`;** injection sites re-check it under their subsystem lock.
- **I5 — Lock ordering:** `_profileLock` is outermost (lifecycle only, never on hot path). Subsystem locks (`_combinedOverridesLock`, `_capsLockStateLock`, `_holdBreathLock`, `_transientTapLock`, `_heldLauncherKeysLock`) are never nested into each other, with ONE new documented exception introduced by P6 (Phase 3): `_holdBreathLock → _transientTapLock` (one direction only; grep-verify no reverse nesting on every phase).
- **I6 — Elapsed-time stale-fire guards** (`HOLD_FIRE_TOLERANCE_MS` idiom) stay on every reusable-timer callback.
- **Gate for every phase:** `dotnet build .\sWinShortcuts.csproj -c Release --no-incremental` → 0 errors; `dotnet test .\Tests\Tests.csproj` → all green. No commits unless the user asks.

---

## Phase 1 — Zero-behavior micros & cleanup (P1–P4)

### P1 (F5c) Delete dead code
- Remove `ALT_MOUSE_HOLD_JITTER_MIN_MS` / `ALT_MOUSE_HOLD_JITTER_MAX_MS` (`InputHookService.cs:30-31`) — unused since Alt+Mouse hold went deterministic.
- Remove the commented-out SpinWait block (`:1312-1317`). Spinning would fight the game for CPU; the deliberate replacement is P7 (timer resolution), not spinning.

### P2 (F5a) Injection micro-allocations
- Add `private static readonly int InputStructSize = Marshal.SizeOf<NativeMethods.INPUT>();` and use it at all 4 `SendInput` call sites.
- `ForceCapsLockState` (`:958`, `:962`): replace the two `SendInput(1, …)` calls with ONE `SendInput(2, new INPUT[2]{down, up}, InputStructSize)` — atomic batch; the down/up pair can no longer interleave with other injected input. `dwExtraInfo = INPUT_IGNORE` on both elements (unchanged).
- Keep `new INPUT[1]` in `SendKey` (human-frequency; not worth an unsafe span P/Invoke).

### P3 (F5e) `HandleWindowsLauncher` check order
- Move `Launchers.TryGetValue` + `binding.Path` null-check **before** the two `GetAsyncKeyState` Win-key syscalls (`:1210-1218`). Identical truth table (pure conjunction), saves 2 syscalls on every unhandled keydown while a launcher profile is enabled. Key-up latch handling stays above, untouched.

### P4 (F5d) `IsExtendedKey` completeness
- Add `Key.Apps` (`:1399-1406`) — `KeyCatalog.cs:66` offers it as a mappable target and VK_APPS (0x5D) is E0-extended; without the flag, scan-code-reading games see a wrong physical key.
- Audit note: catalog's other extended keys (Insert/Delete/Home/End/PgUp/PgDn/arrows/RightAlt/RightCtrl/Divide) are already listed; LWin/RWin/media keys are not in the catalog — no further additions.

**Phase 1 validation:** build+tests; manual: CapsLock Hold/Remap toggle still works; a `Win+<key>` launcher still fires once per press with no Start-menu pop; map a key to `Apps` and confirm the context-menu key registers in a test app.

---

## Phase 2 — Hook hot path (P5, F1)

### P5 Mouse-move early-out + unboxed hook reads
- `sWinShortcuts.csproj`: add `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`.
- **MouseCallback:** after the `nCode/_isRunning` guard, switch on `message` FIRST and `return CallNextHookEx(...)` for anything that is not one of the 8 button messages (`WM_L/R/M/XBUTTONDOWN/UP`). Moves (up to 8 kHz on gaming mice) and wheel exit before `lParam` is ever touched — today each one pays `Marshal.PtrToStructure<T>`, which **boxes on .NET 8** (verified against runtime source).
- Replace `Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam)` / `<KBDLLHOOKSTRUCT>` with unsafe by-value reads: `var data = *(NativeMethods.MSLLHOOKSTRUCT*)lParam;` (both structs are blittable: uint/enum-uint/IntPtr/POINT). The copy is taken before `CallNextHookEx`, so lifetime is safe. Unsafe code confined to these two lines.
- **Do not** move the injected-event filter: it stays immediately after the (new) message filter for mouse, and first-after-marshal for keyboard (I1).

**Phase 2 validation:** build+tests; manual: wiggle mouse at high polling rate with a profile active — Alt+tap/hold, right-click-only combined mappings, hold-breath all still fire; verify no `Marshal` usage remains in either callback body.

---

## Phase 3 — Tap injection determinism (P6, F2 + codex double-dispatch)

### P6 Synchronous tap DOWN; only sleep+UP deferred
Current: `FireTapKey` (`:1276`) and the hold-breath Toggle tap (`:1105`) queue DOWN+`Thread.Sleep`+UP entirely to the pool → DOWN inherits pool dispatch latency (load-correlated; the workers themselves park pool threads in `Sleep` 20–53 ms, so fast tap bursts self-inflict pressure) and loses ordering vs. the user's next physical input. Timer-fired AltMouse holds pay a **double** dispatch (timer callback = pool thread → queues another pool item).

Change:
1. Parameterize: `private void FireTapKey(Key key, int minDurationMs, int maxDurationMs)`. Alt+Mouse call sites pass `KEY_PRESS_DURATION_MIN/MAX_MS` (31–53); hold-breath Toggle passes its current 20–31. **Duration distributions unchanged.**
2. Inside `FireTapKey`: capture `var epochAtCall = _tapReleaseEpoch;` then synchronously:
   ```csharp
   lock (_transientTapLock)
   {
       if (_disposed || !_isRunning || epochAtCall != _tapReleaseEpoch) return;
       _transientTapKeys.Add(key);
       SendKey(key, true);              // DOWN: deterministic, ordered ahead of the user's next physical input
   }
   ThreadPool.QueueUserWorkItem(_ =>    // worker now owns ONLY duration + UP
   {
       var rng = _random.Value!;
       /* RNG warmup loop — unchanged, stays on the worker */
       var duration = rng.Next(minDurationMs, maxDurationMs + 1);
       Thread.Sleep(duration);
       lock (_transientTapLock)
       {
           if (_transientTapKeys.Remove(key)) SendKey(key, false);   // drain-safe: Remove-guard prevents double-UP
       }
   });
   ```
3. Delete the duplicated inline Toggle-tap worker in `ActivateHoldBreathLocked` (`:1104-1148`); call `FireTapKey(key, 20, 31)` instead (hoist 20/31 to named constants `HOLD_BREATH_TAP_DURATION_MIN/MAX_MS`).
4. **New lock nesting** `_holdBreathLock → _transientTapLock` (Toggle path calls FireTapKey while holding `_holdBreathLock`). Verified safe: `ReleaseAllState` takes `_holdBreathLock` (via `ReleaseHoldBreathState`) and `_transientTapLock` *sequentially*, never nested; the tap worker takes only `_transientTapLock`; nothing takes them in reverse order. Add a comment at both lock declarations documenting the one-way order (I5).
5. Epoch semantics preserved exactly: capture at decision point, check under the lock before injecting. For hook-thread call sites the check is now near-vacuous (hook thread can't interleave with itself); for timer-fired holds it still closes the decision→injection gap against a concurrent `ReleaseAllState`. The worker no longer needs the epoch — the `Remove`-guard alone is sufficient for the UP.
6. `_transientTapKeys` is a `List` on purpose (two overlapping same-key taps = two pending UPs) — do not change to a set.

**What this buys the user:** taps land ~20 µs after the decision regardless of autosave/logger/color/GC load (today: occasionally ms–tens of ms); injected key guaranteed to reach the game before the user's next physical keystroke; the timer-hold double dispatch disappears.

**Phase 3 validation:** build+tests; manual: Alt+tap rapid spam (10+ fast clicks — all pulses complete, none stuck), Alt+hold past threshold, hold-breath Toggle-mode tap, profile switch mid-tap-burst (no stuck key — drain still owns pending UPs), Stop/exit mid-tap (no stuck key).

---

## Phase 4 — Timer resolution (P7, F3)

### P7 Request 1 ms resolution while hooks are live + Win11 opt-out
Today every `Thread.Sleep` (tap durations) and `System.Threading.Timer` due-time (hold threshold, hold-breath delay) quantizes to the system interrupt period (typically ~15.6 ms default). The app never requests better, and on Windows 11 a hidden/minimized-window process **cannot** raise resolution unless it opts out of power throttling — exactly this app's tray state while gaming.

- `NativeMethods`: add `winmm.dll!timeBeginPeriod(uint)` / `timeEndPeriod(uint)`; `kernel32!GetCurrentProcess`; `kernel32!SetProcessInformation` with `ProcessPowerThrottling = 4`, `PROCESS_POWER_THROTTLING_STATE { Version=1, ControlMask, StateMask }`, `PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4`.
- `InputHookService.Start()` (after hooks install): `SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, { Version=1, ControlMask=IGNORE_TIMER_RESOLUTION, StateMask=0 })` — control-bit set + state-bit clear = "always honor this process's timer-resolution requests even when occluded" (Win11; returns an error on Win10 — **ignore failures silently, log at debug**). Then `timeBeginPeriod(1)`; remember success in `_timerResolutionRaised`.
- **Pairing discipline (codex R1.1):** if `timeBeginPeriod(1)` succeeded and any LATER step of `Start()` throws (SessionSwitch subscribe, watchdog creation), call `timeEndPeriod(1)` and clear the flag before rethrowing. `Stop()` calls `timeEndPeriod(1)` exactly once iff `_timerResolutionRaised`, then clears the flag (winmm requires matched pairs; `Stop()` is already idempotent via `_isRunning`).
- Effect: hold-threshold firing error drops from ≤ ~15.6 ms to ≤ ~1 ms; hold-breath activation delay tightens the same way; tap durations become `[d, d+~1)`. Deliberate jitter (15–36 ms hold-breath, 31–53 ms durations) is untouched — this narrows *unintended* scheduler noise only. Power cost is negligible on a gaming desktop and per-process since Win10 2004.

**Phase 4 validation:** build+tests; manual with debug logging: hold-breath `delay>0` activation timestamps cluster near base+jitter (not +0–16 ms beyond); no exceptions on Win10-style failure path (test by feature-flagging the call if no Win10 machine — acceptable to rely on documented graceful `!= TRUE` return).

---

## Phase 5 — Hook-loss watchdog (P8, F4a)

### P8 Detect and re-install silently-removed hooks
Windows (Win7+) silently removes an LL hook whose callback exceeds `LowLevelHooksTimeout` (HKCU, ~300 ms class defaults, hard-capped 1000 ms since Win10 1709) — no notification; today all bindings die until app restart.

- **Liveness tracking:** `private long _lastKeyboardEventTick; private long _lastMouseEventTick;` — `Volatile.Write(ref …, Stopwatch.GetTimestamp())` as the FIRST statement of each callback (before all guards; any invocation proves the hook is alive). Watchdog reads with `Volatile.Read`. **Initialize BOTH ticks to `Stopwatch.GetTimestamp()` immediately after the initial hook install** so a freshly-started idle app can't look stale (codex R1.3).
- **Context capture (codex R1.2):** in `Start()`, capture `_hookDispatcher = Dispatcher.CurrentDispatcher` (WindowsBase, no `Application` dependency) and verify `SynchronizationContext.Current is DispatcherSynchronizationContext`. Hooks must be (re-)installed from a message-pumping thread — **never a pool thread**. If the check fails, hooks are already on a broken thread (pre-existing FABLE_Plan.md warning about the `ConfigureAwait(false)` startup chain): log ERROR and disable the watchdog's re-install action (detection may still log). Watchdog re-install marshals via `_hookDispatcher.InvokeAsync`.
- **Watchdog:** one `System.Threading.Timer`, period 10 s, created in `Start()`, disposed in `Stop()`:
  1. If `!_isRunning` → return.
  2. `GetLastInputInfo(ref lii)` → last *system-wide* input tick (32-bit `Environment.TickCount` domain — compute age with `unchecked((uint)Environment.TickCount - lii.dwTime)` to survive wrap).
  3. If system input is FRESH (age < 2 s) but a hook's last-seen tick is STALE (> 30 s), that hook is presumed removed → post re-install to `_hookContext`.
  4. Re-install (on `_hookDispatcher`, under `_profileLock`, re-check `_isRunning`) — **install-new-before-unhook-old with a fail-open swap window** (codex R1.4 + R2.1): both registrations invoke the same delegate and LL callbacks receive no registration identity, so do NOT rely on idempotency during overlap — `MouseCallback` runs **non-suppressing** side effects on every pass (`_rightButtonPressed` tracking + `ReleaseRightClickOverrides` at `:421-430`, hold-breath arm/up at `:435-445`), and a double-processed `WM_RBUTTONDOWN` would re-arm hold-breath with a fresh jitter sample (user-visible timing change). Sequence:
     a. Set the per-hook volatile flag (`_keyboardReplacementInProgress` / `_mouseReplacementInProgress`). Each callback checks its flag immediately after liveness stamping and returns `CallNextHookEx` with **zero side effects** while set — the window fails OPEN (raw passthrough) for at most a few events.
     b. `SetWindowsHookEx` with the SAME kept-alive delegate field. On failure: clear the flag immediately, **keep the existing handle**, log ERROR, do NOT stamp the liveness tick (watchdog retries next period). A false positive can never lose a live hook.
     c. On success: `UnhookWindowsHookEx(oldHandle)` ignoring its result (handle may already be dead), swap the stored handle, log WARNING.
     d. Run step 5 (`ReleaseAllState`) — this also cleans up any release that passed through unprocessed during the fail-open window — then stamp ticks (step 6), then clear the flag LAST.
  5. **Missed-release safety (codex R1.5):** a hook that died mid-press has missed physical UPs — its injected state may be orphaned (e.g., mouse hook died during a right-click hold → `_holdBreathInjectedKey` still down; keyboard died mid-combined-hold → override never released). Immediately after a SUCCESSFUL re-install, call `ReleaseAllState()` (still under `_profileLock`, same as profile switches do). Coarse but regression-minimal: it reuses the proven release path, bumps the tap epoch, and H2 re-pairs any source key the user is still physically holding on its next key-up.
  6. After a successful re-install + release, stamp that hook's last-seen tick to now — rate-limits a false-positive (e.g., user mousing but never typing) to one refresh per stale-threshold, not one per watchdog period.
  5. Per-hook independence matters: a stall can kill only the hook that had an event pending (keyboard dies, mouse survives) — hence per-hook ticks, per-hook re-install.
- **False-positive cost is a ~µs unhook/rehook gap** (one event may pass unfiltered); thresholds above make this rare (user mousing but not typing for 30 s triggers a harmless keyboard-hook refresh at most).
- **Injected input caveat:** our own `SendInput` still invokes our callbacks (filtered at the top), so injections also refresh liveness ticks — correct behavior.
- **Testability:** extract the decision as `internal static bool ShouldReinstallHook(long hookLastSeenTicks, long nowTicks, uint systemInputAgeMs, ...)` (pure) + unit tests (`InternalsVisibleTo` already in place). The Win32 plumbing itself stays untested (accepted, consistent with repo).
- Session-lock interaction: none — watchdog only re-installs when system input is fresh, which is false on the lock screen for our session's ticks; `OnSessionSwitch` behavior unchanged.

**Phase 5 validation:** build+tests (new pure-function tests); manual: run app, `Suspend-Process`-style stall is hard to fake safely — instead temporarily lower the stale threshold to 5 s in a debug run, freeze the dispatcher with a deliberate `Thread.Sleep(2000)` injected via debugger or temporary menu item, confirm WARNING + re-install + bindings still work. Remove the temporary trigger.

---

## Phase 6 — State re-derivation + optional micro (P9, P10)

### P9 (F5f) Re-derive physical modifier/button state after profile-switch releases
`ReleaseAllState` force-clears `_altPressed`/`_rightButtonPressed` (`:1515-1516`) even when the user physically holds them across a profile switch → Alt+Mouse / RightClickOnly / hold-breath gates inert until re-press.
- Keep `ReleaseAllState` clearing both flags (correct for `Stop()` and `OnSessionSwitch` — desktop is going away).
- In `ActivateProfile`/`DeactivateProfile` ONLY, after `ReleaseAllState()` (still inside `_profileLock`): re-derive `_altPressed` from `GetAsyncKeyState(VK_LMENU|VK_RMENU)`; re-derive `_rightButtonPressed` respecting button swap — `GetAsyncKeyState(VK_L/RBUTTON)` reports **physical** buttons while the LL hook's `WM_RBUTTONDOWN` reports **logical** ones, so query `GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_LBUTTON : VK_RBUTTON` (add `GetSystemMetrics` + `SM_SWAPBUTTON = 23` to NativeMethods).
- Note: re-deriving `_rightButtonPressed=true` does NOT re-arm hold-breath (arming happens only on a real `WM_RBUTTONDOWN`) — it only lets RightClickOnly combined mappings work immediately, which matches physical reality.

### P10 (F5b, OPTIONAL — implement last or skip) Persistent hold-timer delegate
Replace the per-press `HoldCallback` closure (`:552-586`, 2 allocs per Alt+down-with-hold) with one persistent delegate per `MouseButtonState` reading `HoldKeyAtArm`/`ThresholdAtArm`/`DownTickAtArm` fields written before `HoldTimer.Change` (same publication order as today; the elapsed-time guard already rejects stale reads — semantics identical). Human-frequency allocation; value is tidiness, not latency. **Skip if any doubt.**

**Phase 6 validation:** build+tests; manual: hold Alt through a foreground-driven profile switch → Alt+click works immediately on the new profile; hold RMB through a switch → RightClickOnly mapping works without re-click; lock screen mid-hold still releases everything.

---

## Deferred (documented, deliberately NOT in this plan)

- **F4b — Dedicated high-priority hook thread.** The structural end-state, but requires migrating remaining UI-thread-serialization dependencies first: `WindowsLauncher.Launchers` is a live `Dictionary` mutated in place (`WindowsLauncherSettings.cs:10`, `WindowsLauncherEntryViewModel.cs:34-49`), and scalar live settings reads for AltMouse (`:458`, `:541`), CombinedMappings (`:694`), CapsLock (`:865-889`), hold-breath (`:973-979`). Prerequisite work: copy-on-write for `Launchers` + settings snapshot pattern. Do after P1–P9 have soaked.
- **KEYEVENTF_SCANCODE injection mode.** Some DirectInput/scan-reading games only accept scancode-flagged input; but with the flag, VK derives from scan via the active layout (behavior change). Revisit only if a real game ignores injected keys; would ship as a per-profile toggle, default off.
- **ISendInput seam + state-machine unit tests** (from memory.md next-steps): right refactor for test coverage, orthogonal to this plan; do not entangle.

## Recommended execution order & best recommendation

**Implement P1→P9 in phase order** (P10 optional). Rationale: Phases 1–2 are zero-behavior-change and de-risk the file for the behavioral phases; Phase 3 is the highest user-value input change (deterministic taps, ordering guarantee); Phase 4 tightens every timer the user can configure; Phase 5 removes the only silent-total-failure mode; Phase 6 fixes the last known behavioral nits. Ranked by what a user would ever consciously notice: **P8 > P6 > P5/P7 > rest**. Each phase is independently shippable and bisectable; stop-the-line rule: any phase that fails its gate gets fixed or reverted before the next begins. No commits without explicit user request.
