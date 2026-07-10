# INJECTOR_MIGRATION_Plan.md — Extending the Hold-Breath Injector Pattern

**Source:** 2026-07-07/08 right-click stutter investigation (root cause proven with instrumentation, fixed, codex-verified CLEAN, live 4-hour play session confirms zero pointer stalls). See `memory.md` entries dated 2026-07-07 for the full forensic trail.
**Baseline:** `main` @ `4ac0c53` (pushed). Build 0 errors, 99/99 tests.
**App intent:** gaming — reliable timers/execution, zero regression, no stuck keys, bindings never silently die.
**Scope:** `Services\InputHookService.cs` only, unless a phase says otherwise.

---

## 0. The mechanism (why this pattern exists at all)

`WH_KEYBOARD_LL`/`WH_MOUSE_LL` hooks run on this app's WPF dispatcher thread, and Windows' raw-input thread (RIT) waits synchronously on every hook callback in the system before delivering input anywhere. `SendInput` is not fire-and-forget: the injected event dispatches **synchronously** through every process's low-level hook chain, including foreign hooks (overlays, anti-cheat, RGB software, or — as proven here — the game's own hook while it opens a UI element like an item context menu). If any one of those foreign hooks is slow to return, `SendInput` blocks for up to `LowLevelHooksTimeout` (~300ms) waiting on it.

The bug this app shipped for years, now fixed once: hold-breath's `SendInput` ran **synchronously on the timer thread, holding `_holdBreathLock`**. When a foreign hook stalled, that lock stayed held for ~300ms, and the mouse-hook's `WM_RBUTTONUP` handler — which needed the same lock to process your button release — blocked on it *inside the hook callback*. That stalled the RIT, which froze **all system pointer input**, not just this app's.

The fix: a dedicated single-consumer FIFO worker thread (`_holdBreathInjectionQueue`, a `BlockingCollection<HoldBreathInjection>`) now owns every hold-breath `SendInput` call. Decisions (record state, enqueue) stay under `_holdBreathLock` and complete in microseconds; the actual injection — and any 300ms foreign-hook stall — happens off the hook thread entirely, where it can't block anyone. FIFO single-consumer ordering is what guarantees UP can never overtake DOWN (replacing the lock's role in that guarantee). `queue.IsAddingCompleted` doubles as a per-session drain discriminator so a slow `Stop()` shutdown drain can't leak a queued press into the next session.

**The finding from the live validation session (2026-07-07 19:01–23:07):** this isn't just a rare-event fix. `sendMs` (the injector's own SendInput timing) averaged **10.5ms** and clustered **5–17ms** across 193 sends — meaning routine SendInput calls routinely cost double-digit milliseconds going through this game's hook chain, not just the rare 300ms pathological stall. Every synchronous, lock-held SendInput elsewhere in this file pays that same routine cost today, on the hook thread, and none of it has been measured yet.

**Every other injection site in this file is a smaller-probability instance of the exact same bug shape.** This plan ranks them by exposure (frequency × whether the send happens under a lock the hook path also needs) and migrates them onto the same proven idiom.

### Invariants every phase MUST preserve (carried over from the original hold-breath fix and `INPUT_SLEEP_Plan.md`)
- **I1 — Injected-filter-first:** both hook callbacks early-out on `LLKHF_INJECTED`/`LLMHF_INJECTED`/`INPUT_IGNORE` before taking any lock. Never reorder.
- **I2 — Every injected DOWN is recorded before or atomically with injection**, so a release path can always force the UP. Migrating a send off-thread must not weaken this — record-then-enqueue, never enqueue-then-record.
- **I3 — Release paths are unconditional** (no `IsEnabled`/profile gates on UP paths).
- **I4 — `_isRunning` flips false before `ReleaseAllState` in `Stop()`;** injection sites re-check it under their subsystem lock before enqueuing.
- **I5 — Lock ordering** stays one-way; no new nesting introduced by a migration. Grep-verify after each phase.
- **New I7 (this plan) — A queued DOWN must be skippable at shutdown, and its paired UP must be a safe no-op if the DOWN was skipped.** This is the `IsAddingCompleted` discriminator proven in the hold-breath fix; any new queue consumer must replicate it, not re-derive it.
- **Gate for every phase:** `dotnet build .\sWinShortcuts.csproj -c Release --no-incremental` → 0 errors; `dotnet test .\Tests\Tests.csproj` → all green (currently 99/99). Codex read-only review (gpt-5.5, high effort) before considering a phase done. No commits unless the user asks.

---

## Phase 1 — Evidence gathering (do this first, cheap, non-invasive)

