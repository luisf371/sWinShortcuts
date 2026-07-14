# CODEBASE_REVIEW_REPORT Remediation Plan

**Target:** all 25 findings in `CODEBASE_REVIEW_REPORT.md` (0 Critical / 8 High / 14 Medium / 3 Low).
**Baseline:** `main` @ `aa1155b`, Release build 0 errors / 163 CA1416 warnings, tests 105/105.
**Hard mandate (gaming app):** no stuck keys, reliable timers/execution, no regression to bindings or profile switching.

## Foundational architecture facts (drive every fix)
- **LL keyboard/mouse hooks run ON the WPF dispatcher/UI thread** (`App.OnStartup` await chain never truly yields — `IniProfileStore` load is synchronous). Hook callbacks are therefore serialized with UI-thread work. Any *synchronous* `SendInput` reachable from a hook or UI-thread path can stall past `LowLevelHooksTimeout` (~300 ms) → Windows silently drops the hook → system-wide input freeze.
- **A proven FIFO injector already exists** (`_holdBreathInjectionQueue` + a dedicated background thread) used by hold-breath / Auto-Run. F-002/F-011/F-013 reuse this, not a new mechanism.
- **`ActivateProfile`/`DeactivateProfile`/`ReleaseAllState` run on the POOL worker** (`ProfileActivationService` channel). `_combinedOverridesLock`, `_capsLockStateLock`, `_holdBreathLock`, `_autoRunLock` guard hook-thread vs pool-thread state.
- **Established discipline:** SendInput-inside-a-subsystem-lock the hook thread also takes is bounded (≤ LL timeout) because our injected events are filtered at the top of both callbacks — that early-out is the invariant that makes locking around SendInput safe. Keep it first.

## Scoping decisions (explicit — nothing silently dropped)
| Finding | Doing now | Deferred / documented follow-up |
|---|---|---|
| F-004 | ACL + reparse guard on HIGHEST task; tri-state task query | Signed installer into Program Files |
| F-005 | Elevated process refuses user-writable `RunAsAdmin` launcher targets | Privileged-broker IPC architecture |
| F-009 | Best-effort `GetDeviceGammaRamp` + DVC baseline capture/restore; honest docs | Guaranteed exact capture across all HDR/ICC/modeset paths |
| F-019 | `global.json`, `packages.lock.json`, xUnit v3, CI | **.NET 8 → .NET 10 bump — isolated future change, must land before 2026-11-10 (EOS)** |
| F-024 | Field-level warning logging for malformed INI values | `SchemaVersion` + ordered migration framework (over-built for this app) |

## Global no-regression protocol
Every phase: (1) `dotnet build -c Release` 0 errors; (2) `dotnet test` all green (app must be closed — running exe locks `bin\Release`); (3) read the full diff; (4) `/codex` gate (read-only, high; **xhigh/sol for the hook + F-001 state machines**); (5) fix findings → re-codex until clean; (6) append a dated note to `memory.md`. No commit unless the user asks. The riskiest items (F-001, F-002, F-009) get a codex **design** review before implementation, not just after.

---

## Phase 0 — Foundation & safety nets (low risk; enables safe iteration on everything after)

### F-025 — Clean the CA1416 warning baseline
- **Approach:** declare Windows platform at assembly/project level (`<SupportedOSPlatform>` / assembly `[SupportedOSPlatform("windows")]`, explicit `TargetFramework`=`net8.0-windows10.0.x`), so the WPF-tmp + final compile stop emitting duplicate platform warnings. Address any residual warnings individually — do **not** globally `NoWarn`.
- **Files:** `sWinShortcuts.csproj`, `Properties/AssemblyInfo.cs`.
- **Risk:** Low. **Tests:** clean Release rebuild; warning count → 0 or a small reviewed baseline.
- **Guard:** must not change runtime TFM behavior; only annotate intent.

### F-021 — Hermetic persistence tests (storage-root seam)
- **Approach:** add an `internal` ctor to `IniProfileStore` taking a root directory (+ `InternalsVisibleTo` already present for Tests). Migrate `IniProfileStoreIntegrationTests` to a unique temp root per test with deterministic async cleanup that fails visibly. Keep one explicit opt-in real-`%APPDATA%` smoke category.
- **Files:** `Configuration/IniProfileStore.cs`, `Tests/IniProfileStoreIntegrationTests.cs`.
- **Risk:** Low. Also unblocks F-008 failure-injection tests. **Tests:** parallel isolated stores; cleanup after assertion failure; prove real app dir untouched.

