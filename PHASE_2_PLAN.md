# sWinShortcuts — Phase 2 Plan

**Forward roadmap for deferred work, accepted residuals, and a confirmed test gap.**

Companion to `IMPLEMENTATION_PLAN.md` (Phase 1) and `CODEBASE_REVIEW_REPORT.md` (source findings).

---

## 1. Status going in

Phase 1 is complete and verified:

- **15 / 16 Tier-1 findings implemented and codex-verified** (F-001 accepted as a documented residual).
- Final full-tree codex review verdict: **`Sound`** — no remaining must-fix defects; accepted residuals bounded.
- **126 / 126 tests** green.

Phase 2 is everything deliberately left out of Tier 1: the accepted residuals, the Tier 2–4 findings, the framework bump, and one newly-confirmed automated-test gap (Alt+Mouse tap/hold).

## 2. Governing mandate (unchanged)

> Gaming utility: **reliable timers and execution, no regression, no stuck keys / bindings.**

Every input-path change in Phase 2 must go through the same loop that governed Phase 1:
**implement → codex verify → fix regressions → re-verify until `Sound`.** The low-level hooks run
**on the WPF dispatcher/UI thread**, so any change touching the hook path is guilty until proven safe.

## 3. The keystone: F-002 ordered injector

Most of the highest-value Phase-2 items depend on one piece of infrastructure, so build it first.

**F-002 — route the remaining synchronous `SendInput` sites through the existing FIFO injector.**
Today, hold-breath and Auto-Run already ride a dedicated ordered injector thread
(`_holdBreathInjectionQueue`). Several other sites (caps remap, combined-target overrides, tap keys)
still call `SendKey`/`SendInput` **inline on the hook thread**, which is what forces the current
release-ordering compromises. Giving every injected event a single ordered lane:

- **unlocks F-011's full fix** — the combined-target DOWN/UP ordering race (currently a documented
  residual) disappears when transitions are emitted in one serialized order instead of racing the
  pool-release path against the hook key-down path;
- **removes F-013** (the remaining inline-inject sites are the finding);
- **hardens F-001** — an ordered lane is a prerequisite for the non-dropping input worker.

> ⚠️ Sequencing constraint: migrating a site to the injector must **preserve DOWN→UP pairing order**.
> A naïve migration that reorders a release relative to its press re-introduces a stuck key. This is
> the single riskiest change in Phase 2 and must be codex-verified per-site.

---

## 4. Priority tiers

| Tier | Item | Value | Depends on |
|------|------|-------|-----------|
| **2** | **F-002** ordered injector | Keystone; unlocks F-011/F-013, hardens F-001 | — |
| **2** | **F-020** hook behavior test seam (`ISendInput`) | Makes the whole input path testable | — |
| **2** | **F-011** combined-target race — full fix | No premature release under concurrent switch | F-002 |
| **2** | **F-001** input/color two-worker decoupling | Kills the ~200 ms cross-app mapping leak | F-002 |
| **2** | **F-014** shutdown recovery journal | Last-edit durability at exit | — |
| **3** | **F-016** INI off-dispatcher | Removes dispatcher I/O on slow/roaming AppData | — |
| **3** | **F-009** color/gamma baseline restore | Reliable revert of display color on exit | — |
| **4** | **F-023** WinEvent unhook thread affinity | Correctness of hook teardown thread | — |
| **4** | **F-024** INI schema / unknown-field warnings | Diagnostics on malformed config | — |
| **4** | Dead-code cleanup | Maintainability | — |
| **FW** | **F-019** .NET 10 + xUnit v3 | **Framework support — hard deadline** | — |
| **Sec** | **F-004 / F-005** security / LPE hardening | Deferred by decision (1-user app) | — |

---

## 5. Item detail

### F-002 — ordered injector (Tier 2, keystone)
- **Why:** remaining inline `SendInput` on the hook thread forces release-ordering compromises.
- **Approach:** extend the existing FIFO injector to accept all injected transitions; migrate caps
  remap, combined-target overrides, and tap keys one site at a time, each preserving DOWN→UP order.
- **Risk:** HIGH (reordering a release = stuck key). Migrate + verify per-site; keep the bounded-stall
  invariant (injected events filtered at the callback top).
- **Acceptance:** no inline `SendInput` on the hook thread; all existing input tests green; codex `Sound`.

### F-020 — hook behavior test seam (Tier 2)
- **Why:** the input state machine (caps tap/hold, combined-target, **Alt+Mouse tap/hold**) has **no
  automated coverage** because injection goes straight to `SendInput`. Every input fix so far has
  been verified by reasoning + codex, not by a repeatable test.
- **Approach:** introduce an `ISendInput` (or `IInputSink`) seam the hook writes through; in tests,
  a recording fake captures the exact synthetic DOWN/UP stream. Drive the hook with scripted event
  traces and assert the emitted sequence.
- **First tests to write** (regressions for bugs already fixed, so they can never silently return):
  - **Alt+Mouse tap vs hold** — a quick tap emits **only** `TapKey`; a hold past the threshold emits
    **only** `HoldKey`; back-to-back presses never let a stale hold-timer elapse fire during the next
    tap. *(This is the "previous build triggered both keys" bug — fixed in `ee59dc0` by the H3
    stale-guard; currently correct but untested.)*
  - **CapsLock** — suppression latched once per physical press; UP pairs with DOWN across a mid-hold
    mode/enable change; a Caps held across `Stop→Start` passes through (no stuck CapsLock).
  - **Combined-target** — DOWN on 0→1, UP on 1→0; ordered emission across a profile switch.
