# AUTORUN_ANTIAFK_ADVANCED_Plan.md — Auto-Run, Anti-AFK, and Advanced Mode

**Baseline:** `main` @ `4ac0c53` (pushed). Build 0 errors (160 pre-existing CA1416), tests 99/99.
**App intent:** gaming — reliable timers/execution, zero regression, **no stuck keys**, bindings never silently die.
**Scope:** three new user-facing features plus one global safety gate. Touches models, one VM, MainWindow XAML/code-behind, IniProfileStore, InputHookService, the Settings window trio, and the two service seams (`IInputHookService` + `FakeInputHookService`).

This plan is written to be applied phase-by-phase and reviewed by `/codex` between phases, mirroring the discipline of `INJECTOR_MIGRATION_Plan.md` / `FABLE_Plan.md`.

---

## 0. What we reuse (so nothing is invented that already exists)

Every new behavior maps onto a proven idiom already in the file. **No new injection mechanism is introduced.**

| Need | Existing idiom being reused | Anchor |
|---|---|---|
| Persistent held key (Auto-Run W / sprint) with guaranteed release | Hold-breath FIFO injector: record injected key under lock, enqueue DOWN/UP, unconditional release | `InputHookService.cs:149-151`, `:1763-1811`, `:1832-1911` |
| Timed self-releasing taps (Anti-AFK W/A/S/D) | `HoldBreathInjection` with `PreSleepMs` (DOWN, sleep, UP), or `FireTapKey` | `:1774-1795`, `:2016-2063` |
| Feature = `Settings` class on `Profile` | `RightClickHoldBreathSettings` etc. | `Models/*.cs`, `Models/Profile.cs:26-37` |
| Per-feature INI section | `Serialize/DeserializeRightClickHoldBreath` | `IniProfileStore.cs:353-360`, `:471-475` |
| Live global `[App]` flag, service-applied | `HookWatchdogEnabled` end-to-end | see §4 |
| Key dropdowns in UI | `ComboBox` + `ComboBoxKeySelectionBehavior` bound to `KeyOptions` | `MainWindow.xaml:597-609` |
| Cancel/release on every teardown | `ReleaseAllState()` | `:2220-2253` |
| Physical modifier re-derivation across profile switch | `RederivePhysicalModifierState()` | `:2263-2275` |

### Invariants every phase MUST preserve (carried from the hold-breath / injector / input-sleep work)
- **I1 — Injected-filter-first.** Both hook callbacks early-out on `LLKHF_INJECTED`/`LLMHF_INJECTED`/`INPUT_IGNORE` before any side effect (`:977-981`, `:1063-1066`). Auto-Run's own injected W/sprint/WASD are `INPUT_IGNORE`-tagged (`SendKey`/injector already do this, `:2094`) so they **never** feed Auto-Run's own cancel logic or physical-state tracking. Never reorder.
- **I2 — Record before/atomically-with injection.** Any injected DOWN is recorded in a field before (or in the same locked section as) the enqueue, so a release path can always force the UP.
- **I3 — Release paths are unconditional** (no `IsEnabled`/profile/AdvancedMode gate on an UP path).
- **I4 — `_isRunning` flips false before `ReleaseAllState` in `Stop()`;** injection sites re-check it under their lock before enqueuing.
- **I5 — Lock ordering stays one-way.** New `_autoRunLock` must never nest with `_holdBreathLock`/`_transientTapLock`/`_combinedOverridesLock` in a way that inverts existing order. Recommended: Auto-Run holds only `_autoRunLock`, and while holding it calls only enqueue-on-the-injector (which takes no lock, `:1832-1849`) and `SendInput` — never another subsystem lock. Grep-verify after each phase.
- **I7 — Shutdown-skippable DOWN, no-op paired UP.** Any injector producer relies on the worker's `IsAddingCompleted` drain discriminator (`:1866-1911`) — reuse it, do not re-derive it.
- **Gate for every phase:** `dotnet build .\sWinShortcuts.csproj -c Release --no-incremental` → 0 errors; `dotnet test .\Tests\Tests.csproj` → all green (currently 99/99). `/codex` read-only review (gpt-5.5, high) before a phase is "done". **No commits unless the user asks.**

---

## 1. Decisions locked from the interview (2026-07-08)

1. **Auto-Run trigger = modifier + key chord** (e.g. `Ctrl+R`). New chord detection in the keyboard hook. The chord is a **toggle** (press → on; press again → off).
2. **Seamless W-handoff — implemented as "cancel only on a *fresh physical down-edge* of W / S / sprint", never on a release.** A W held *through* activation produces no new down-edge, so releasing it keeps you running (seamless); a W *tapped after* activation is a fresh edge, so it cancels. This directly satisfies the user's constraint that a first physical W press *after activation* must still cancel — nothing is "absorbed".
3. **Anti-AFK is idle-gated** (fires only after real physical idle ≥ interval), while the profile's game is foreground.
4. **Advanced Mode gates:** Auto-Run (whole feature), Anti-AFK (whole feature), Hold-Breath (whole feature), and the **ability to un-check "Suppress"** under Combined Mappings (Suppress-off = 1→2 input = not 1:1). **Alt-Mouse stays ungated** (button→key is 1:1).

### Defaults chosen without asking (call out any you want changed)
- **Sprint sub-option** exposes both modes (`Hold` = hold sprint down for the whole run; `Press` = tap sprint once at activation, for games where sprint is an in-game toggle). Sprint key is user-selected (default `LeftShift`).
- **"Sprint bound to disable criteria"** interpreted as **both**: a fresh physical sprint press cancels Auto-Run (like W/S) **and** cancelling always releases any injected sprint hold (no stuck sprint).
- **A/D do not cancel** (you can strafe while auto-running). Cancel set = { W, S, sprint }.
- **Anti-AFK WASD = sequential taps** (W↓W↑ · gap · A↓A↑ · gap · S↓S↑ · gap · D↓D↑), net-zero displacement, each with human jitter. Not simultaneous (simultaneous W+S / A+D cancel and read as a single frame).
- **Anti-AFK never fires while Auto-Run is active** (Auto-Run already counts as activity/movement; overlapping a WASD tap onto a held W is nonsense).
- **Auto-Run / Anti-AFK config is per-profile** (consistent with every other feature). Advanced Mode is the one **global** `[App]` flag.
- **Advanced Mode default:** `false` for a fresh install (honors "disabled initially"), but **`true` on upgrade of an existing install that already has any gated feature enabled** (so the user's existing Hold-Breath profiles don't silently go inert). See §4.4 — flag for confirmation.
- **Trigger chord key + Anti-AFK toggle** are consumed (suppressed from the game) when they fire; cancel keys (W/S/sprint) **pass through** to the game (pressing S to cancel should also move you back).

---

## 2. Feature 1 — Auto-Run