### F-022 — Tests in solution + CI
- **Approach:** add `Tests.csproj` to `sWinShortcuts.sln`. Add `.github/workflows/ci.yml` (windows-latest: locked restore, Release build, `dotnet test`, `dotnet list package --vulnerable/--deprecated`). Pin actions by SHA, minimal permissions.
- **Files:** `sWinShortcuts.sln`, new `.github/workflows/ci.yml`.
- **Risk:** Low. **Tests:** deliberately fail one test → CI red; solution `dotnet test` discovers all cases.

### F-019a — Reproducible restore (keep .NET 8)
- **Approach:** add `global.json` (SDK pin + `rollForward: latestFeature`), enable `RestorePackagesWithLockFile` → commit `packages.lock.json`; CI uses `--locked-mode`. Do **not** bump TFM or migrate xUnit here.
- **Files:** new `global.json`, `packages.lock.json`, csproj lock property.
- **Risk:** Low. **Tests:** clean-machine locked restore.

### F-020a — `ISendInput` / injection seam
- **Approach:** extract the native `SendInput` boundary (and the `INPUT[]` build) behind an `IInputInjector` interface with a real impl and a fake. **Seam only** — behavior unchanged; the fake enables Phase 4 tests. No call-site logic moves yet.
- **Files:** `Interop/NativeMethods.cs` (or new `Services/IInputInjector.cs`), `Services/InputHookService.cs`, `Tests/Fakes/`.
- **Risk:** Low-Med (wide but mechanical). **Tests:** existing 105 stay green; fake records calls.

---

## Phase 1 — Data-integrity & lifecycle (low-med risk; no hot-path behavior change on success)

### F-014 — Autosave failure requeues (never lose an edit)
- **Approach:** remove a profile from `_dirty` only after confirmed success; on failure atomically re-add + bounded retry/backoff. `SaveProfileInternalAsync` returns a result (not void). Preserve edit-during-save generation so an older success can't clear newer changes.
- **Files:** `ViewModels/MainViewModel.cs`. **Risk:** S-M. **Tests:** fail-once, persistent-failure, edit-during-save, concurrent flush, shutdown-after-recovery.

### F-015 — Delete durably before mutating memory
- **Approach:** delete the INI first (or roll back list/snapshot on `File.Delete` throw); raise removal event / cancel pending autosave only after the delete transaction succeeds; surface an actionable error from the command.
- **Files:** `Services/ProfileManager.cs`, `Configuration/IniProfileStore.cs`, `ViewModels/MainViewModel.cs`. **Risk:** S. **Tests:** locked-file rollback, event suppression, restart consistency.

### F-016 — Transactional Settings dialog
- **Approach:** keep live preview (watchdog / Advanced Mode / debug logging are deliberately live) but **snapshot** service state on open and **roll back** on Cancel / title-bar close / any save-or-apply failure. `SaveIni()` returns/throws a result → dialog stays open with a clear error instead of reporting false success.
- **Files:** `ViewModels/SettingsViewModel.cs`, `Views/SettingsWindow.xaml(.cs)`, `MainWindow.xaml.cs`. **Risk:** S-M. **Tests:** Save / Cancel / close / INI-failure / startup-task-failure each leave service+persisted state matching.

### F-017 — Centralize executable validation
- **Approach:** move normalization + `.exe` policy + non-empty + duplicate-normalized detection into `ProfileManager.SaveProfileAsync` so **every** caller (inline edit + Modify dialog) is protected. Inline VM edit rolls back to prior valid value on rejection.
- **Files:** `Services/ProfileManager.cs`, `ViewModels/ProfileViewModel.cs`, `MainWindow.xaml`, `ViewModels/MainViewModel.cs`. **Risk:** S-M. **Tests:** empty/whitespace/non-.exe/path-qualified/case-only/dotted/duplicate through both paths.

### F-007 — Immutable `ProfileKind` identity
- **Approach:** add `ProfileKind { Custom, Windows, Color }` assigned by the factory/load path (not derived from `Name`). Route persistence destination + delete-protection + `DeduplicateProfileNames` by `Kind`, never display name. Migration: a custom file declaring a reserved `Name` stays `Custom`, gets a deterministic display suffix, and saves back to its own `SourcePath` (cannot write/delete `Win.ini`/`Color.ini`). Reuses the existing suffix/collision logic already in load.
- **Files:** `Models/Profile.cs`, `Services/ProfileFactory.cs`, `Configuration/IniProfileStore.cs`, `Services/ProfileManager.cs`. **Risk:** M. **Tests:** both reserved names as custom files → stay custom, suffixed, save to source, cannot clobber built-ins.