Before committing engineering time to migrate sites that may never actually stall, get the same kind of proof the hold-breath fix had. Add a `Stopwatch`-timed log line at each remaining synchronous `SendInput` call site (`SendKey`, `ForceCapsLockState`'s batched pair) — same shape as the injector's existing `sendMs=` log — for one play session, no logic changes.

- `SendKey` (`InputHookService.cs:2066`): wrap the existing `NativeMethods.SendInput` call with `Stopwatch.GetTimestamp()` before/after, log `sendMs=` alongside the existing FAILED-path logging (success path currently logs nothing — add a debug-gated line).
- `ForceCapsLockState` (`:1578`): same around its `SendInput(2, ...)` call.
- Leave all call sites and locking exactly as-is — this phase only measures.

**Exit criterion:** a play session's `debug.log` answers, per site: does `sendMs` ever approach double digits or spike toward 300ms while a lock the hook path needs is held? If CapsLock Remap and AltMouse tap sends never exceed ~1ms in practice (foreign-hook stalls may be specific to this one game's context-menu code path), Phase 2/3 can be deprioritized to "when it's actually reported," rather than migrated preemptively. If they do spike, the ranking below is confirmed and Phase 2/3 proceed unconditionally.

**Phase 1 validation:** build+tests only (log lines are debug-gated, zero behavior change); one real play session with the profile(s) the user actually uses (CapsLock Remap + AltMouse both active).

---

## Phase 2 — CapsLock Remap + AltMouse FireTapKey (highest exposure × frequency)

These two are grouped because they're the highest-frequency injection paths in the file (CapsLock Remap fires on every tap of a remapped CapsLock; AltMouse fires on every mouse-button tap/hold in profiles that use it) and both currently do a **synchronous, lock-held** `SendInput` directly on either the hook thread or a timer thread.