- **Acceptance:** the three trace suites above pass; seam adds no hot-path allocation.

### F-011 — combined-target race, full fix (Tier 2)
- **Residual today:** a rare premature release when two profiles map the same target and a switch
  happens while the target is held (sends occur outside `_combinedOverridesLock`). Not a stuck key —
  a *premature* release; widened to ~300 ms only during a foreign-hook stall.
- **Fix:** emit transitions through the F-002 ordered lane under a single serialized order.
- **Acceptance:** trace test (from F-020) proving no premature release across concurrent switch.

### F-001 — input/color two-worker decoupling (Tier 2)
- **Residual today (accepted "stay put"):** ~200 ms cross-app leak of the old profile's mappings when
  switching **to/from a color-changing profile**, plus a rare A→B→A drop mid-color-apply. Worst
  effects already mitigated (eager Auto-Run release, color dedup, F-003 no-freeze).
- **Fix:** split activation into a **non-dropping FIFO input worker** + a **coalescing color worker**,
  with generation-gating and the F-010 ownership guard. Input activation must never be dropped by the
  capacity-1 `DropOldest` channel the way a stale color apply can be.
- **Risk:** HIGHEST — this rewrites the profile-switch spine every binding depends on. Do **after**
  F-002 + F-020 exist so it can be trace-tested. This was deliberately deferred in Phase 1 for exactly
  this reason.
- **Acceptance:** switching to/from a color profile shows no old-mapping leak; A→B→A never drops.

### F-014 — shutdown recovery journal (Tier 2)
- **Residual today:** an edit is lost **only if** its own profile file is *permanently* locked at the
  exact instant of exit (the common data-loss window was closed in Phase 1 via coalesced flush).
- **Fix:** write pending edits to a small journal before exit; replay on next start if a normal save
  didn't complete.
- **Acceptance:** kill-during-locked-save test recovers the edit on next launch.

### F-016 — INI off-dispatcher (Tier 3)
- **Residual today:** settings INI read/write runs on the dispatcher (~1 ms locally; only matters on
  slow/roaming AppData). schtasks is already off-dispatcher.
- **Fix:** move the settings INI read/write off the dispatcher like the startup task work.

### F-009 — color / gamma baseline restore (Tier 3)
- **Residual today:** GDI gamma ramps are not reliably reversible; documented limitation.
- **Direction:** codex previously advised dropping the unreliable `GetDeviceGammaRamp` baseline;
  prefer a known-good default restore (identity ramp / NVAPI reset) over trying to capture+replay.

### F-023 / F-024 / dead-code (Tier 4)
- **F-023:** ensure the WinEvent hook is unhooked on the same thread that installed it.
- **F-024:** warn on unknown/malformed INI fields instead of silently ignoring.
- **Dead-code:** remove now-unreachable paths surfaced during Phase 1.

### F-019 — .NET 10 + xUnit v3 (Framework — **hard deadline**)
- **Why:** **.NET 8 reaches end-of-support on 2026-11-10.** Must land before then.
- **Scope:** retarget to .NET 10 (LTS), bump xUnit to v3, re-run the full suite, re-verify the hook
  path on the new runtime. Serena's C# LSP also needs ≥ .NET 10 to work on this repo (unblocks
  symbol-level tooling that was unavailable in Phase 1).

### F-004 / F-005 — security / LPE hardening (Deferred by decision)
- **Status:** explicitly deferred — single-user desktop app, no multi-user/elevation threat model.
- Revisit only if the app ever ships to multi-user or elevated contexts. Items: "Start as admin"
  self-setup, elevated-app privilege handling, task/registry ACLs.

---

## 6. Recommended sequencing

1. **F-020 test seam** and **F-002 ordered injector** first — the seam makes every later input change
   verifiable; the injector unblocks the rest. (Independent of each other; can proceed in parallel.)
2. **F-011 full fix** on top of F-002 (small, high-confidence once the lane exists).
3. **F-014 recovery journal** any time (independent, persistence-only).
4. **F-001 two-worker decoupling** — only after F-002 + F-020; highest risk, now trace-testable.
5. **F-016 / F-009** (Tier 3) as capacity allows.
6. **F-019 .NET 10 bump** scheduled to complete **before 2026-11-10**; it touches everything, so land
   it at a stable point (ideally after the input work settles) but not so late it risks the deadline.
7. **Tier 4** cleanup opportunistically.

## 7. Deadlines

| Date | Item |
|------|------|
| **2026-11-10** | .NET 8 end-of-support — **F-019 must be done** |

## 8. Notes carried from Phase 1

- The hook callback runs **on the WPF dispatcher thread**; a `SendInput` inside a subsystem lock the
  hook also takes is bounded (~`LowLevelHooksTimeout` ≈ 300 ms) because injected events are filtered
  at the callback top. Preserve this invariant in every F-002 migration.
- Held-key latches (caps, and future ones) must be **seeded from physical key state at Start**, never
  blindly cleared — and never reset in the shared release path (profile switch / watchdog reinstall
  keep the hook running with keys possibly held). See `memory.md` "LESSON" entries.
- Accepted-residual rationale and per-fix reasoning live in `memory.md`.