### F-008 — Non-fatal built-in load failure
- **Approach:** wrap `Win.ini` and `Color.ini` loads in the same per-file recovery custom profiles get: preserve the unreadable source, log file/section/reason, start that built-in from in-memory defaults, show one non-fatal warning. Guard Profiles-directory enumeration separately. Uses the F-021 root seam for tests.
- **Files:** `Configuration/IniProfileStore.cs`, `IniDocument.cs`, `MainWindow.xaml.cs`, `App.xaml.cs`. **Risk:** M. **Tests:** independent failures for Win.ini / Color.ini / enumeration / one custom file → startup continues, files untouched, diagnostic identifies source.

---

## Phase 2 — Color pipeline (med-high risk; hardware-adjacent). Order: safe → structural.

### F-003 — Bound NVAPI enumeration (do first — smallest, highest value-per-risk)
- **Approach:** break both display-handle loops on the **first** non-OK status; classify expected end/no-device vs actionable failure in logs; add a documented defensive max-enumeration cap. `EnsureFunctionsLoaded()` requires **every** delegate the enabled capability uses before reporting success (fail closed).
- **Files:** `Services/NvidiaColorControlService.cs`. **Risk:** Low. **Tests:** each terminal status at index 0 and after one valid handle; each missing delegate → bounded completion, one diagnostic, input switching still works.

### F-018 — Color mutation under the snapshot lock
- **Approach:** `SnapshotProfiles()` already deep-copies under `_sync`; close the **write** side. Stop handing out a shared mutable `DisplayColorProfile` from `GetOrCreateProfile` for external mutation — provide `ColorSettings.UpdateProfile(id, Action<DisplayColorProfile>)` (or immutable value + `SetProfile` copy-on-write) so VM slider writes mutate under `_sync`. Reader then always sees whole-before/whole-after tuples.
- **Files:** `Models/ColorSettings.cs`, `ViewModels/DisplayColorSettingsViewModel.cs`, `Services/ProfileActivationService.cs`, `Configuration/IniProfileStore.cs`. **Risk:** M. **Tests:** barrier-controlled snapshot vs multi-field update → no mixed tuples.

### F-006 — Hot-plug must not touch disabled displays
- **Approach:** split `NotifyMasterEnabledChanged()` into UI-state notification (raise `AreControlsEnabled` only) vs explicit hardware apply/revert. Display-list **rebuilds** raise UI state only; only an explicit user enable/disable toggle applies/reverts. Route topology re-apply through `ProfileActivationService` whose plan-diff already knows whether the app previously owned that display.
- **Files:** `Models/ColorSettings.cs`, `ViewModels/ColorSettingsViewModel.cs`, `ViewModels/DisplayColorSettingsViewModel.cs`, `Services/ProfileActivationService.cs`. **Risk:** S-M. **Tests:** hot-plug w/ new/disabled display → zero hardware calls; explicit enabled→disabled → one restore; enabled reapplied only by activation pipeline.

### F-009 — Best-effort baseline capture/restore
- **Approach:** before the **first** successful write per display, capture `GetDeviceGammaRamp` and the current NVAPI DVC level; restore that exact baseline on disable, `StopAsync`, and partial-failure rollback (instead of writing an identity ramp / DVC 0). Invalidate + re-capture on `DisplaySettingsChanged`/modeset. Where reliable capture is unavailable (HDR), **document** the limitation honestly and leave hardware untouched rather than guessing.
- **Files:** `Services/NvidiaColorControlService.cs`, `Services/ProfileActivationService.cs`, `Interop/NativeMethods.cs` (`GetDeviceGammaRamp`). **Risk:** M-H (hardware). **Tests:** capture/restore for enable→disable, switch, stop, modeset, partial-fail; RecordingColorControlService extended to model capture.

### F-010 — Contain late native completion at shutdown
- **Approach:** a stopping generation checked before **every** worker side effect (activate/deactivate/tray/color); retain + observe the worker `Task` to completion; make a late-returning native call side-effect-free; do not null/dispose worker-owned state until termination is confirmed.
- **Files:** `Services/ProfileActivationService.cs`. **Risk:** M. **Tests:** blocked apply + canceled stop, late completion, repeated stop, container disposal → no activation/tray/color after stopping begins.