### P2a — CapsLock Remap (`HandleCapsLock`, `:1426–1510`; `ReleaseCapsState`, `:1539`)
Today: `SendKey(target.Value, true/false)` at `:1494`/`:1500`/`:1484` runs **synchronously inside the keyboard hook callback, under `_capsLockStateLock`** — no lock-collision even needs to occur; a foreign-hook stall here is an **unconditional** full input freeze on every single CapsLock remap event, keyboard *and* mouse. This is the app's single highest-risk site: it is the user's actual hottest path (constant CapsLock→M activity confirmed in tonight's log) and has zero of the mitigations hold-breath now has.

Migration:
- Extend the existing `HoldBreathInjection` queue+worker to a **shared** generic injector (`record struct KeyInjection(Key Key, bool IsDown, int PreSleepMs)` — same shape, hold-breath's `PreSleepMs` already generalizes), OR stand up a second dedicated queue/thread scoped to CapsLock (simpler blast radius, more threads). **Recommendation: share one worker.** The FIFO ordering only needs to be per-key-per-source; a single consumer draining a merged queue is strictly simpler than reasoning about two workers racing on the same physical keyboard state, and it turns "no interleaving between injection sites" into a structural property of the whole file, not a per-site accident.
- `HandleCapsLock`'s Remap branch keeps its `_capsLockStateLock`-guarded record step (`_capsRemappedKey = target`) exactly where it is (I2) — only the trailing `SendKey(...)` call becomes `Enqueue(...)`.
- `ReleaseCapsState` (`:1539`, called from `ReleaseAllState`) already runs off the hook thread (dispatcher/Stop path) — its `SendKey(_capsRemappedKey.Value, false)` at `:1564` migrates to the same enqueue for consistency, but is not the urgent half of this fix.
- **TOCTOU note carried from prior analysis:** CapsLock *Hold* mode's `ForceCapsLockState` does a read-check-act (`IsCapsLockOn()` → conditionally `SendInput`) — if ever migrated, that whole sequence must move to the worker as one atomic unit, not just the tail `SendInput`. Hold mode is lower-frequency than Remap in this user's profiles; sequence it after Remap lands clean, not bundled with it.

### P2b — AltMouse `FireTapKey` (`:2016–2063`; callers `:1232`, `:1278`, `:1284`)
Today: DOWN (`:2032`) is synchronous under `_transientTapLock`, called from either the mouse-hook thread directly (`HandleMouseUp`, quick tap / hold-threshold-met paths) or a `Timer` callback thread (hold-timer fired path, `:1232`) — both are exactly the shape that caused the original bug (lock-held synchronous SendInput reachable from a hook-adjacent thread). UP (`:2057`) already runs off a `ThreadPool` work item after `Thread.Sleep(duration)`, still under `_transientTapLock`.
- This is architecturally the closest match to hold-breath's pre-fix shape, and the fix is structurally identical: `PreSleepMs` in the shared injector already models "DOWN, human-delay, UP" — `FireTapKey`'s duration/jitter logic maps directly onto it.
- **Deliberate trade to document, not silently accept:** the code comment at `:2011-2014` (P6 from `INPUT_SLEEP_Plan.md`) states DOWN-synchronous-on-caller is intentional — it guarantees the injected key lands *before* the user's next physical input, ordered deterministically. Moving DOWN onto a queued worker changes that guarantee from "hard, before next physical input" to "soft, microseconds later in FIFO order." For a gaming input tool this is very likely still the right trade (a hard guarantee that can freeze the whole system for 300ms is worse than a soft one that's off by microseconds), but say so explicitly when this lands — it's a real semantic change, not a pure refactor.
- Migrate the DOWN call at `:2032` and the UP call at `:2057` (drop the now-redundant `ThreadPool.QueueUserWorkItem`/`Thread.Sleep` wrapper — the shared worker's `PreSleepMs` replaces it, same as it already replaced hold-breath's Toggle-mode FireTapKey call). `_transientTapKeys` add/remove bookkeeping (I2/I3) stays exactly where it is, under `_transientTapLock`, unchanged — only the `SendKey` calls move.
- Callers (`:1232`, `:1278`, `:1284`) are unaffected — they still call `FireTapKey`, which now enqueues instead of sending directly.

**Phase 2 validation:** build+tests; manual — CapsLock Remap tap-hold-repeat under load (verify no stuck M key, no retarget leak per the existing `:1482` comment), AltMouse tap and hold-threshold paths in a profile that uses both mouse buttons, combined with hold-breath active in the same profile to confirm the shared worker doesn't reorder across *different* keys' DOWN/UP pairs (FIFO is global-queue order, not per-key — verify no cross-key head-of-line stall: a slow foreign-hook stall on key A's send must not delay key B's already-queued UP past a user-perceptible threshold; if this shows up, revisit single-shared-queue vs. per-key queues in P2's design note above).

---

## Phase 3 — Combined overrides + CapsLock atomic pair (lower urgency, closes remaining gaps)

### P3a — Combined mapping sends (`HandleCombinedMappings`, `:1334`/`:1409`/`:1412`; release paths `:2186`/`:2214`)
Today: these run on the hook thread but **outside any lock held by another path** (`_combinedOverridesLock` only guards the dictionary mutation, not the `SendKey` call itself) — lower risk than P2, but a foreign-hook stall here still blocks the keyboard hook callback itself for up to 300ms, which is still a system-wide freeze even without a lock-convoy. Migrate the three `SendKey` calls (`:1334`, `:1409`, `:1412`) to the shared enqueue; `ReleaseAllOverrides`/`ForceReleaseRightClickOverrides` (`:2186`, `:2214`) already run off the hook thread (dispatcher/Stop path) — enqueue there too for consistency, and because `CompleteAdding`-style drain-on-shutdown semantics (I7) need every producer going through the same queue to be trustworthy.

### P3b — `ForceCapsLockState`'s atomic `SendInput(2, ...)` pair (`:1578–1611`)
This one **cannot** migrate as two separate single-key enqueue calls — the whole point of the current code (batched `SendInput(2, new[]{down, up}, ...)`, per the `INPUT_SLEEP_Plan.md` P2 comment at `:1586-1588`) is that the down/up pair is atomic and can't be interleaved with other injected input mid-pair. Migrating this requires extending the queue's item type to carry an optional *paired* injection (`record struct KeyInjection(Key Key, bool IsDown, int PreSleepMs, INPUT[]? AtomicPairOverride = null)` or a distinct queue item variant) so the worker still issues one `SendInput(2, ...)` call for this site specifically. Do this last, and only if Phase 1's evidence shows CapsLock Hold-mode toggles are frequent enough in the user's actual profiles to matter (Remap is confirmed hot; Hold mode's frequency is unconfirmed).

**Phase 3 validation:** build+tests; manual — combined-override key held/released rapidly (verify H2 release-at-top-of-method guarantee still fires even mid-drain), CapsLock Hold mode toggle repeatedly, full `ReleaseAllState` on Stop() with all subsystems (hold-breath + CapsLock + combined + AltMouse) simultaneously mid-flight to confirm the shared queue's `IsAddingCompleted` drain guard (I7) covers every producer, not just hold-breath's original one.

---

## Out of scope / explicitly not migrating
- `SendDummyKeyEvent` (`:2110`) — fires the unassigned VK 0xFF tap used only to suppress the Start menu after a consumed Win-chord; not reachable from a hook-thread lock, extremely low frequency (once per consumed Win+key), and its ordering relative to the *shell's* own Start-menu-suppression logic likely wants to stay synchronous. Leave as-is unless evidence says otherwise.
- `FileLoggerService` — already uses this exact queue+dedicated-writer-thread pattern (`BlockingCollection<string>` + background writer, `Services/FileLoggerService.cs:16-29`). No action needed; cited here only as confirmation the codebase already independently converged on this idiom for exactly this class of problem (blocking I/O called from a hot/hook-adjacent path).

## Sequencing recommendation
Do Phase 1 first regardless of how confident the ranking above feels — it's near-zero cost and turns "probably CapsLock Remap is worst" into a measured fact before spending engineering time. Phase 2a (CapsLock Remap) alone captures most of the remaining risk given this user's actual usage pattern; Phase 2b and Phase 3 can follow opportunistically or be deferred until Phase 1's logs (or a future user report) justify them.