### 2.1 Model — `Models/AutoRunSettings.cs` (new)
```
public sealed class AutoRunSettings
{
    public bool IsEnabled { get; set; } = false;
    public ModifierKeys TriggerModifier { get; set; } = ModifierKeys.Control; // System.Windows.Input
    public Key TriggerKey { get; set; } = Key.R;
    public bool SprintEnabled { get; set; } = false;
    public Key SprintKey { get; set; } = Key.LeftShift;
    public SprintActivation SprintMode { get; set; } = SprintActivation.Hold;
    public AutoRunSendMode SendMode { get; set; } = AutoRunSendMode.Foreground; // Background = experimental, see §11.5
}
public enum SprintActivation { Hold = 0, Press = 1 }
public enum AutoRunSendMode { Foreground = 0, Background = 1 }
```
- `ModifierKeys` (flags: Control/Alt/Shift/Windows) models the chord modifier and is already referenced across WPF. `TriggerKey` reuses the `KeyOptions` dropdown. **Constraint: exactly one, side-agnostic modifier** (the UI dropdown offers only the four single values). This keeps `IniExtensions.GetEnum`'s `Enum.IsDefined` guard happy — a single flag like `Control` (=2) is a defined member and round-trips via `SetEnum`/`GetEnum`; a *combined* modifier (e.g. `Control|Alt`=6) is **not** `IsDefined` and would silently fall back to default, so combined modifiers are intentionally unsupported. Side-agnostic = detection checks `VK_CONTROL`/`VK_MENU`/`VK_SHIFT`/`VK_LWIN|VK_RWIN` so either physical Ctrl works.
- Add `public AutoRunSettings AutoRun { get; init; } = new();` to `Profile.cs` (`:26-37` block).
- **Movement/cancel keys are hardcoded** (W=0x57, S=0x53, A=0x41, D=0x44) — not user-configurable, matching the "hold W, cancel on W/S" spec.

### 2.2 Persistence — `IniProfileStore.cs`
Mirror `Serialize/DeserializeRightClickHoldBreath` exactly.
- New `[AutoRun]` section: `Enabled`, `TriggerModifier` (SetEnum/GetEnum on `ModifierKeys`), `TriggerKey` (SetKey/GetKey), `SprintEnabled`, `SprintKey`, `SprintMode`, `SendMode` (SetEnum/GetEnum on `AutoRunSendMode`; missing/old INIs → `Foreground`).
- Call `DeserializeAutoRun` from the load path (beside `:189`) and serialize in `SerializeProfile` (beside `:471-475`). New profiles/old INIs missing the section keep model defaults (feature off) — no migration needed.

### 2.3 Runtime — `InputHookService.cs`