### F-001 — Decouple input transition from color I/O (highest-risk; codex **design** review first)
- **Problem:** capacity-1 `DropOldest` channel + color enumeration/gamma work *before* input activation ⇒ old app's non-Auto-Run mappings (Alt+Mouse, combined, caps, hold-breath) stay live in the new foreground app during ~200 ms color work; an A→B→A burst can drop B entirely so the release boundary is never observed.
- **Design (for codex validation before coding):** two paths.
  1. **Fast input-identity path** — on the `ForegroundWatcher` event, eagerly publish `{hwnd,pid,exe,generation}` and perform old-profile **release + new-profile input activation** on a non-coalesced fast path. Input activation is cheap (no blocking native color I/O). *Open question for codex:* run this on the dispatcher/hook thread (natural for hook-state reads, but changes current pool-thread locking assumptions) vs a dedicated fast worker. Recommendation: keep it on the pool but **before** and independent of color; revisit thread only if locking demands it.
  2. **Slow color path** — color plan stays on the coalescing channel keyed to the latest published generation; color failure/latency can **never** block or drop an input transition.
  3. Defense in depth: hook feature paths validate the latest foreground generation/identity (as Auto-Run already does).
- **Files:** `Services/ProfileActivationService.cs`, `Services/ForegroundWatcher.cs`, `Services/InputHookService.cs`. **Risk:** High. **Tests:** blocking color fake, A→B and A→B→A → immediate `ReleaseAllState`, no A injection while B foreground, correct final profile, independent color coalescing.

---

## Phase 3 — Input hook correctness (med-high risk; the no-stuck-key core). Tests land with fixes.

### F-011 — Reference-count shared mapping targets
- **Approach:** maintain target-key refcounts under `_combinedOverridesLock`; emit target DOWN only on 0→1 and UP only on 1→0; centralize normal + forced (right-click/profile-teardown/stop) release paths through one ownership function.
- **Files:** `Services/InputHookService.cs`. **Risk:** M. **Tests:** 2- and 3-source→1-target, every release order, right-click combos, profile switch, stop, failed target DOWN.

### F-012 — Caps release before consulting live settings
- **Approach:** release recorded caps state **before** the enable/mode gate. If `_capsShiftEngaged`, always perform its matching Caps-off action. Release `_capsRemappedKey` by the **recorded** key on disable, retarget, profile change, and physical UP.
- **Files:** `Services/InputHookService.cs`. **Risk:** S-M. **Tests:** mutate enabled/mode/target between DOWN and UP → exact paired release incl. profile change / stop / global-vs-active.

### F-002 — Ordered injector for remaining synchronous hook injection (codex xhigh)
- **Approach:** route CapsLock-remap, `FireTapKey`, combined-mapping sends, Windows-launcher dummy-key, and `ForceCapsLockState` through the ordered injector via the F-020a seam. Keep only suppression/state **decisions** in the callback. Use generations + acknowledgements so queued DOWN/UP stay ordered across profile switches, failed sends, and `Stop()`. **Do not** add per-feature queues that can reorder releases. CapsLock Hold-mode `GetKeyState` read-check-act must move to the worker as a unit if migrated (TOCTOU). Migrate incrementally (CapsLock-remap + FireTapKey first per prior survey), codex-gating each site.
- **Files:** `Services/InputHookService.cs`, injector. **Risk:** High. **Tests:** blocking native fake must not delay callbacks; FIFO DOWN/UP pairing, release epochs, profile-switch cancellation, failure reporting, no injection after stop.

### F-013 — Structured injection outcome + bounded UP recovery (rides on F-002)
- **Approach:** `SendKey` returns a structured result (distinguish UIPI-blocked zero-return). Retain failed-UP ownership in a **bounded** recovery set + retry when a permissible desktop/context returns, suppressing duplicate DOWN. Lightweight — integrate with the F-002 injector; skip the elaborate variant.
- **Files:** `Services/InputHookService.cs`, injector. **Risk:** M (bounded by F-002). **Tests:** fail each UP once/permanently → ownership retention, bounded retry, no duplicate DOWN, final cleanup on boundaries.

### F-020b — Behavior tests for hook invariants
- **Approach:** with the seam + a fake clock/barriers, test invariants (not private details): every DOWN has one eventual UP; refcount correctness (F-011); caps paired release (F-012); stale generation cannot affect a replacement run; callback returns under a blocking native fake; no injection after stop.
- **Files:** `Tests/`. **Risk:** Low. **Tests:** the above as the suite.