**Auto-run active state (guarded by a new `private readonly object _autoRunLock = new();`):**
- `bool _autoRunActive;`
- `bool _autoRunMoveInjected;` (recorded injected W, I2; move key is the constant `Key.W`)
- `bool _autoRunSprintInjected;` + `Key _autoRunSprintInjectedKey;`
- `bool _autoRunTriggerKeyConsumed;` (suppress the held trigger key's repeats + matching keyup)

**Physical down-state — hook-thread-only, NOT under `_autoRunLock`, NOT cleared on release, but RE-SEEDED FROM GROUND TRUTH AT EVERY ACTIVATION (revised — codex R1 #2):**
- `bool _wPhysicallyDown, _sPhysicallyDown, _sprintPhysicallyDown;` — maintained like the lock-free `_altPressed`/`_rightButtonPressed` benign fields (`:987-1006`). They track *physical reality independent of auto-run state* (clearing on cancel would make "re-activate while still holding W" self-cancel on the next auto-repeat). Written only by the keyboard hook (single thread); our own injected W/sprint are `INPUT_IGNORE` (I1) so they never reach here.
- **The key robustness fix:** `ActivateAutoRun` **re-seeds all three flags from `GetAsyncKeyState`** at the moment it activates (`_wPhysicallyDown = (GetAsyncKeyState(0x57)&0x8000)!=0`, S=0x53, sprint=`ToVk(snapshotSprintKey)`). This eliminates codex's Scenario A/B (W held *before* the profile activated, or an up/down dropped during a `_keyboardReplacementInProgress` fail-open gap `:955-958`): every run begins from the true physical state, so a held-through-activation W is correctly seen as *already down* (no false cancel) and a genuinely-up W is correctly *fresh* on next press (no missed cancel).
- **Why activation-seeding is sufficient (not also re-deriving on reinstall):** a fail-open gap only occurs during a hook reinstall, and every reinstall runs `ReleaseAllState` (`:832`,`:869`) which **releases Auto-Run** — so there is no window where a run stays active across a gap with stale flags; the next activation re-seeds. Re-deriving in `RederivePhysicalModifierState` would additionally be *wrong*, because it runs before `_activeProfile` is assigned in `ActivateProfile` (`:892-894`) and so cannot see the profile's sprint key.
- **Residual, now genuinely bounded:** a missed event with NO accompanying `ReleaseAllState` (e.g. a key released on the UAC secure desktop mid-run) could still stale a flag. This cannot *stick* a key — the chord toggle re-press, any profile/session switch, `Stop()`, and Advanced-Mode-off all force `ReleaseAutoRunState`. Worst case is one rare missed W/S cancel that the user clears with the chord. Documented, not hand-waved.
- **Sprint key handling:** snapshot the sprint `Key` into `_autoRunSprintKey` at activation and use that snapshot for both injection and cancel-matching for the run's lifetime — so a mid-run config edit can never leave `_sprintPhysicallyDown` tracking the wrong VK (codex R1 #2 sub-point).

**Handler — new `HandleAutoRun(int vkCode, bool isKeyDown, bool isKeyUp)`, called UNCONDITIONALLY at the top of the dispatch region (right after the Alt-tracking block, `:1006`), BEFORE the `handled = HandleCapsLock || ...` chain:**
```
var autoRunSuppress = HandleAutoRun(vkCode, isKeyDown, isKeyUp);
if (autoRunSuppress) return (IntPtr)1;
var handled = HandleCapsLock(...) || HandleCombinedMappings(...);
...
```
Running it before (not inside) the short-circuit chain is required: a cancel key (W/S) may *also* be a Combined-Mappings source; if HandleAutoRun sat inside the `||` chain it would be skipped whenever another feature consumed the key, and cancel detection would silently break. HandleAutoRun returns `true` **only** to suppress the trigger chord key; every other path returns `false` so W/S/sprint still flow into the normal chain and reach the game.

Logic:
1. **Maintain physical down-state (always, W/S/sprint VKs only):** on a non-injected keydown for W/S/sprint, capture `wasDown` before setting the flag true; on keyup, set false. Runs regardless of whether Auto-Run is enabled/active/advanced — pure physical bookkeeping.
2. **Cancel on fresh down-edge:** if `_autoRunActive` (read under `_autoRunLock`) and this is a *fresh* physical down (`!wasDown`) of W, S, or the profile's sprint key → `ReleaseAutoRunState()`; return **false** (pass the key through — pressing S to cancel must still move you back).
3. **Toggle on trigger chord:** if the feature is usable (`_advancedModeEnabled` and `_activeProfile?.AutoRun.IsEnabled`), `isKeyDown`, `vkCode == ToVk(TriggerKey)`, it is a *fresh* down (not auto-repeat, via a `_triggerKeyDown` flag), and the single `TriggerModifier` is physically down (`GetAsyncKeyState`, same idiom as `RederivePhysicalModifierState` `:2265-2274`):
   - `_autoRunActive ? ReleaseAutoRunState() : ActivateAutoRun()`; set `_autoRunTriggerKeyConsumed = true`; return **true** (suppress).
   - While `_autoRunTriggerKeyConsumed`, suppress the trigger key's auto-repeats and its matching keyup (clear the flag on that keyup), so no dangling trigger keyup leaks to the game.

**`ActivateAutoRun()` (under `_autoRunLock`, re-check `_isRunning`):**
- snapshot `settings = _activeProfile.AutoRun`; snapshot `_autoRunSprintKey = settings.SprintKey`; `_autoRunActive = true`.
- **Seed physical-down flags from `GetAsyncKeyState`** (W/S/`_autoRunSprintKey`) — see the boxed note above; this is what makes the handoff robust against pre-activation holds and dropped events.
- **Transport branch on `settings.SendMode`:** `Foreground` → enqueue W-down on the injector (below); `Background` → capture/validate `_autoRunTargetHwnd` and `PostMessage(WM_KEYDOWN)` W instead, per §11.5. All record/release bookkeeping is identical; only the transport differs.
- Enqueue W-down on the injector, record `_autoRunMoveInjected = true`. (If physical W is currently down too, both are down — harmless; the injected W is what survives the physical release. This is the seam that makes the handoff seamless.)
- If `SprintEnabled`:
  - `Hold` → enqueue sprint-down, `_autoRunSprintInjected = true`, `_autoRunSprintInjectedKey = SprintKey`.
  - `Press` → enqueue a self-releasing sprint tap (DOWN + UP-with-`PreSleepMs=tapDuration`, same shape as Toggle hold-breath `:1791-1792`); do **not** mark it held.
- Debug-log `AutoRun ACTIVATED` (key, sprintMode).

**`ReleaseAutoRunState()` → `lock(_autoRunLock) ReleaseAutoRunStateLocked()` (unconditional, I3):**
- if `_autoRunMoveInjected` → enqueue W-up, clear.
- if `_autoRunSprintInjected` → enqueue sprint-up (`_autoRunSprintInjectedKey`), clear.
- `_autoRunActive = false`; clear `_autoRunTriggerKeyConsumed`. **Do NOT touch the `_wPhysicallyDown/_sPhysicallyDown/_sprintPhysicallyDown` fields** (they track physical reality, not auto-run state — see above).
- Enqueue-only (no SendInput under the lock) ⇒ I5 preserved; the injector's FIFO guarantees each UP lands behind its DOWN even if the DOWN is still queued (`:1798-1801`).

**Teardown wiring:**
- `ReleaseAllState()` (`:2220-2253`): add `ReleaseAutoRunState();` (alongside `ReleaseHoldBreathState()`), so profile switch, session-switch (lock/logoff), watchdog reinstall, and `Stop()` all release W/sprint. This is the single most important no-stuck-key line for this feature.
- `Stop()` already drains the injector *after* `ReleaseAllState` (`:475-487`) → the enqueued W-up/sprint-up execute before the queue closes. No new Stop() logic needed.
- `HandleAutoRun` reads `_activeProfile` lock-free (benign staleness, identical to `HandleRightClickHoldBreathDown` `:1618`).

### 2.4 Why the handoff is correct (the subtle part, for the reviewer)
- **Held-through-activation:** physical W already down ⇒ `_wPhysicallyDown == true` at activation. Auto-repeat of W → "already down" → not a fresh edge → no cancel. Physical W **release** → we cancel on *down*, not *up* → no cancel; injected W keeps the game moving. Later fresh W press → cancel. ✅ seamless, and the first *post-activation* fresh W still cancels.
- **Not-held-at-activation:** `_wPhysicallyDown == false` ⇒ the very next physical W down is fresh ⇒ cancels. ✅ (the user's explicit requirement).
- No release is ever "absorbed", so the failure mode the user flagged ("first W after activation gets ignored") cannot occur.

---

## 3. Feature 2 — Anti-AFK

### 3.1 Model — `Models/AntiAfkSettings.cs` (new)
```
public sealed class AntiAfkSettings
{
    public bool IsEnabled { get; set; } = false;
    public int IntervalMinutes { get; set; } = 5;   // clamp 1..15 on load + use
}
```
Add `public AntiAfkSettings AntiAfk { get; init; } = new();` to `Profile.cs`. INI `[AntiAfk]` section (`Enabled`, `IntervalMinutes`), clamp `Math.Clamp(x,1,15)` at load (mirror the Delay clamp `:360`).

### 3.2 Idle source
Reuse the watchdog's `GetLastInputInfo`/`LASTINPUTINFO` interop already in `NativeMethods` (added for P8, referenced by `TryGetWatchdogAges` `:712`). Physical idle = `GetTickCount() - LASTINPUTINFO.dwTime`.

### 3.3 Runtime — one always-ticking `System.Threading.Timer _antiAfkTimer`
- Created in `Start()` at a **fixed coarse period** (e.g. 5 s, `WATCHDOG`-style slot `:385`), disposed in `Stop()`/`Dispose()`. **No arm/disarm** — a fixed periodic tick that reads live conditions each time is simpler than arming on `ActivateProfile` and, crucially, picks up a UI toggle of `AntiAfk.IsEnabled` on the *already-active* profile within one period (arm-on-activate would miss it). It also sidesteps the `ActivateProfile` ordering hazard (Rederive/ReleaseAllState run before `_activeProfile` is assigned, `:892-894`). The ~5 s no-op tick is negligible.
- **Tick body `AntiAfkTick()` fires one WASD sequence iff ALL hold** (single-flight guard via `Interlocked` like `_watchdogTickRunning` `:208`, so a slow injector can't overlap ticks):
  1. `_isRunning` and not `_disposed`;
  2. `_advancedModeEnabled` (gated feature — when Advanced Mode flips off, the next tick simply no-ops; no explicit disarm needed);
  3. `p = _activeProfile` non-null, `p.AntiAfk.IsEnabled`;
  4. **not** `_autoRunActive` (read under `_autoRunLock`) — Auto-Run is activity;
  5. physical idle `≥ intervalMs` **AND** `now - _antiAfkLastFireTick ≥ intervalMs`. Idle = unsigned `GetTickCount() - LASTINPUTINFO.dwTime` (wrap-safe uint math, as the watchdog already does). The second clause fixes cadence regardless of whether our own injected WASD advances `GetLastInputInfo` — robust either way.
  6. **Foreground still matches the active profile (revised — codex R1 #3):** resolve the current foreground window's process (`GetForegroundWindow`→`GetWindowThreadProcessId`→process name, via the existing `ExecutableName`/`ForegroundWatcher` helpers) and require it equals `p.NormalizedExecutable`. **This guard is mandatory, not cosmetic:** `ProfileActivationService.ProcessForegroundChange` runs `ApplyColorPlan` *before* `_inputHookService.ActivateProfile/DeactivateProfile` (`ProfileActivationService.cs:212` vs `:220`/`:224`), so after you alt-tab away there is a real window where `_activeProfile` is still the game while a browser is foreground. Without this check an idle-satisfied tick would inject WASD into that browser. With it, Anti-AFK physically cannot type into a non-matching window.
- On fire: enqueue the ONE atomic WASD sequence item (§3.4) on the shared injector, set `_antiAfkLastFireTick = GetTickCount()`, debug-log.

**"Game focused" is enforced by TWO things:** (a) Anti-AFK only ticks meaningfully while a game profile is the *active* profile, and (b) the mandatory fire-time foreground-process check (guard #6) that closes the async-activation window. Both are required — (a) alone is insufficient because `_activeProfile` lags the true foreground during `ProfileActivationService`'s color work (codex R1 #3). Together, WASD can only ever land in the profile's own game window. (Do **not** attempt background/PostMessage injection — out of scope, anti-cheat-hostile, and breaks the no-stuck-key model.)

### 3.4 WASD sequence via ONE atomic injector item (revised — codex R1 #1)
**A naive 8-item enqueue `(W↓)(W↑)(A↓)(A↑)…` is NOT shutdown-safe** and must not be used. The drain guard (`:1877`) only skips DOWNs *still in the queue*; a DOWN already dequeued-and-sent before its paired UP is enqueued has no protection, and `EnqueueHoldBreathInjection` swallows the completed-queue throw (`:1840-1848`). Hold-breath's Toggle pair escapes this only because it enqueues both halves under `_holdBreathLock`, which `Stop()`→`ReleaseAllState`→`ReleaseHoldBreathState` also takes, serializing the pair ahead of `CompleteAdding` (`:475`→`:482`). Anti-AFK holds no such lock, so a split pair would strand a movement key.

**Fix — extend the injector item to carry an optional atomic tap-sequence** (this is exactly the queue-item variant the `INJECTOR_MIGRATION_Plan.md` P3b already anticipated):
- `record struct KeyInjection(Key Key, bool IsDown, int PreSleepMs, TapStep[]? Sequence = null)` where `TapStep(Key Key, int DownMs, int GapMs)`.
- Anti-AFK enqueues **one** item carrying the whole `[W,A,S,D]` sequence (durations/gaps jittered via the `HOLD_BREATH_TAP_DURATION_*` + RNG-warmup idiom `:1783-1790`).
- In `HoldBreathInjectionLoop`, before the existing `IsDown && IsAddingCompleted` skip: if `injection.Sequence is { } steps`, execute the sequence as a loop with a **per-step abort check at the TOP of each iteration, BEFORE that key's DOWN** (revised — codex R2 #1/#2):
  ```
  foreach step in steps:
      if (!_isRunning || queue.IsAddingCompleted || !ForegroundMatchesActiveProfile()) break;  // abort between paired steps only
      try { SendKey(step.Key, down); Thread.Sleep(step.DownMs); } finally { SendKey(step.Key, up); }
      Thread.Sleep(step.GapMs);
  ```
  - **Abort granularity is the fully-paired step, never between a DOWN and its UP** — the `finally` guarantees every DOWN that fired gets its UP, so aborting can't strand a key; and because the check runs *before* each key's DOWN (including the first), a mid-sequence alt-tab / profile switch / `Stop()` means the remaining keys are simply **not pressed** rather than leaking into the new window or overrunning shutdown.
  - This bounds the worst-case post-`Stop()` drain to **at most one key-pair** (≤ ~2× a foreign-hook-stall, well inside `Stop()`'s `Join(2000)` `:483`) instead of a whole 4-step sequence — which, at the ~300 ms-class `SendInput` stalls the injector comments already document (`:1852-1870`), could otherwise outlive the join and inject into a later session. The `Join(2000)` is thus a *foreign-hook-stall* bound, not a *sequence-duration* bound.
  - `ForegroundMatchesActiveProfile()` re-uses guard #6's check (foreground process == `_activeProfile?.NormalizedExecutable`); reading `_activeProfile` on the injector thread is the same benign lock-free read used elsewhere.
- This adds **no new lock and no recording** — pairing atomicity comes from the per-key `finally`; abort-safety from the between-step checks. The proven DOWN/UP/PreSleep paths are untouched (the abort logic lives only in the `Sequence` branch).
- Contention with Auto-Run/Hold-Breath on the shared queue is a non-issue (guard #4 + you cannot be physically idle while right-clicking).

---

## 4. Feature 3 — Advanced Mode (global safety gate)

### 4.1 The live flag — mirror `HookWatchdogEnabled` end-to-end
- **`IInputHookService`** (`:15`): add `bool AdvancedModeEnabled { get; set; }`.
- **`InputHookService`**: `private volatile bool _advancedModeEnabled;` + property mirroring `HookWatchdogEnabled` (`:214-225`). **Setter, when transitioning true→false, must release ALL gated held state** so nothing keeps injecting under a now-off gate, calling each **sequentially, never nested** (each takes only its own leaf lock — `_autoRunLock`, `_holdBreathLock`, `_combinedOverridesLock` — and must not be held simultaneously, I5):
  1. `ReleaseAutoRunState()` — releases injected W/sprint.
  2. `ReleaseHoldBreathState()` — releases the injected hold-breath key.
  3. **`ReleaseUnsuppressedCombinedOverrides()` (new — codex R1 #5):** under `_combinedOverridesLock`, for each active entry in `_activeCombinedOverrides` with `SuppressOriginal == false`: **remove it from the dict AND enqueue its target-key UP on the injector** (NOT a synchronous `SendKey`). Update `_activeCombinedOverrideCount`. Filtered to the *gated* un-suppressed ones — suppressed 1:1 overrides are game-safe and stay. Removing the dict entry means a later physical source-keyup finds nothing to release (`:1328` `Remove`→false) so it can't double-send. Without this, a source key held with Suppress-off when the user flips Advanced off keeps its injected target down until physical keyup — the gate wouldn't take effect on the in-flight press.
  - **Thread-safety note (important):** the setter runs on the UI **dispatcher thread, which is the hook thread**. So all three releases must be **enqueue-only / non-blocking** — `ReleaseAutoRunState` and `ReleaseHoldBreathState` already are (injector); this is precisely why #3 must enqueue the target UP on the injector rather than call the existing synchronous `ReleaseAllOverrides` shape (`:2191`, which is safe only because it runs on the pool thread via `ReleaseAllState`). A synchronous `SendInput` here could stall the dispatcher on a foreign hook = the freeze the injector exists to prevent. Anti-AFK needs no action (its tick self-gates on guard #2). Log the transition.
- **`FakeInputHookService`** (`:10`): `public bool AdvancedModeEnabled { get; set; }` auto-prop.
- **`SettingsViewModel`** (`:63-75`): add `AdvancedModeEnabled` property that live-applies to `_inputHookService` (identical to `HookWatchdogEnabled`).
- **`SettingsWindow.xaml`**: add a checkbox under a new "Advanced" (or existing "Troubleshooting"-style) header — **`Content="Enable Advanced Mode"`**, `IsChecked="{Binding AdvancedModeEnabled, Mode=TwoWay}"`, with a tooltip: *"Enables non-1:1 automation (Auto-Run, Anti-AFK, Hold-Breath, and un-suppressed key mappings). Off = only game-safe 1:1 remaps."* Window `Height` grows ~40px.
- **`SettingsWindow.xaml.cs`** `LoadState`/`SaveIni` (`:42-61`): read/write `[App] AdvancedMode` (see default rule §4.4).
- **`MainWindow.xaml.cs`** startup (`:54-57`): `_inputHook.AdvancedModeEnabled = <resolved default>;`.

### 4.2 Runtime gating (service side)
- **Auto-Run:** `HandleAutoRun` treats `!_advancedModeEnabled` as feature-off (no activation), but the setter already released any active run when the flag went false.
- **Anti-AFK:** `AntiAfkTick` guard #2 (`_advancedModeEnabled`).
- **Hold-Breath:** `HandleRightClickHoldBreathDown` (`:1616-1622`) adds `|| !_advancedModeEnabled` to its early-out. The UP/release path (`:1684`, `ReleaseHoldBreathState`) stays **ungated** (I3) so a hold in flight when the flag flips still releases.
- **Suppress-off:** in `HandleCombinedMappings`, force suppression when advanced is off. At `:1367` change `var suppressOriginal = entry.SuppressOriginalKey;` → `var suppressOriginal = entry.SuppressOriginalKey || !_advancedModeEnabled;`. This makes every mapping 1:1 (source consumed) whenever Advanced Mode is off, regardless of the saved per-entry value — exactly "disabling Suppress is the gated capability".

### 4.3 UI gray-out (visible but not enable-able)
- **Source of truth for binding:** add `public bool AdvancedModeEnabled { get; set; }` (raising `OnPropertyChanged`) to **`MainViewModel`** (beside `KeyOptions`/`HoldBreathModes` `:53-59`). **Seed it at MainWindow startup** in the same `try` block that reads `[App]` and sets `_inputHook.AdvancedModeEnabled` (`MainWindow.xaml.cs:54-57`) — set both the service flag and `_viewModel.AdvancedModeEnabled` from the one resolved value, so gray-out and gating agree from the first frame. **Refresh after the Settings dialog closes:** in `SettingsButton_Click` (`:211-217`), after `wnd.ShowDialog();`, set `_viewModel.AdvancedModeEnabled = _inputHook.AdvancedModeEnabled;` (modal dialog ⇒ this runs on close, capturing the live-applied value, including a mid-dialog toggle).
- **XAML:** the Auto-Run, Anti-AFK, and Hold-Breath `GroupBox`es bind `IsEnabled="{Binding DataContext.AdvancedModeEnabled, ElementName=RootWindow}"` (per-profile items sit in a `DataTemplate`, so reach the window DataContext via `ElementName=RootWindow`, exactly like `DataContext.KeyOptions` `:357`/`:600`). A disabled `GroupBox` auto-grays its contents = "visible but unusable". For the Combined-Mappings **Suppress** checkbox specifically, bind its `IsEnabled` to the same source so the box can't be *un*-checked while advanced is off (the runtime force in §4.2 is the belt; this is the suspenders). Optionally add a small "Advanced" `TextBlock` badge in each gated header (cheap, uses existing `InfoIconStyle`/`WarningIconStyle`).

### 4.4 Default / migration (flag for confirmation)
Reading `[App] AdvancedMode`:
- **Key present** → honor it (`== "true"`).
- **Key absent (upgrade or fresh):** default `false`, **except** resolve to `true` if any loaded profile already relies on a now-gated capability — i.e. `RightClickHoldBreath.IsEnabled` **OR any `CombinedMappingEntry` with `SuppressOriginalKey == false`** (revised — codex R1 #4; un-suppressed 1→2 mappings are also gated by the runtime force at `HandleCombinedMappings:1367`, so they'd silently collapse to 1→1 if we defaulted off). Because this needs the loaded profiles, resolve it **after `InitializeAsync` has loaded them** (MainViewModel has the profile list there) and then push the one value to both `_inputHook.AdvancedModeEnabled` and `_viewModel.AdvancedModeEnabled`, and persist it so the next launch takes the "key present" branch. This prevents a silent regression where a returning Hold-Breath user's feature goes inert until they find the toggle, while still honoring "disabled initially" for genuinely fresh installs. **DECISION (user, 2026-07-08): enabled-on-upgrade confirmed — implement this rule, not the always-false simplification.**

---

## 5. ViewModel + XAML surface (per-profile features)

`ProfileViewModel` (`:169-176` shows the exact pattern): expose each new setting as a get/set that reads/writes `Model.AutoRun.*` / `Model.AntiAfk.*` and calls `OnProfileChanged()` on set (this is what triggers the per-profile debounced autosave via `ProfileChanged` → MainViewModel). Properties needed:
- Auto-Run: `AutoRunEnabled`, `AutoRunTriggerModifier`, `AutoRunTriggerKey`, `AutoRunSprintEnabled`, `AutoRunSprintKey`, `AutoRunSprintMode`.
- Anti-AFK: `AntiAfkEnabled`, `AntiAfkIntervalMinutes` (slider 1..15).
- Enum option lists on `MainViewModel` beside `HoldBreathModes` (`:59`): `SprintActivationModes`, and a `TriggerModifiers` list (`Control/Alt/Shift/Windows`).

XAML: two new `GroupBox`es in the per-profile panel (`MainWindow.xaml`, after the Hold-Breath group `:574-636`), reusing the same header/`InfoIconStyle`, `ComboBox`+`ComboBoxKeySelectionBehavior` for keys, and the existing `Slider`+`StringFormat` pattern (`:618-635`) for the Anti-AFK interval and (optionally) sprint. Enable-binding per §4.3.

---

## 6. Concurrency & lock map (grep-verify after each phase — I5)

| Lock | Guards | Taken by | Never nests with |
|---|---|---|---|
| `_autoRunLock` (new) | Auto-Run **active flag + injected W/sprint records + snapshotted sprint key** ONLY | keyboard hook (chord/cancel path), `ReleaseAllState` (pool), AdvancedMode setter (UI) | any other subsystem lock — enqueue-only inside; **no SendInput, no nested lock** |
| *(no lock)* `_wPhysicallyDown/_sPhysicallyDown/_sprintPhysicallyDown` | physical key down-state | **keyboard hook thread ONLY** — continuous tracking + the activation-time `GetAsyncKeyState` reseed (both run on the hook thread, since `ActivateAutoRun` is called from the hook), so lock-free like `_altPressed`; nothing off-thread reads or writes them | n/a — resolves the §2.3/§6 consistency point (codex R2 #3): these are NOT under `_autoRunLock` |
| `_holdBreathLock` | unchanged | unchanged | unchanged |
| `_transientTapLock` | unchanged (Anti-AFK uses the injector, not `FireTapKey`, so it does not touch this) | unchanged | unchanged |

- Anti-AFK timer thread only: reads `_activeProfile`, reads `_autoRunActive` under `_autoRunLock`, enqueues on the injector. No new lock.
- The AdvancedMode setter runs on the UI thread and calls release paths that **enqueue only** — safe from any thread (`:1828-1849`).

---

## 7. Tests (`Tests/` — keep the green bar meaningful)
- **IniProfileStore round-trip:** `[AutoRun]` + `[AntiAfk]` serialize/deserialize (mirror existing `IniProfileStoreIntegrationTests`), including `IntervalMinutes` clamp (0→1, 99→15) and enum round-trips.
- **FakeInputHookService** compiles against the new `AdvancedModeEnabled` member (interface change) — verify no other `IInputHookService` impls exist (only real + fake).
- **Auto-Run cancel semantics (pure logic, if extractable):** if the fresh-edge decision can be lifted behind a seam, unit-test: held-through-activation → release doesn't cancel, next fresh press cancels; not-held → first press cancels. If not cheaply extractable without a bigger refactor, cover by the manual matrix in §8 and note the untested surface (do **not** fake a passing test).
- Existing 99 must stay green; no test may *encode* the pre-change behavior of a thing we changed (per the C1/watchdog lesson in `memory.md`).

---

## 8. Phasing, validation gates, and manual matrix

Sequence so each phase is independently buildable/testable and codex-reviewable:

- **P1 — Models + INI + tests.** `AutoRunSettings`, `AntiAfkSettings`, `Profile` wiring, serialize/deserialize, round-trip tests. *Zero behavior change.* Gate: build + tests.
- **P2 — Advanced Mode flag + gating plumbing** (interface, service flag+setter-release, fake, SettingsVM/Window, MainWindow read, MainViewModel bindable, XAML gray-out + Suppress force). Ship this **before** the new features exist at runtime so they are born gated. Gate: build + tests + codex.
- **P3a — Auto-Run runtime, Foreground transport** (`HandleAutoRun`, activate/release, `ReleaseAllState` line, dispatch-chain insert, eager release-on-focus-loss). Gate: build + tests + codex + manual A.
- **P3b — Auto-Run Background transport** (`AutoRunSendMode`, HWND capture/validate, `PostMessage` down/up + `lParam` build, best-effort release on all paths, optional repeat timer). Ship after P3a is clean so the experimental path can't destabilize the reliable one. Gate: build + tests + codex + manual per §11.5.
- **P4 — Anti-AFK runtime** (timer, arm/disarm, tick, WASD sequence). Gate: build + tests + codex + manual B.
- **P5 — VM/XAML for the two features** (the two GroupBoxes, ProfileVM props, option lists). Gate: build + tests + codex + manual C.

**Manual matrix (the no-stuck-key drills — these are the acceptance bar):**
- **A (Auto-Run):** activate not-holding-W → W held by app, character runs; tap W → stops, W not stuck. Hold W physically → activate → release physical W → still running (seamless); tap W → stops. Repeat for S and sprint as the cancel key. Sprint `Hold`: sprint released on cancel. Sprint `Press`: single tap, nothing stuck. Activate → Alt-Tab / profile-switch / Win+L / app-exit mid-run → W+sprint released every time (check with a key-state viewer). Toggle chord off → released. Advanced Mode off mid-run → released.
- **B (Anti-AFK):** enable, interval 1 min, sit idle in-game → one WASD ripple after ~1 min, character ends where it started; touch a key → timer resets, no fire; Alt-Tab away → no WASD in the other app; Auto-Run active + idle → no WASD; exit app mid-ripple → no stuck key.
- **C (Advanced Mode):** off → Auto-Run/Anti-AFK/Hold-Breath groups grayed, cannot enable; Combined-Mappings Suppress cannot be unchecked and every mapping is 1:1 at runtime; toggle on → all usable; toggle off with each running → clean release.

---

## 9. Explicitly out of scope
- Background / not-focused input injection (PostMessage/SendMessage to a specific window) for Anti-AFK — anti-cheat-hostile, game-specific, and incompatible with the injected-filter/no-stuck-key model. Anti-AFK is foreground-only by design (§3.3).
- Making the Auto-Run movement key or Anti-AFK key set configurable (spec is W-run / WASD-jiggle).
- Any change to the hold-breath injector's proven internals beyond optionally renaming `HoldBreathInjection`→`KeyInjection` if we choose to formally share it (§10).

## 10. Injector: SHARE the existing worker (DECISION — user, 2026-07-08)
**Share the existing `_holdBreathInjectionQueue`/worker**, renamed for honesty: `HoldBreathInjection`→`KeyInjection`, `EnqueueHoldBreathInjection`→`EnqueueInjection`, `HoldBreathInjectionLoop`→`InjectionLoop`, `_holdBreathInjectionQueue`→`_injectionQueue`, thread name `"HoldBreathInjector"`→`"KeyInjector"`. The three producers (Hold-Breath, Auto-Run held keys, Anti-AFK sequence item) all enqueue onto the one proven single-consumer FIFO with the one hard-won `IsAddingCompleted` drain path (I7) — strictly safer than duplicating shutdown logic into a second worker, and global FIFO makes "no cross-site interleaving" structural (the `INJECTOR_MIGRATION_Plan.md` conclusion). The rename is mechanical but touches the just-stabilized hold-breath path — do it as its own commit-sized step and re-run codex on the diff. Existing hold-breath log strings (`HoldBreath inject …`) stay as-is or gain a neutral `inject …` prefix; don't let a log rename obscure the debug.log grep recipes in `memory.md`.

---

## 11. Alt-Tab / "keep running while unfocused" — analysis & options (user question, 2026-07-08)

**Question:** can Auto-Run keep holding W in the game after you Alt-Tab away (like AutoHotkey's `ControlSend`/background send), so the character keeps running while you do something else?

### 11.1 Why the current (SendInput) design is foreground-only
`SendInput` injects at the **system input layer**, which routes to the **foreground window**. Auto-Run injects one W-DOWN and holds it. The moment the game loses foreground:
- Most games **stop processing movement** when unfocused (they gate input on focus, or throttle/pause the loop), so the character stops anyway; and
- the held injected W would **leak to whatever window is now focused** (type "w" into your browser/desktop).

So the plan already **releases Auto-Run when the profile deactivates** (via `ReleaseAllState` on foreground loss). For leak-tightness this should be immediate on the foreground-change edge (same lag concern as Anti-AFK §3.3/guard #6 — `ProfileActivationService` deactivates the input profile only after color work; add an eager release-on-foreground-loss for Auto-Run so the held W can't briefly leak during that window). Net: **Alt-Tab stops Auto-Run cleanly. Reliable and safe for every game.**

### 11.2 The AutoHotkey-style alternative: `ControlSend` / `PostMessage(WM_KEYDOWN/UP)` to the game HWND (focus-independent)
This posts key **messages** directly to a target window's message queue, bypassing focus. It is the only user-mode way to deliver "input" to an unfocused window. **But it is fundamentally game-dependent and, for the games Auto-Run is actually for, usually a no-op:**
- **Only reaches games that read the Win32 message queue** (`WM_KEYDOWN`/`WM_CHAR`). Games that read input via **DirectInput, Raw Input (`WM_INPUT`), XInput, or by polling `GetAsyncKeyState`** — which is **most modern action/FPS games** — **ignore posted messages entirely.** (Confirmed by AutoHotkey's own docs — "Some games use DirectInput exclusively… they might ignore all simulated keystrokes" — and the general "PostMessage only works in some applications" result. `PostMessage` does **not** update `GetAsyncKeyState` or feed the raw-input stack.)
- **Specifically for this user's game (Grey Zone Warfare):** GZW is **Unreal Engine 5**, which uses Raw Input / the Enhanced Input system — **`PostMessage`/`ControlSend` will not move the character.** So a background-send mode would do nothing for the very profile that motivates this.
- **Anti-cheat exposure:** posting synthetic messages into another process (and the window/handle probing around it) is a recognized cheat pattern; BattlEye/EAC-class systems flag it. This app deliberately minimizes its footprint (RNG warmup, injected-filter). Background message injection cuts against that.
- **Safety-model mismatch:** posted messages don't carry `INPUT_IGNORE`, aren't seen by our LL hooks, and there's no global key-state to guarantee release through `ReleaseAllState` — a different (weaker) safety story than the whole app is built on. (Upside: no *global* stuck key, since nothing is held in system state — but also no reliable "held" for raw-input games.)

### 11.3 The only thing that *reliably* survives focus loss: a virtual gamepad (out of scope)
Games generally keep reading **controller** input in the background. A virtual Xbox pad (e.g. **ViGEm/ViGEmBus**) holding "left stick forward" would keep many games moving while unfocused — **but** it needs a **kernel driver dependency + install**, the game must map movement to a controller, and it's a substantial new subsystem. Not a fit for this app's scope or its zero-dependency footprint.

### 11.4 Recommendation
**Keep Auto-Run foreground-only (SendInput) with an eager release-on-focus-loss (§11.1).** It is reliable for all games including GZW, leak-safe, and consistent with the app's injection/no-stuck-key model. A `PostMessage` "background send" mode could be added as a **per-profile, Advanced-Mode-gated, explicitly-labeled "experimental (game-dependent; won't work with Unreal/DirectInput games)"** toggle. It would not help GZW and carries anti-cheat risk. **DECISION (user, 2026-07-08): add it anyway** as the opt-in experimental mode below (useful for message-queue games; the user accepts it is a no-op for GZW/UE and validates per-game themselves).

### 11.5 Experimental background-send mode — design (`AutoRunSendMode.Background`)
Selected per-profile via `AutoRunSettings.SendMode` (default `Foreground`). Only Auto-Run gets this — Anti-AFK stays foreground-only (its whole premise is you're idle *at* the game). Ship it clearly labeled **"Experimental — background send. Only works with games that read the Windows message queue; does nothing for Unreal/DirectInput/raw-input games."** with a `WarningIconStyle` badge, and gate the *Background* choice behind Advanced Mode like the rest of Auto-Run.

**Mechanism (differs from foreground only in the transport):**
- **Target HWND capture at activation:** in `ActivateAutoRun`, when `SendMode == Background`, capture `_autoRunTargetHwnd = GetForegroundWindow()` — the game is foreground at chord time, since you press the chord while playing. Validate **and snapshot** its process exe (read from `_activeProfile.NormalizedExecutable` — valid *here* because the game is foreground at chord time — and stored as the run's target exe per §11.6, so later per-post checks never touch `_activeProfile`); if the foreground process isn't the profile's exe, **fall back to Foreground for this run** and debug-log (don't post into the wrong window). Store the HWND for the run's lifetime.
- **Deliver via `PostMessage`, NOT the injector:** `PostMessage(hwnd, WM_KEYDOWN, vk, lParam)` for down and `WM_KEYUP` for up, with a correctly-built `lParam` (scan code from `MapVirtualKey`, extended-key bit for extended VKs, transition/previous-state bits for keyup). **`PostMessage` is asynchronous and non-blocking** — it enqueues to the target's message queue and returns immediately, so unlike `SendInput` it **cannot stall on a foreign hook**; therefore background posts do **not** need the injector and are safe to call directly from the hook/activation thread. (`SendMessage` would block — do not use it.)
- **W / sprint hold:** post `WM_KEYDOWN` for W (and sprint if `Hold`) on activate. For games that move per key *message* rather than per key *state*, a held key needs an autorepeat stream: an optional `_autoRunRepeatTimer` re-posts `WM_KEYDOWN` (previous-state bit set) every ~40 ms while active. **Start without the repeat timer** (down-once); add it only if per-game testing shows the game needs the stream. Post `WM_KEYUP` on release.

**Safety — a DELIBERATELY WEAKER guarantee than the rest of the app (codex R4 — do not overclaim):**
- **OS-level: always safe.** `PostMessage` writes only to the *target window's message queue*, never global system input state, so Background mode can **never** create a system-wide stuck key. Every other feature's hard no-stuck-key guarantee is about OS-level state; that guarantee is *preserved* — there is nothing to release at the OS level.
- **In-game: a weaker, best-effort guarantee.** Because the "held W" lives inside the *game's own* key-state tracking, a posted `WM_KEYUP` only clears it if the **same window is still alive and pumping messages** when the UP arrives. If the game thread hangs, stops pumping, destroys/swaps its HWND, or the handle is reused, the release UP may not reach the consumer that accepted the DOWN → the **game can keep moving ("stuck run")** even though this app holds nothing. **App crash/kill after a `WM_KEYDOWN` but before any `WM_KEYUP` is another such case** (codex R6 #3) — the process dies with no release path at all, so the game keeps its internal held state (OS input stays clean — nothing is stuck system-wide) until one of the escapes below. **Do not claim otherwise, and do not claim a physical W tap compensates** — a physical tap while the game is *unfocused* delivers no `WM_KEYUP` to the game (physical input goes to the focused window). The realistic escapes are: **(a) the chord toggle-off, which posts the UP while the game is still alive/pumping (works in the common case); (b) refocusing the game and tapping W (a real focused keyup clears the game's state); (c) the game closing.** This residual is the price of focus-independent input and is why the mode is **experimental and Advanced-gated**; surface it in the UI tooltip/warning, not just here.
- **Per-post HWND re-validation (required — codex R4 #2):** before *every* `WM_KEYDOWN`/`WM_KEYUP`/repeat post, re-check `IsWindow(_autoRunTargetHwnd)` **and** `GetWindowThreadProcessId` still resolves to the **snapshotted target exe** (from §11.6 — NOT `_activeProfile`, which is `null` once you alt-tab away). On mismatch (window destroyed, or the handle reused by a different-process window), **stop the background run and clear state immediately** — do not post into a wrong or dead window. This closes the *dead-handle* and *cross-process reused-handle* leaks. It does **NOT** close a **same-process window swap** (codex R6 #2): if the game destroys its input window and creates a new one in the same process, `IsWindow`+PID still pass on the stale handle, so posts keep going to a window that may no longer consume them — a remaining Background-mode residual, not a fully-closed hole.
- **Hooks unaffected:** our LL hooks do **not** see posted messages (not system-injected), so physical W/S/sprint still flow through `KeyboardCallback` and trigger the same fresh-down-edge cancel. No `INPUT_IGNORE` needed; no self-cancel. Cancel *decision* is as reliable as Foreground; only the *delivery* of the resulting UP to the game is best-effort per above.
- **Records under `_autoRunLock`** (`_autoRunTargetHwnd` + held keys) exactly like the Foreground records (I2/I3), so every release path knows what to un-post. Release is best-effort-posted on every path (cancel, `ReleaseAutoRunState`, deactivate, `Stop`, Advanced-off) subject to the re-validation above.
- **Eager release on focus loss is NOT applied in Background mode** (keeping posting while unfocused is the whole point); Foreground mode keeps the §11.1 eager release.

### 11.6 Background run is DECOUPLED from the profile lifecycle (codex-critical — user Q, 2026-07-08)
A background run must **outlive profile deactivation**, or the feature is inert: when you alt-tab away, `ProfileActivationService` deactivates the game profile (after its color work), which calls `ReleaseAllState` — and if that released the run, the character would stop the instant you tab out. So a background run is **not** owned by `_activeProfile`; it is a **self-contained snapshot** taken at activation and released only by the explicit paths below.

**Snapshot at background activation (so nothing depends on `_activeProfile` afterward):** trigger chord (modifier + key), sprint key + mode, target `_autoRunTargetHwnd`, and target exe name (for the focus check). Store under `_autoRunLock`. Only **one** auto-run may be active at a time; a fresh chord while one is active toggles the existing one off first (matched via its own snapshot, not the live profile).

**Release matrix for a BACKGROUND run — exactly these, nothing else:**
| Trigger | Release? | Notes |
|---|---|---|
| Chord toggle-off (matched via snapshot) | **YES** | works globally even when `_activeProfile == null`; suppress the chord key |
| Physical W/S/sprint while game **focused** (foreground == snapshot exe) | **YES** | user is back in-game taking control |
| Physical W/S/sprint while game **unfocused** | **NO** | user's choice (2026-07-08): chord-only while unfocused — stray W/S in another app must not kill the run |
| Per-post `IsWindow`/PID re-validation fails | **YES** | window died/reused; stop + clear (can't post a clean UP) |
| `Stop()` (app exit) | **YES** | post final UP, then drain |
| `OnSessionSwitch` (lock/logoff) | **YES** | desktop going away — safety |
| Advanced-Mode → off | **YES** | gate closed |
| `ActivateProfile`/`DeactivateProfile` (alt-tab, profile switch) | **NO** | the decoupling — a background run ignores ordinary profile churn |
| Watchdog hook reinstall | **NO** | background uses `PostMessage`, not the LL hooks |

**Implementation consequences:**
- `ReleaseAllState` must **skip a background run** (it is reached by profile-switch AND watchdog-reinstall, which must NOT kill it). Add an explicit `ReleaseAutoRunState(includeBackground: true)` call to the three **hard-teardown** sites only — `Stop()`, `OnSessionSwitch`, and the Advanced-off setter — so those still release it. `ReleaseAllState`'s existing call becomes `includeBackground: false`. (Foreground runs are always released by `ReleaseAllState`, unchanged.)
- The **cancel decision** (`HandleAutoRun`) for a background run reads the snapshot, not `_activeProfile`; the W/S/sprint cancel is gated on `ForegroundMatchesActiveProfile()`-against-the-**snapshot-exe** (so it fires only when the game is focused). The `GetForegroundWindow` cost is paid only on W/S/sprint edges **while a background run is active** — never on the foreground hot path.
- This lifecycle applies to Background only. **Foreground mode is unchanged**: profile-tied, eager-released on focus loss (§11.1), no snapshot decoupling. Keeping the two lifecycles cleanly separated is the safest shape and the reason P3b lands after P3a is proven.

> **Honest bottom line for the UI + the user:** Background mode is the only way to run while alt-tabbed, but it (1) does nothing for Unreal/DirectInput/raw-input games incl. GZW, (2) carries anti-cheat risk, and (3) trades the app's hard in-game no-stuck-key guarantee for a best-effort one. It is OS-safe always; in-game it relies on the game still consuming the posted key-up. Ship it labeled exactly that blunt.

**Concurrency:** background posts touch only `_autoRunLock`-guarded records + `PostMessage` (non-blocking) — no injector, no new lock, no I5 concern. The optional repeat timer is a single `System.Threading.Timer` (armed on background-activate, disposed on release), guarded like the other Auto-Run state.

**Validation add-ons (§8):** a manual test that Background mode moves the character in a known message-queue app/game, does nothing in GZW (expected), leaks nothing to other windows, and always posts the `WM_KEYUP` on cancel/switch/exit (verify the game doesn't keep running after cancel).