---

## Phase 4 — Security hardening (code-level; med risk)

### F-004 — Refuse HIGHEST task for a writable target
- **Approach:** before creating a `/RL HIGHEST` task, resolve the final exe path, reject reparse points where appropriate, and verify the file **and every ancestor directory** ACL denies write/delete/rename to non-admin principals; refuse otherwise with a clear message. Make `TryDisableScheduledTask` tri-state (query timeout/error ≠ "absent"). Installer into Program Files = documented follow-up.
- **Files:** `Services/StartupService.cs`. **Risk:** M (logic), Low (blast radius). **Tests:** ACL matrix Program Files vs user-writable; reparse path; failed query/delete; action == verified canonical path.

### F-005 — Elevated process rejects user-writable admin launcher policy
- **Approach:** when the app runs elevated, do **not** honor a `RunAsAdmin=true` launcher target loaded from user-writable `Win.ini` without an explicit admin-approved allowlist / re-authorization; validate canonical paths. Ordinary (non-elevated) launcher prefs unchanged. Broker architecture = documented follow-up.
- **Files:** `Configuration/IniProfileStore.cs`, `Services/InputHookService.cs` (`HandleWindowsLauncher`/`LaunchProcess`), `Services/ProcessLauncher.cs`. **Risk:** M. **Tests:** tampered `Win.ini` under standard token → elevated instance refuses changed admin binding; canonical-path replacement; explicit re-authorization.

---

## Phase 5 — Startup low-severity + toolchain isolation

### F-023 — WinEvent unhook on the install thread
- **Approach:** marshal `UnhookWinEvent` to the thread that installed it (dispatcher); check the Boolean result and preserve the handle on failure (allow retry). Minimal version — no dedicated message-loop-thread redesign.
- **Files:** `Services/ForegroundWatcher.cs`, `App.xaml.cs`. **Risk:** S. **Tests:** install/uninstall thread identity, failed-unhook handle retention, repeated start/stop.

### F-024 — Visible malformed-INI diagnostics (lightweight)
- **Approach:** emit field-level warnings (file, section, key, reason) when a typed getter falls back to default; **skip** the `SchemaVersion`/migration framework. Optional: hold a degraded flag so autosave doesn't silently normalize away a value the user hasn't acknowledged (evaluate risk; may defer the no-rewrite half).
- **Files:** `IniExtensions.cs`, `IniDocument.cs`, `Configuration/IniProfileStore.cs`. **Risk:** S-M. **Tests:** invalid value per typed getter → warning emitted; no crash.

### F-019b — xUnit v3 migration (isolated; own codex gate)
- **Approach:** migrate `Tests.csproj` to `xunit.v3` + current test SDK/runner; preserve all cases; regenerate `packages.lock.json`; confirm the two High test-only advisories are gone. Isolated so it can't destabilize fix work.
- **Files:** `Tests/Tests.csproj`, test files as needed. **Risk:** M (test-only). **Tests:** all cases discovered in CLI + VS; `--vulnerable` clean.

---

## Phase 6 — Dead code & cleanup (low risk; verify no references first)
- Remove: `MouseButtonBindingViewModel`, `AvailableKeysConverter` (+ XAML resource), `BooleanNegationConverter`/`NotConverter` (+ resource), unused tray APIs `ShowBalloon`/`SetIcon`, unused image assets (keep `Icon.ico`), `AddProfileDialogOptions.IsProfileNameReadOnly`, `DisplayColorProfile.ResetToDefaults`, unused `WaitForAsync` test helpers, unused whole-desktop gamma helper + its interop, `System.Reflection` import in `ProcessLauncher.cs`.
- **Dispose the five per-button timers** in `InputHookService.Dispose()` + add a create/dispose test.
- Scope `Marshal.FinalReleaseComObject` in a `finally` for **locally acquired** Explorer RCWs only (`ProcessLauncher.cs`).
- **Leave** `FABLE.md` / `*_Plan.md` / `codex_review.md` — the user's working history (offer to move to `docs/history/` later; do not delete).
- **Risk:** Low. **Tests:** build + targeted exercise of each touched surface.

---

## Deferred (documented, NOT implemented this effort)
- **F-019c** — .NET 8 → .NET 10 LTS framework bump. Isolated future change; validate WPF/WinForms/P-Invoke/hook timing separately. **Must land before 2026-11-10 (.NET 8 EOS).**
- **F-004/F-005 infra** — signed installer into an ACL-protected root + privileged-broker IPC.
- **F-009 full** — guaranteed exact baseline capture across all HDR/ICC/vendor scenarios (best-effort implemented now).

## Suggested execution order (dependency-aware)
Phase 0 (all) → Phase 1 (F-014, F-015, F-016, F-017, F-007, F-008) → Phase 2 (F-003, F-018, F-006, F-009, F-010, **F-001**) → Phase 3 (F-011, F-012, **F-002**, F-013, F-020b) → Phase 4 (F-004, F-005) → Phase 5 (F-023, F-024, F-019b) → Phase 6 (cleanup) → **final `/codex` review of the whole codebase.**

---

## Codex plan review — verdict "Sound-with-fixes" — amendments folded in (2026-07-10)

**Active scope = Tier 1 batch** (F-025, F-021, F-022, F-014, F-015, F-016, F-017, F-007, F-008, F-003, F-018, F-006, F-010, F-011, F-012, F-001). Implement order: **A** foundation (F-025, F-021, F-022) → **B** data-integrity (F-014, F-015, F-016, F-017, F-007, F-008) → **C** color-safe (F-003, F-018, F-006) → **D** hook-safe (F-011, F-012) → **E** F-001 **+** F-010 (gated). Build+test+codex gate each batch.

- **F-001 (design LOCKED by codex):**
  (a) Fast input path = a **single-reader, NON-dropping FIFO worker** — *not* `Task.Run`-per-event and *not* the current capacity-1 `DropOldest` channel. Each item = `{ hwnd, pid, exe, generation, resolvedProfile }`; worker performs full `Deactivate/ReleaseAllState → Activate` **in order**.
  (b) Publish a monotonic `generation` **synchronously** in `OnForegroundChanged`. Every profile-scoped hook **NEW-DOWN / suppression** path gates on `activeProfileGeneration == publishedGeneration` (identity/exe match alone breaks A→B→A). **Release / UP paths stay UNGATED** (no-stuck-key invariant).
  (c) Input activation stays on the **pool worker, never the dispatcher/hook thread** (`ReleaseAllState` does synchronous key-ups under subsystem locks → dispatcher would re-introduce hook-timeout/loss).
  (d) Color becomes a **separate coalescing worker** carrying a generation; it discards stale queued plans before hardware work and **never mutates input/tray active-profile state**.
  (e) Implement **with F-010 shutdown ownership**: `StopAsync` must prevent late activation before `InputHookService.Stop()`.
- **F-010:** co-implemented with F-001 (a second worker invalidates the current single-worker stop logic).
- **F-012:** release **recorded** caps state BEFORE consulting live enable/mode gates — do this independent of (and before) any injector work.
- **F-007:** `ProfileKind` assigned from **load origin only** — `LoadWindowsProfile`/`LoadColorProfile` set Windows/Color; **every** file under `Profiles\` is `Custom` even if its `Name` is reserved; **never** deserialize Kind from INI. Switch **all** `IsWindowsProfile`/`IsColorProfile` consumers (manager accessors, dedup, save/delete routing, UI protection) to Kind. Keep the custom `SourcePath`, suffix the display name, verify first autosave writes only that custom file.
- **F-008:** wrap **each** built-in load AND directory enumeration independently (a `Win.ini` failure must not skip `Color.ini`); handle **ctor-time** directory-create failure (before `LoadProfilesAsync`); mark a failed built-in **persistence-degraded/read-only** so autosave can't overwrite the preserved source with defaults; defer the user warning until UI/tray is up.
- **F-020 ordering:** deterministic behavior tests (blocking-color A→B→A with every transition observed; generation gating; FIFO DOWN/UP across switch/stop; failed-UP recovery; Caps live-edit release) land **before** F-001 as its regression gate.

**Deferred-tier amendments recorded for their phases:** F-002 (#5 build one injector command stream + delivery ledger + epochs + non-blocking acks first, move each feature's *complete* ownership closure together, never drop stateful commands; #6 do NOT migrate `ForceCapsLockState` by just moving its `GetKeyState` check — the injector thread ≠ hook thread, needs a dedicated Caps authority; #7 `FireTapKey` = atomic injector tap w/ unconditional paired UP, launcher dummy must ack-before-launch). F-009 (#10 **drop** `GetDeviceGammaRamp` baseline capture — a TRUE read/write doesn't mean calibration ownership; keep documented limitation + optional NVAPI-DVC-only baseline, never synthesize identity/zero as "restore").
