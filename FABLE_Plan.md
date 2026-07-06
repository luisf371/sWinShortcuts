# FABLE_Plan.md — Implementation Plan for the `fixupv2` Code‑Review Findings

> Turns the `FABLE.md` review (xhigh, 2026‑07‑05) into a **verified, regression‑safe** fix plan.
> Every finding below was **re‑verified against current `HEAD`** (not FABLE's line numbers), because the branch has had fixing commits *since* the review (color‑plan dedup + sequential foreground processing, per‑display color + EDID naming, combined‑override thread‑safety, `ReleaseRightClickOverrides` de‑LINQ). Authored 2026‑07‑06.

---

## 0. Purpose & how to use this plan

- **Prime directive (from the request):** *fixing a bug must not regress the feature or the intent of the feature.* Every fix entry has an explicit **Intent** (what the feature must keep doing) and a **Regression guard** (why the fix preserves it).
- **Scope:** correctness/data‑loss/crash first; cleanup/perf last. `net8.0-windows`, WPF + `IHostedService`. Test baseline: `Tests/Tests.csproj`, **65 passing**.
- **Verification tooling note:** Serena's C# semantic server is unavailable locally (needs .NET 10; machine has .NET 9), so verification was Read/Grep static tracing. The app itself was **not executed** by the reviewers; manual repro steps are given where runtime observation is required.

### Status legend
| Tag | Meaning |
|---|---|
| **REPRO** | Still reproduces at HEAD — fix required |
| **PARTIAL** | Partially addressed by a prior commit — residual work only |
| **UNREACHABLE** | Real mechanism but not reachable from any current caller — optional guard |

### Build / verify commands
```powershell
dotnet build .\sWinShortcuts.csproj -c Release --no-incremental
dotnet test  .\Tests\Tests.csproj   -c Release
```

---

## 1. Prime directive — no‑regression governance (applies to every change)

1. **Preserve built‑in profiles.** `Windows` and `Color Settings` profiles are identified *by name* (`Profile.IsWindowsProfile`/`IsColorProfile` derive from `Name`) and are special‑cased in ~28 places. No fix may rename them, route them through `DetermineProfilePath`, or make them user‑deletable/renamable. UI already blocks renaming them via `CanModifyProfile`.
2. **Preserve upgrade data.** This branch *replaces* `master`. Any load‑path change must keep loading every legacy INI shape faithfully (see §6.B migration matrix) — never drop or silently rewrite user data.
3. **Respect the real threading model** (§2). Do not "fix" a race that is currently serialized by the UI thread in a way that breaks when it is genuinely cross‑thread, and do not assume UI affinity where continuations run on the pool.
4. **Keep the intended side effects that look like bugs but aren't.** Two traps encountered during verification:
   - The color slider **revert** path deliberately calls `Apply(display, {IsEnabled=false, defaults})` and *wants* the hardware write. → the calibration‑wipe fix (C1) must live in the **orchestrator's plan‑diff**, not as an `IsEnabled` short‑circuit inside `Apply`.
   - `_rightButtonPressed = true` is read by CombinedMappings' `RightClickOnly` gate. → the hold‑breath reorder (H6) must not move that assignment.
5. **Land safety nets before behavior changes** (§3 Phase 0), so that if a later fix is imperfect the app degrades to *logged/visible error*, not *crash* or *dead session*.
6. **Every fix ships with a test or a documented manual repro.** Pure decision‑logic is extracted into testable helpers where the surrounding code is hook/timer/GDI‑bound and not unit‑testable.

---

## 2. Architectural invariants every fix must respect (verified at HEAD)

- **LL hooks run on the WPF UI thread** (safe‑by‑accident): `App.OnStartup` → `await _host.StartAsync()` → `ProfileActivationService.StartAsync` → `_inputHookService.Start()` runs synchronously because `ProfileManager.InitializeAsync` never yields. So keyboard/mouse hook callbacks and the UI‑thread collections they touch are serialized **today**. Fixes that rely on this must say so; do not make profile load truly async without revisiting the hook‑vs‑UI races.
- **The color/foreground worker runs on a pool thread**: `ProfileActivationService.StartAsync` does `Task.Run(ProcessForegroundChangesAsync)`. `ActivateProfile`/`DeactivateProfile` therefore also run on the pool. This is the genuine cross‑thread surface (C3, and the `_activeCombinedOverrides` lock is real and necessary).
- **Timer + `ThreadPool.QueueUserWorkItem` callbacks run on pool threads**: `MouseButtonState.HoldTimer` (tap/hold), `_holdBreathTimer`, `FireTapKey`, Toggle hold‑breath, `LaunchProcess`. These are where H3/H5/H6 live.
- **`NvidiaColorControlService` is a singleton and `Apply` is fully serialized by `lock (_sync)`** — worker‑`Apply` and UI‑slider‑`Apply` cannot corrupt each other at the hardware call. The unprotected surfaces are `ColorSettings._displayProfiles` (plain `Dictionary`, C3) and the cross‑class `_lastAppliedColorPlan` staleness (C5).
- **Profile file identity = `SourcePath`** (set on load and after each save; empty until a new profile's first save). `Profile.Name` is now `{ get; set; }` (master had `{ get; init; }`) — that mutability is what enabled the rename‑clobber (M2/P4).

---

## 3. Master implementation sequence (phased, dependency‑ordered)

Land phases in order; within a phase, items are independent unless "bundle" is noted. **Bundled items must land in the same PR/commit** because they form one coherent behavior.

| Phase | Theme | Items | Why here |
|---|---|---|---|
| **0** | Safety nets | **C2** (worker try/catch + inject logger), **M3** (non‑fatal save + `DialogService` marshal), **C6** (NvAPI log braces) | Convert crash/dead‑session into logged/visible error before touching behavior; unlock diagnostics. |
| **1** | Concurrency correctness | **C3** (ColorSettings lock/snapshot), **H4** (hook‑install leak), **H5** (dispose guard) | Remove the actual throw sources that Phase 0 now merely survives. |
| **2** | Silent data loss / activation | **M1** (per‑profile autosave + flush); **bundle:** identity/rename **P4+P3+M2** (+ `LastProfile` refresh); **bundle:** migration **P1+P2+P8**; **S1** (dotted‑exe activation) | The "my settings vanished / app never triggers" class. |
| **3** | Color hardware correctness | **bundle:** **C1+C5** (diff‑aware apply + resume re‑apply); **C4** (DVC broadcast), **C8** (CreateDC fail‑closed); **C7** (hot‑plug list) | Requires C2/C3/C6 landed; C1 and C5 are atomic. |
| **4** | Input hook behavior | **H1** (launcher latch), **bundle:** **H2+H7‑fastpath** (release‑by‑held‑key), **H3** (elapsed‑time tap/hold), **H6** (hold‑breath reorder) | Requires H4/H5 lifecycle fixes landed. |
| **5** | OS integration | **bundle:** **S2+S3** (schtasks quote/delete), **S4** (window restore), **bundle:** **S5+S6+S7** (shutdown: SessionEnding + sync OnExit + logger flush) | Self‑contained; shutdown trio is one sequence. |
| **6** | Cleanup / low | **C9** (slider debounce), **P5** (color‑serialize dedup), **P6** (INI key quirks), **H7** residual `LogDebug` guards + allocs, **H8** (`SendKey` dead param), **P7** (IniDocument guard), dead‑code sweep | Correctness always outranks these. |

---

## 4. Detailed fixes — Subsystem A: Color / display pipeline

Files: `Services/ProfileActivationService.cs`, `Services/NvidiaColorControlService.cs`, `Services/ColorPlan.cs`, `Models/ColorSettings.cs`, `Services/DisplayService.cs`, `Services/IDisplayService.cs`, `ViewModels/ColorSettingsViewModel.cs`, `ViewModels/DisplayColorSettingsViewModel.cs`.

### C2 — Worker loop has no per‑item try/catch → one throw kills all switching *(REPRO, Phase 0, linchpin)*
- **File:** `ProfileActivationService.cs:107‑113` (`ProcessForegroundChangesAsync`).
- **Intent:** a single bad foreground event must not permanently disable profile switching + color.
- **Fix:** inject `ILoggerService` into the ctor; wrap the loop body; keep cancellation clean (the OCE originates in `ReadAllAsync`, *outside* the try):
  ```csharp
  await foreach (var processName in _foregroundChanges.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
  {
      try { ProcessForegroundChange(processName); }
      catch (OperationCanceledException) { throw; }          // let shutdown cancel cleanly
      catch (Exception ex) { _logger.Log($"[Color] ProcessForegroundChange failed: {ex}"); }
  }
  ```
- **Regression guard:** OCE re‑throw keeps `StopAsync` shutdown identical (covered by `ProfileActivationServiceShutdownTests`). Behavior otherwise unchanged when nothing throws.
- **Test impact:** ctor gains a parameter → update the **2** direct constructions (`ProfileActivationServiceDeduplicationTests.cs:21`, `ProfileActivationServiceShutdownTests.cs:21`) to pass `NullLoggerService`. Add a test: a throwing `IColorControlService` on event #1 still lets event #2 route through (`FakeInputHookService` sees the later Activate/Deactivate).

### C3 — Worker reads the live `DisplayProfiles` dictionary off‑thread *(REPRO, Phase 1)*
- **Files:** `ProfileActivationService.cs:173‑195` (`BuildDisplayColorPlan` `TryGetValue`), `Models/ColorSettings.cs:8,16,31` (plain `Dictionary`, live‑instance getter, UI‑thread `GetOrCreateProfile` write).
- **Intent:** per‑display settings resolve correctly; no crash from a `TryGetValue` racing an `Add`‑resize (which feeds C2).
- **Fix:** add an internal lock + snapshot accessor to `ColorSettings`; guard `GetOrCreateProfile`/`SetProfile`/`ClearProfiles`; snapshot once in `BuildColorPlan` and pass the copy into `BuildDisplayColorPlan`:
  ```csharp
  // ColorSettings
  private readonly object _sync = new();
  public IReadOnlyDictionary<string, DisplayColorProfile> SnapshotProfiles()
  { lock (_sync) return new Dictionary<string, DisplayColorProfile>(_displayProfiles, StringComparer.OrdinalIgnoreCase); }
  ```
- **Regression guard:** snapshot is a faithful copy → resolution unchanged; `DisplayColorProfile` value objects are still shared (a torn slider field is benign — no throw, corrected next foreground change). Lock is uncontended in the common case. Keep `DisplayProfiles` for UI/serialization enumeration but route all *mutation* through the lock.

### C1 — Startup wipes monitor calibration on every launch *(REPRO, Phase 3, bundle with C5)*
- **Files:** `ProfileActivationService.cs:32,124‑128,197‑217`; confirmed `NvidiaColorControlService.Apply` (`:33‑53`) has **no** `IsEnabled` check and, with disabled defaults (50/50/1.0/50), writes an **identity gamma ramp** (overwrites ICC/Night Light/f.lux) and sets **DVC level 0** (resets NVCP vibrance). On cold start `_lastAppliedColorPlan == Empty` ≠ the all‑disabled plan → `ApplyColorPlan` runs once per launch and wipes calibration.
- **Intent:** touch gamma/vibrance **only** when a color profile is actively enabled, and **restore neutral** when a user turns a previously‑active profile off. Never touch hardware on a cold launch where color was never enabled.
- **Fix (orchestration‑level diff — NOT inside `Apply`):** make `ApplyColorPlan` compare current vs previous plan and skip a display that is *disabled‑now AND (disabled‑or‑absent)‑before*:
  ```csharp
  if (!_lastAppliedColorPlan.Equals(plan))
  { ApplyColorPlan(plan, _lastAppliedColorPlan, displays); _lastAppliedColorPlan = plan; }
  ...
  private void ApplyColorPlan(ColorPlan plan, ColorPlan previous, IReadOnlyList<DisplayInfo> displays)
  {
      foreach (var dp in plan.Displays)
      {
          var prior = FindDisplayPlan(previous, dp.DisplayId);          // linear scan, mirrors FindDisplay
          if (!dp.IsEnabled && (prior is null || !prior.IsEnabled)) continue;  // never‑applied → leave hardware alone
          var display = FindDisplay(dp.DisplayId, displays);
          if (display is null) continue;
          _colorControlService.Apply(display, /* existing projection */);
      }
  }
  ```
- **Regression guard:** `Apply` is unchanged → the live‑slider revert path (which calls `Apply` directly with `IsEnabled=false`+defaults) still writes as intended. Enabled cold‑start still applies (enabled vs `Empty` → applies). Enabled→disabled transition still restores (disabled‑now + enabled‑before → applies defaults). Bonus: a mixed setup preserves the calibration of never‑configured displays.
- **Test impact:** `ProfileActivationServiceDeduplicationTests` currently asserts `AppliedProfiles.Count == 1` after a disabled‑color startup — **that test encodes the bug**; change to `== 0`. Add: (1) disabled cold‑start → 0 applies; (2) enable global → 1 enabled apply; (3) disable → 1 restore apply with default values.

### C5 — Colors stay wrong after sleep/resume until you switch profiles *(REPRO, Phase 3, bundle with C1)*
- **Files:** `ProfileActivationService.cs:32,124‑128`; the existing `SystemEvents.DisplaySettingsChanged` handler at `NvidiaColorControlService.cs:302‑308` only clears `_handleCache` — it **cannot** reach `_lastAppliedColorPlan` and does not re‑apply; there is **no** `PowerModeChanged` subscriber anywhere. So after an OS/driver gamma+DVC reset, the next foreground change rebuilds a byte‑identical plan, `Equals` is true, re‑apply is skipped.
- **Intent:** on display‑settings‑change or power‑resume, re‑assert the currently intended plan.
- **Fix:** subscribe in `StartAsync` / unsubscribe in `StopAsync` to `SystemEvents.DisplaySettingsChanged` and `SystemEvents.PowerModeChanged` (act on `Resume`); keep all `_lastAppliedColorPlan` access on the worker via a force flag + a wake write:
  ```csharp
  private int _forceReapply;
  private volatile string? _lastProcessName;                 // updated in OnForegroundChanged on newest arrival — NOT at end of processing (see §14.2)
  private void OnReapplyRequested()
  { Interlocked.Exchange(ref _forceReapply, 1); _foregroundChanges.Writer.TryWrite(_lastProcessName); }
  // in ProcessForegroundChange:
  var force = Interlocked.Exchange(ref _forceReapply, 0) == 1;
  if (force || !_lastAppliedColorPlan.Equals(plan)) { ApplyColorPlan(plan, _lastAppliedColorPlan, displays); _lastAppliedColorPlan = plan; }
  ```
- **Regression guard:** no infinite loop — `SetDeviceGammaRamp`/`NvAPI_DVC_SetLevel` don't raise `DisplaySettingsChanged`; `Resume` fires once. `_lastAppliedColorPlan` stays worker‑owned (handler only sets an `Interlocked` flag + thread‑safe channel write). **Because the forced re‑apply routes through the C1 diff**, a resume while color is disabled rebuilds an all‑disabled plan → all displays skipped → **no calibration wipe**. This is why C1 and C5 are atomic; never ship C5 without C1.

### C4 — Per‑display vibrance broadcast to the wrong monitor *(REPRO, Phase 3)*
- **File:** `NvidiaColorControlService.cs:126‑179` (`TryApplyNvapiDvc`): when `FindDisplayHandle` returns `Zero`, the fallback loop `success |= ApplyDvc(handle, profile)` writes *this* display's level to **every** NV handle.
- **Intent:** per‑display digital vibrance affects only its own monitor.
- **Fix:** enumerate NV handles once; if exactly one exists, apply to it (unambiguous single‑GPU‑display case, where name matching sometimes legitimately fails); if multiple exist and no name match was found, **log and skip** instead of broadcasting.
- **Regression guard:** the correctly‑mapped path is untouched; single‑monitor rigs keep working via the `count == 1` branch; only the multi‑monitor broadcast is removed. C1 already reduces how often this triggers (disabled displays no longer applied).

### C8 — Gamma applied to the primary/desktop DC on `CreateDC` failure *(REPRO, Phase 3)*
- **File:** `NvidiaColorControlService.cs:85‑124` (`TryApplyGammaRampToDevice`): a named‑device `CreateDC` failure falls back to `GetDC(NULL)` (whole‑desktop/primary DC) and applies the *target's* ramp there, then returns `true`.
- **Intent:** a per‑display gamma apply must affect only the named display and **fail closed** if that display's DC can't be created.
- **Fix:** only use `GetDC(NULL)` when **no** device name was supplied; when a named device's `CreateDC` fails, log and `return false`.
- **Regression guard:** the intentional whole‑desktop path (unnamed `deviceName`, used by the otherwise‑unused `TryApplyGammaRamp(profile)` overload) is retained; only the erroneous "named device failed → hit primary" path is removed.

### C7 — Hot‑plugged monitors never appear in color settings *(REPRO, Phase 3)*
- **Files:** `ViewModels/ColorSettingsViewModel.cs:37` snapshots displays once in the ctor; `IDisplayService` has no change event (though `DisplayService` *does* invalidate its own cache on `DisplaySettingsChanged`).
- **Fix:** add `event EventHandler? DisplaysChanged;` to `IDisplayService`; raise it in `DisplayService.OnDisplaySettingsChanged` after cache‑invalidate; `ColorSettingsViewModel` subscribes and rebuilds `DisplayViewModels` from `GetDisplays()`, reusing existing per‑display profiles via `GetOrCreateProfile`.
- **Regression guard (important):** `DisplayService` is a singleton that outlives transient VMs → the VM **must unsubscribe** on teardown or handlers leak. Marshal the rebuild through the `Dispatcher` before touching the `ObservableCollection`. Only rebuild the list; don't disturb the master enable toggle. Shares the `DisplaySettingsChanged` source with C5 (distinct consumer).

### C6 — NvAPI failure logs print literal `{status}` *(REPRO, Phase 0)*
- **File:** `NvidiaColorControlService.cs` — doubled braces inside `$"…"` at **11 lines**: `165, 189, 228, 268, 276, 402, 407, 412, 431, 436, 446`. Replace each `{{x}}` with `{x}`. (Correctly single‑braced logs at 42‑43, 169, 184, 193, 255, 283, 290 must be left alone.)
- **Regression guard:** log text only; logging is opt‑in (`ILoggerService.IsEnabled`, default off). Do this before field‑testing C4/C8 so their diagnostics are legible.

### C9 — Sliders drive synchronous GDI+NvAPI per tick *(REPRO, Phase 6, low)*
- **Files:** `DisplayColorSettingsViewModel.cs:98‑156,220‑236`; `MainWindow.xaml:790,820,850,880` bind `UpdateSourceTrigger=PropertyChanged`. Live path fires only for the **global color profile** (`_allowLiveUpdates = IsColorProfile`).
- **Fix:** debounce the hardware `Apply` (~30‑50 ms `DispatcherTimer`, or apply on `Thumb.DragCompleted`); keep the `_profile.*` field writes immediate so persistence stays correct; always apply the trailing value.

---

## 5. Detailed fixes — Subsystem B: Profile persistence & upgrade migration

Files: `Configuration/IniProfileStore.cs`, `Services/ProfileManager.cs`, `Models/{Profile,CapsLockSettings,CombinedMappingsSettings,AltMouseSettings,ColorSettings}.cs`, `Utilities/{IniExtensions,IniDocument,KeySerializer,KeyInteropUtilities}.cs`. Legacy `master` formats confirmed from git (merge‑base `8c44d89`).

### P1 — Upgrade drops old right‑mouse mappings *(REPRO, Phase 2, migration bundle)*
- **Files:** `IniProfileStore.cs:261‑287` (`DeserializeCombinedMappings` reads only `[KeyMappings]`/`[KeyMappingsOverrides]`, no legacy fallback); `:388‑396` (fresh `IniDocument` on save erases old sections). Master wrote `[RightMouse] Enabled/SuppressOriginal` + `[RightMouseOverrides] Src=Tgt` with a single **global** suppress flag.
- **Semantic equivalence (proven by reading both engines):** master's `HandleRightMouseOverride` (fires only while RMB held; returns global `SuppressOriginal`) == branch's `HandleCombinedMappings` with `entry.RightClickOnly=true`. So each legacy row `Src=Tgt` maps to `CombinedMappingEntry { SourceKey=Src, TargetKey=Tgt, SuppressOriginalKey=<global>, RightClickOnly=true }`, with `CombinedMappings.IsEnabled = <RightMouse.Enabled>`.
- **Fix:** prefer the new format, fall back to legacy (mirrors the existing AltMouse fallback at `:223‑258`). Discriminate with `document.GetSection("KeyMappings").Any()` (a branch profile always writes `[KeyMappings] Enabled=…`; a master INI never has it):
  ```csharp
  var overrides = document.GetSection("KeyMappingsOverrides");
  if (document.GetSection("KeyMappings").Any() || overrides.Any()) { /* existing new‑format parse */ return; }
  var legacy = document.GetSection("RightMouseOverrides");
  if (document.GetSection("RightMouse").Any() || legacy.Any()) {
      settings.IsEnabled = document.GetBoolean("RightMouse", "Enabled", settings.IsEnabled);
      var suppress = document.GetBoolean("RightMouse", "SuppressOriginal", true);
      foreach (var pair in legacy) { /* KeySerializer.Deserialize both; add CombinedMappingEntry{RightClickOnly=true, SuppressOriginalKey=suppress} */ }
  }
  ```
- **Regression guard:** new‑format parse is byte‑identical to today's and tried first; legacy rows load only when new sections are absent; after migration the next save writes the modern shape and the stale sections vanish (data preserved). Built‑ins never call this method.

### P2 — Upgrade resets CapsLock "Hold" mode *(REPRO, Phase 2, migration bundle)*
- **Files:** `IniProfileStore.cs:297‑302` (`DeserializeCapsLock`), `IniExtensions.cs:66` (`Enum.TryParse` fails on removed member), `CapsLockSettings.cs`. Master's `MomentaryShift = 2` was renamed to `Hold = 2` (same underlying value); old INIs store the **name** → parse fails → falls back to `Normal` → persisted, losing the setting. Affects **both** `Windows.ini` and profile INIs.
- **Fix:** targeted alias in `DeserializeCapsLock`:
  ```csharp
  var raw = document.GetString("CapsLock", "Mode", string.Empty);
  settings.Mode = string.Equals(raw, "MomentaryShift", StringComparison.OrdinalIgnoreCase)
      ? CapsLockMode.Hold : document.GetEnum("CapsLock", "Mode", settings.Mode);
  ```
- **Regression guard:** only the exact legacy string is intercepted; all current values still flow through `GetEnum`; after migration next save writes `Mode=Hold` and round‑trips.

### P8 — *(NEW, not in FABLE)* Upgrade drops 4th/5th mouse‑button Alt‑mouse bindings *(REPRO, Phase 2, migration bundle)*
- **Finding:** the AltMouse legacy fallback (`IniProfileStore.cs:223‑258`) reads only `Left/Right/Middle`, but master also wrote `[AltMouse.Button4]`/`[AltMouse.Button5]`; the branch `MouseButton` enum has `XButton1=4`/`XButton2=5`. Upgrading users lose those bindings.
- **Fix:** extend the AltMouse fallback to also read `AltMouse.Button4`→`XButton1` and `AltMouse.Button5`→`XButton2` (same Tap/Hold parse).
- **Regression guard:** additive; only fills previously‑dropped buttons; no effect on Left/Right/Middle or new‑format profiles.

### P4 — Rename → store side: stale `SourcePath` lets a new profile clobber a renamed file *(REPRO, Phase 2, identity bundle)*
- **Files:** `IniProfileStore.cs:491‑500` (`DetermineProfilePath` returns stale `SourcePath` after rename, and the `SourcePath`‑empty branch has **no** collision check), `:62‑83` (Save targets `DetermineProfilePath` then sets `SourcePath`).
- **Intent:** rename keeps exactly one profile with the new name + data; no two profiles ever bound to one file.
- **Fix (minimal, regression‑safe — makes rename data‑safe even without a file move):** add collision avoidance to the **`SourcePath`‑empty branch only**:
  ```csharp
  if (!string.IsNullOrWhiteSpace(profile.SourcePath)) return profile.SourcePath;
  var sanitized = SanitizeFileName(profile.Name);
  var candidate = Path.Combine(_profilesDirectory, $"{sanitized}.ini");
  for (var n = 2; File.Exists(candidate); n++)
      candidate = Path.Combine(_profilesDirectory, $"{sanitized} ({n}).ini");   // never reuse an existing file
  return candidate;
  ```
  Optionally add an explicit `IProfileStore.RenameProfileAsync(profile, newName)` that `File.Move`s `SourcePath`→new sanitized path (on‑disk tidiness; not required for correctness).
- **Regression guard:** the loop runs **only** when `SourcePath` is empty (first save of a genuinely new profile) → existing profiles keep writing to their own files; `DeleteProfileAsync` uses `DetermineProfilePath` only when `SourcePath` is empty → unsaved profile resolves to a non‑existent path → safe no‑op; built‑ins use fixed paths and never reach here.

### P3 — Duplicate profile names on disk brick every save *(REPRO, Phase 2, identity bundle)*
- **Files:** `ProfileManager.cs:40‑42` (load never dedups names — names come from INI *content*), `:157‑161` (save throws on any duplicate name held by a different instance). Two files with `Name=Foo` → every save on either throws (→ error dialog / M3 crash). Creatable by the P4 clobber.
- **Fix:** dedup **names** on load, keeping each profile on its own file; deterministic order (`SourcePath`), skip reserved built‑in names, suffix `" (2)"`, `" (3)"`… Runs inside the `_gate` already held by `InitializeAsync`.
- **Regression guard:** no profiles dropped/merged; only a colliding display name changes (and only in the disk‑duplicate edge case that is unusable today). `SourcePath` ordering keeps suffixes stable across restarts. Because file identity is `SourcePath` (P4), the renamed in‑memory name still saves to its own `.ini`.

### M2 — Rename → VM/manager side *(REPRO, Phase 2, identity bundle; see also §7)*
- **Files:** `MainViewModel.cs:266` (`selected.Name = newName` only), `ProfileViewModel.cs:82‑87` (`Name` setter → `OnProfileChanged` → autosave against the stale path). No `Rename` method exists.
- **Fix:** add `IProfileManager.RenameProfileAsync(Profile, string newName, CancellationToken)` — under `_gate`: reject built‑ins, reject duplicate name excluding self (reuse the `:157‑161` check), keep the **same** `Profile` instance in `_profiles` (preserves selection + M1's per‑VM debounce key). `MainViewModel.ModifyProfile` becomes async: on an actual name change, call `RenameProfileAsync`, then `ProfileViewModel.RefreshNameFromModel()` (updates the displayed `Name` **without** re‑firing `OnProfileChanged`/autosave); executable edits keep normal autosave.
- **Regression guard:** built‑ins stay unrenamable (both `CanModifyProfile` and the manager guard); same instance preserves selection/keying. **Also:** `MainWindow` persists `LastProfile` by `Name` (`:396`) and only refreshes on selection change → refresh `_lastProfileName` on rename (or key it on the stable path) so it doesn't go stale.

### P5 — Color block serialization duplicated + version‑less `ColorDisplays` *(REPRO, Phase 6, low)*
- **Files:** `IniProfileStore.cs:409‑423` and `:434‑447` (verbatim duplicate write); `:312‑359` (reader sniffs by field count, `>=5` = new, no version key → a future 6th field is silently dropped).
- **Fix:** (a) extract `WriteColorSection(document, color)` used by both `SerializeProfile` and `SerializeColorProfile` (byte‑identical output → guaranteed round‑trip). (b) *Specify only:* reserve a `[ColorDisplays] Version` key so a future field can be added without silent loss; don't change the emitted format now.
- **Regression guard:** (a) is a pure refactor with identical bytes; existing 4‑ and 5‑field files round‑trip exactly as today.

### P6 — Hand‑edited INI key quirks *(REPRO, Phase 6, low)*
- **Files:** `KeySerializer.cs:27` (`TapKey=5` → `Enum.TryParse` numeric → `Key.Clear`, shadowing the `D{n}` branch), `KeyInteropUtilities.cs:14‑16` (`Key.None` returned instead of `null`). App never writes these itself.
- **Fix:** short‑circuit bare single digits to `D{n}` before the generic parse; return `null` when `KeyInterop.KeyFromVirtualKey` yields `Key.None`.
- **Regression guard:** app‑written values unchanged; `Key.None`→`null` matches how callers already ignore it; consistent with `Deserialize("None")→null`.

### P7 — IniDocument bare‑name / headerless `[Default]` *(UNREACHABLE, Phase 6, optional)*
- All callers pass absolute `%APPDATA%` paths + literal sections; not reachable. Optional cheap guard: `var dir = Path.GetDirectoryName(path); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);`. Don't over‑invest.

---

## 6. Detailed fixes — Subsystem C: Autosave / rename / crash (MVVM)

Files: `ViewModels/MainViewModel.cs`, `ViewModels/ProfileViewModel.cs`, `Services/DialogService.cs`, `App.xaml.cs`, `MainWindow.xaml.cs`.

### M1 — Autosave silently drops edits + no exit flush *(REPRO, Phase 2)*
- **Files:** `MainViewModel.cs:362‑396` (single shared `_saveCts` debounces **all** profiles; editing B cancels A's pending save; 500 ms; no flush anywhere), `App.xaml.cs:51‑64` (`OnExit` only `StopAsync`; `MainViewModel` is a plain singleton, never flushed).
- **Intent:** coalesce rapid edits *to one profile*; never lose another profile's pending edit; flush on exit.
- **Fix:** debounce **per `ProfileViewModel` instance** (stable across rename) with a dirty‑set under one lock, plus an awaitable flush:
  ```csharp
  private readonly object _saveSync = new();
  private readonly Dictionary<ProfileViewModel, CancellationTokenSource> _debounce = new();
  private readonly HashSet<ProfileViewModel> _dirty = new();
  public async Task FlushPendingSavesAsync() { /* snapshot _dirty; SaveIfDirtyAsync each */ }
  ```
  Hook `FlushPendingSavesAsync()` into `App.OnExit` **before** `_host.StopAsync`, and register `Application.SessionEnding` to the same flush. Cancel+drop a profile's debounce entry in `DetachProfile` (prevents an edit‑then‑remove save hitting `ProfileManager`'s "not managed" throw).
- **Regression guard:** coalescing preserved (one 500 ms timer *per profile*); single‑profile write volume unchanged; concurrently‑edited profiles now each persist (previously dropped). All map access under `_saveSync` (touched from UI + pool continuations + flush). `QueueAutoSave` is no longer `async void` → removes an unhandled‑exception surface.
- **Test:** edit A then B within 500 ms → store receives **both**; edit A then `await FlushPendingSavesAsync()` → A persisted without the delay.

### M3 — Any autosave failure crashes the app *(REPRO, Phase 0)*
- **Files:** `MainViewModel.cs:383‑395` (`ShowError` after `ConfigureAwait(false)` → pool thread), `DialogService.cs:64‑67` (`MessageBox.Show(owner, …)` → owner `VerifyAccess` throws off‑thread), `App.xaml.cs:106` (`DispatcherUnhandledException` never sets `e.Handled`). The throw escapes the `async void QueueAutoSave` (catches only OCE) → process terminates.
- **Intent:** a save failure must be **surfaced**, not fatal.
- **Fix:** make `DialogService.ShowError` self‑marshal (fixes every call site) and use `InvokeAsync` (don't block the background saver on a modal):
  ```csharp
  public void ShowError(string message, string title) {
      var d = System.Windows.Application.Current?.Dispatcher;
      if (d is not null && !d.CheckAccess()) { d.InvokeAsync(() => ShowErrorCore(message, title)); return; }
      ShowErrorCore(message, title);
  }
  ```
  Combined with M1 (no more `async void`), keep `SaveProfileInternalAsync`'s catch swallowing/logging non‑cancellation faults so autosave can never propagate out of the fire‑and‑forget.
- **Regression guard:** errors still visible (MessageBox on the UI thread); debounce limits dialog spam. **Do not** blanket‑set `e.Handled = true` in the Dispatcher handler — that would mask unrelated real crashes; the targeted marshal+swallow keeps genuine bugs surfacing while making the save‑failure path non‑fatal.
- **Test:** throwing fake `IProfileStore` + recording fake `IDialogService` → autosave triggers → no crash and exactly one recorded error.

---

## 7. Cross‑cutting decision — profile identity, rename & autosave keying

M1 (autosave), M2 (VM rename), P3 (dedup), P4 (store path) must be coherent. **Decision (regression‑minimal):**
- **File identity = `SourcePath`**, stable for a profile's lifetime, independent of `Name`. (The `Guid Id`‑keyed‑file scheme is cleaner but a larger migration; **defer as optional hardening**, not required.)
- **Store:** `DetermineProfilePath` returns `SourcePath` when set; the `SourcePath`‑empty branch gets collision avoidance (P4) so a reused name can never clobber. Optional `RenameProfileAsync` `File.Move` for tidiness.
- **Manager/VM:** `IProfileManager.RenameProfileAsync(Profile, newName, ct)` rejects built‑ins + duplicates, keeps the **same** `Profile` instance; VM calls it and refreshes the display name without re‑autosaving; refresh `LastProfile`.
- **Autosave:** debounce keyed by the in‑memory `ProfileViewModel` instance (stable across rename regardless of identity scheme) + `FlushPendingSavesAsync` on `OnExit`/`SessionEnding`.

This closes M1+M2+M3+P3+P4 with no data loss and no cross‑file clobber.

---

## 8. Detailed fixes — Subsystem D: Input hook engine

File: `Services/InputHookService.cs` (1248 lines). `_activeCombinedOverrides` guarded by `_combinedOverridesLock` (real — writers on UI hook *and* pool worker; lock order profile→overrides, hook path overrides‑only, no inversion).

### H1 — Holding a launcher hotkey spawns dozens of processes *(REPRO, Phase 4)*
- **File:** `:972‑1011` (`HandleWindowsLauncher` guards only `!isKeyDown`; every typematic auto‑repeat re‑queues `LaunchProcess`).
- **Fix:** per‑key "handled while down" latch `HashSet<Key> _heldLauncherKeys` (keyboard‑hook‑thread‑only, no lock — consistent with existing `_caps*` state). Pass `isKeyUp` from `KeyboardCallback`; on key‑up `Remove` the latch (return `true` iff it was latched, to suppress the lone up); on key‑down only launch if `_heldLauncherKeys.Add(key)` is true; clear the set in `ReleaseAllState()`.
- **Regression guard:** one launch per physical press; release+press launches again; distinct keys independent (per‑key membership). Relies on the single‑threaded keyboard‑hook invariant (§2) — note it in the change.

### H2 — Disabling a mapping while its key is held sticks the key down *(REPRO, Phase 4, bundle with H7 fast‑path)*
- **File:** `:542‑634` — the key‑up release (`:617‑631`) sits **below** the `!IsEnabled` (`:545`) and `entry is null` (`:567`) guards, so disabling/deleting mid‑hold skips `_activeCombinedOverrides.Remove` + `SendKey(target,false)` → target stays pressed system‑wide.
- **Fix:** release **by held key** at the very **top** of `HandleCombinedMappings`, before any guard:
  ```csharp
  var sourceKey = KeyInteropUtilities.FromVirtualKey(vkCode);
  if (isKeyUp && sourceKey is not null) {
      CombinedOverrideState? held;
      lock (_combinedOverridesLock) { _activeCombinedOverrides.Remove(sourceKey.Value, out held); }
      if (held is not null) { SendKey(held.TargetKey, false); return held.SuppressOriginal; }
  }
  // ...existing enable/entry guards + key‑DOWN handling... DELETE the now‑dead :617‑631 block.
  ```
- **Regression guard:** normal remap unchanged (a normal key‑up finds its override at the top and releases identically, same `SuppressOriginal`); no double‑release (the entry is removed once); un‑overridden keys fall through; lock contract preserved (mutate under lock, `SendKey` outside).
- **Cost/fast‑path (H7):** this takes the lock on *every* key‑up. Add `volatile int _activeCombinedOverrideCount` (maintained under the lock on add/remove, incl. `ReleaseRightClickOverrides`/`ReleaseAllOverrides`) and gate: `if (isKeyUp && sourceKey is not null && _activeCombinedOverrideCount > 0) { lock … }` — skips the lock only when the dict is provably empty.

### H3 — Fast tap can emit the hold key instead of the tap key *(REPRO, Phase 4)*
- **File:** `:408‑468` — `HoldTimer.Change(threshold)` (`:438`) is armed **before** `HoldCallback` is assigned (`:439`); the shared timer root (`:1230`) re‑reads the `HoldCallback` **field** at fire time. `HandleMouseUp`'s `Change(Infinite)` doesn't un‑queue an already‑elapsed callback. A stale elapse from press #1 runs press #2's closure, wins press #2's `ARMED→FIRED` CAS, and injects the hold key; the quick tap then sees `FIRED` and suppresses the tap key.
- **⚠ Epoch/generation token alone does NOT fix this** — because the root reads the current field, a stale elapse carries the *newest* epoch and passes. The discriminator must be **elapsed time**.
- **Fix:** capture the down‑tick at arm time; the callback no‑ops if `elapsed < threshold − tolerance`; assign the callback **before** arming:
  ```csharp
  var downTickAtArm = state.DownTick!.Value; var holdThreshold = threshold;
  state.HoldCallback = _ => {
      if (!_isRunning || !_altPressed) return;
      var elapsedMs = (Stopwatch.GetTimestamp() - downTickAtArm) * TickToMilliseconds;
      if (elapsedMs < holdThreshold - HOLD_FIRE_TOLERANCE_MS) return;                 // stale/premature → no‑op
      if (Interlocked.CompareExchange(ref stateRef.TimerState, TIMER_FIRED, TIMER_ARMED) != TIMER_ARMED) return;
      FireTapKey(holdKey);
  };
  state.HoldTimer.Change(holdThreshold, Timeout.Infinite);                             // arm AFTER assigning
  ```
  (`HOLD_FIRE_TOLERANCE_MS = 2`; Windows timers fire on‑time or late, never meaningfully early.) Capturing `downTickAtArm` as a local `long` also avoids a torn cross‑thread nullable read.
- **Regression guard:** genuine hold: `elapsed≈threshold` → fires (unchanged); genuine tap: `HandleMouseUp` still emits the tap key via existing logic; a stale early firing is now rejected so it can't flip state to `FIRED`. Threshold value and the manual‑beat‑the‑timer path unchanged.

### H4 — Partial hook‑install failure leaks the installed hook *(REPRO, Phase 1)*
- **File:** `:130‑169` — `_isRunning` is set only after **both** `SetWindowsHookEx` succeed; on partial failure `Stop()` early‑returns at `:175` and never unhooks the one that installed. Secondary: `GetLastWin32Error()` is read *after* `Stop()` clobbers it.
- **Fix:** in the failure branch, capture the error **first**, then unhook whichever handle is non‑zero and null both procs, then throw:
  ```csharp
  var err = Marshal.GetLastWin32Error();
  if (_keyboardHookHandle != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_keyboardHookHandle); _keyboardHookHandle = IntPtr.Zero; }
  if (_mouseHookHandle    != IntPtr.Zero) { NativeMethods.UnhookWindowsHookEx(_mouseHookHandle);    _mouseHookHandle    = IntPtr.Zero; }
  _keyboardProc = null; _mouseProc = null;
  throw new Win32Exception(err, "Failed to install input hooks");
  ```
- **Regression guard:** success path untouched (`_isRunning=true` still only after both); `Stop()` stays idempotent; a failed start now leaves nothing installed. Runs under `_profileLock`.

### H5 — Dispose can throw / leave a key stuck at exit *(REPRO, Phase 1)*
- **File:** `:256‑261` — `_random.Dispose()` (`ThreadLocal<Random>`) while queued pool work items (`FireTapKey :1035`, Toggle hold‑breath `:910`, `HandleRightClickHoldBreathDown :815`) still deref `_random.Value` → `ObjectDisposedException` on a pool thread (no try/catch) → crash on exit.
- **Fix:** set `volatile bool _disposed = true` **first** in `Dispose()`; work‑item bodies early‑return on `_disposed`; and either **don't dispose** the `ThreadLocal<Random>` (no unmanaged resources — let GC reclaim) or guard the derefs with `catch (ObjectDisposedException)`. `Stop()`→`ReleaseAllState()` already releases held keys before disposal — keep that order.
- **Regression guard:** normal runtime unaffected (`_disposed` false); no stuck keys (release happens in `Stop`); a work item racing shutdown becomes a silent no‑op instead of a crash. Do **H4 before H5** (Dispose calls Stop).

### H6 — Hold‑breath fires for a suppressed Alt+right‑click *(REPRO, Phase 4, narrow)*
- **File:** `:325‑337` — the breath timer is armed (`:328`) before `HandleAltMouse` (`:337`) decides to suppress the RMB as an Alt+Right binding.
- **Fix:** reorder so breath arms only for a real right‑click: keep `_rightButtonPressed = true` (CombinedMappings `RightClickOnly` reads it), then `handled = HandleAltMouse(...)`; `if (!handled) HandleRightClickHoldBreathDown();`. On `WM_RBUTTONUP` always call `HandleRightClickHoldBreathUp()`.
- **Regression guard:** genuine right‑clicks (no Alt / no Right binding) arm exactly as today; `_rightButtonPressed` semantics preserved. Mouse hook = UI thread.

### H7 — Hot‑path `LogDebug($…)` + allocations *(PARTIAL, Phase 6, low)*
- A debug‑guard pass was already done (`IsDebugEnabled` gates the hottest paths; unmapped keys already skip the lock via the `:567` early‑return). **Residuals:** unguarded interpolated `LogDebug` at `:829` (every RMB‑down) and `:1058` (every injected tap/hold) — guard with `if (IsDebugEnabled)`. Optional: pre‑allocated `INPUT[1]` for `SendKey` (`:1095`); state‑stored callback to kill the `:439` closure alloc (folds into H3). Plus the `_activeCombinedOverrideCount` fast‑path from H2.
- **Regression guard:** guarding only changes whether a string is built; the alloc optimizations touch injection/timer hot paths → treat as optional, validate separately.

### H8 — Dead `bypassHook` parameter on `SendKey` *(REPRO, Phase 6, low)*
- **File:** `:1063` — all ~16 call sites use the 2‑arg form, so `bypassHook` is always `true` and the `: IntPtr.Zero` branch is dead. Remove the parameter, hardcode `dwExtraInfo = INPUT_IGNORE`. Do this **last** (mechanical, touches every feature's call sites). No functional change (our hook already ignores injected input via both `LLKHF_INJECTED` and `INPUT_IGNORE`).

---

## 9. Detailed fixes — Subsystem E: Startup / window / shutdown / logging

Files: `Services/{ProfileManager,ForegroundWatcher,StartupService,FileLoggerService,SystemTrayService}.cs`, `MainWindow.xaml.cs`, `App.xaml.cs`, `Views/AddProfileDialog.xaml.cs`.

### S1 — Profiles for dotted exe names never activate *(REPRO, Phase 2)*
- **Chain:** `ForegroundWatcher.cs:97‑98` yields already‑extensionless `ProcessName` (`paint.net`); `ProfileManager.FindByExecutable`→`NormalizeExecutable` (`:171‑180`) re‑runs `GetFileNameWithoutExtension` → `paint`; stored side yields `paint.net` (from `paint.net.exe`) → never matches. Add‑profile dialog forces `.exe`, so no workaround.
- **Fix:** one canonical match‑key helper that strips **only** a trailing `.exe` (case‑insensitive), never other dotted segments:
  ```csharp
  public static class ExecutableName {
      public static string Normalize(string? v) {
          if (string.IsNullOrWhiteSpace(v)) return string.Empty;
          var f = Path.GetFileName(v.Trim());
          if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) f = f[..^4];
          return f.Trim().ToLowerInvariant();
      }
  }
  ```
  Route the **3 match‑key sites** through it: `ProfileManager.NormalizeExecutable`, `Profile.NormalizeExecutable`, `MainViewModel.NormalizeExecutable`. Leave `AddProfileDialog.NormalizeExecutable` (the 4th, "disagreeing" site) as the extension‑**preserving** storage form (feeds `.exe` validation); optionally rename it `ToStorageForm` to make the split intentional.
- **Regression guard:** verified against all existing normalization tests (`ModelTests`, `ProfileFactoryTests`, `ProfileManagerTests` — `notepad.exe`→`notepad`, full paths, `TEST.EXE`, dedup) — the helper passes every one; only dotted names change (the bug). No false matches. **Note the untested runtime form:** existing tests only pass `.exe`‑suffixed strings; add a `FindByExecutable("paint.net")` (bare process form) test.

### S2 — Admin autostart broken for paths with spaces *(REPRO, Phase 5, bundle with S3)*
- **File:** `StartupService.cs:141‑147` — `/TR "C:\Program Files\…"` has only one quote layer; `CommandLineToArgvW` strips it, schtasks stores the action unquoted → `C:\Program`; schtasks **exits 0** so the `ExitCode != 0` check passes.
- **Fix:** escaped inner quotes: `/TR "\"{exe}\""` (place any args outside the inner quotes). Harden by verifying the created action via `schtasks /Query /TN … /XML` rather than trusting exit 0.
- **Regression guard:** inner quotes are harmless for space‑free paths; idempotency unchanged (`/F` + prior delete). Extract `BuildCreateArguments(taskName, exe)` for a unit test asserting the `/TR "\"…\""` shape.

### S3 — `schtasks /Delete` swallows "Access denied" *(REPRO, Phase 5, bundle with S2)*
- **File:** `StartupService.cs:173‑198` returns `true` unconditionally; `Apply`'s disable‑both branch ignores the result → an unelevated "disable startup" leaves the elevated HIGHEST task running.
- **Fix:** probe existence via `IsScheduledTaskEnabled()` (absent → idempotent `true`); otherwise read `ExitCode` (read streams before `WaitForExit`, mirroring `TryEnableScheduledTask`) and return `false` + error on failure; surface it from `Apply`. **UAC UX:** a HIGHEST task created elevated can't be deleted unelevated — detect elevation in the Settings UI and relaunch elevated (`runas`) to toggle, or clearly message that admin rights are required (the toggle will otherwise visibly bounce back via `GetState()`).
- **Regression guard:** success and not‑present still return `true`; only genuine failures now report `false`.

### S4 — Windows on a monitor left/above primary don't restore *(REPRO, Phase 5)*
- **File:** `MainWindow.xaml.cs:128‑137` rejects negative `Left/Top` and compares against `VirtualScreenWidth/Height` as if origin were `(0,0)`.
- **Fix:** validate the saved rect against the **origin‑aware** virtual‑desktop rect (`VirtualScreenLeft/Top` + width/height), accept if it meaningfully intersects, else fall back to `CenterScreen`:
  ```csharp
  bool intersects = l < vsRight && (l + Width) > vsLeft && t < vsBottom && (t + Height) > vsTop;
  ```
- **Regression guard:** off‑all‑screens still rescued (center); positive/primary positions unaffected; negative multi‑monitor coords now restore. Extract the pure `intersects(rect, bounds)` predicate for a unit test with synthetic negative‑origin rects.

### S5 — OS shutdown flips "start minimized" *(REPRO, Phase 5, shutdown bundle)*
- **File:** `MainWindow.xaml.cs:195‑205,276‑305` — a forced close with `_allowClose==false` routes through `MinimizeToTray()`, which persists `StartMinimized=true`; there is **no** `Application.SessionEnding` handler.
- **Fix:** handle `Application.SessionEnding` (subscribe in ctor/`OnLoaded`): set `_allowClose = true` and `SaveWindowState()` (persists `_startMinimized` **as‑is**). Keep the user X‑button/minimize → `MinimizeToTray` path unchanged.
- **Regression guard:** deliberate minimize‑to‑tray still remembers; OS restart preserves the user's real preference. Guard double‑run via existing `_allowClose`/`_isMinimizingToTray` flags.

### S6 — OnExit truncation → skipped logger flush + ghost tray icon *(REPRO, Phase 5, shutdown bundle)*
- **File:** `App.xaml.cs:51‑64` — `async void OnExit` + `await _host.StopAsync(...)` posts the `_host.Dispose()` continuation back to a dispatcher that may have stopped pumping → singletons (`FileLoggerService`, `SystemTrayService`, hooks) never disposed → lost log tail + ghost tray icon.
- **Fix:** make `OnExit` **synchronous**, run stop+dispose off the dispatcher with a bounded wait:
  ```csharp
  private void OnExit(object sender, ExitEventArgs e) {
      try {
          if (_host is not null) {
              // StopAsync OFF the dispatcher (avoids sync-over-async deadlock); Dispose ON the dispatcher (tray icon's creating thread).
              try { Task.Run(() => _host.StopAsync(TimeSpan.FromSeconds(2))).Wait(TimeSpan.FromSeconds(5)); }
              finally { _host.Dispose(); }   // always reached, even if StopAsync timed out/threw
          }
      }
      catch (Exception ex) { LogCrash("OnExit", ex); }   // log, do NOT silently swallow (see §14.5)
      finally { UnregisterExceptionHandlers(); }
  }
  ```
- **Regression guard:** no dispatcher dependency; bounded `.Wait` prevents a hang; `ProfileActivationService.StopAsync` (color reset) is still awaited to completion — don't edit it, just ensure it runs. Extract `ShutdownHostAsync(host, timeout)` for testability.

### S7 — Logger loses its final lines *(REPRO, Phase 5, shutdown bundle; depends on S6)*
- **File:** `FileLoggerService.cs:58‑113` — on an IO‑error backoff, `await Task.Delay(1000, token)` throws OCE **inside** the general `catch` at shutdown and propagates *past* the post‑loop final flush.
- **Fix:** guard the backoff delay so cancellation breaks to the drain:
  ```csharp
  catch { buffer.Clear(); try { await Task.Delay(1000, _cancellation.Token).ConfigureAwait(false); } catch (OperationCanceledException) { break; } }
  ```
- **Regression guard:** normal logging untouched; final drain stays bounded (token‑less `TryTake`) → no hang; only a shutdown coinciding with IO‑error backoff now still flushes. **Depends on S6** (the drain only runs if `FileLoggerService.Dispose()` is actually invoked at exit).

### Shutdown sequence (S5+S6+S7 as one)
1. `SessionEnding`/tray‑Exit → `_allowClose=true`, `SaveWindowState()` (real rect, true `StartMinimized`).
2. Window closes → `OnExit` runs **synchronously**: `StopAsync(2s)` (hosted services incl. color reset) → `_host.Dispose()` (tray icon removed, hooks unhooked, `FileLoggerService.Dispose()`).
3. `FileLoggerService.Dispose()` → `Cancel()` + `_writeTask.Wait(2000)`; `ProcessQueue` now breaks to the final drain even during IO‑backoff.
4. `UnregisterExceptionHandlers()` in `finally`.

---

## 10. Test plan

**Existing (65) — must stay green.** Changes required:
- C2: add `NullLoggerService` to 2 `ProfileActivationService` test ctors.
- C1: change `ProfileActivationServiceDeduplicationTests` disabled‑startup expectation `1 → 0`.

**New tests (by phase):**
- **Color:** C1 cold‑start(0)/enable(1)/disable‑restore(1) via `RecordingColorControlService`; C2 throwing‑color fake keeps worker alive; C5 forced re‑apply re‑applies an unchanged plan, and stays empty when disabled; C6 message contains the real value with `NullLoggerService.IsEnabled=true`.
- **Persistence:** P1 legacy `[RightMouse]`→`CombinedMappingEntry{RightClickOnly=true}`; P2 `Mode=MomentaryShift`→`Hold`; P8 `[AltMouse.Button4]`→`XButton1`; P3 two same‑named files both survive + save; P4 rename writes back to same `SourcePath` and a new reused‑name profile lands on `Old (2).ini`; P5 color round‑trip (5 fields + legacy 4‑field). *(Consider an `internal` `IniProfileStore` ctor taking a root dir + `InternalsVisibleTo("Tests")` to unit‑test migrations instead of hitting `%APPDATA%`.)*
- **MVVM:** M1 edit‑A‑then‑B persists both + flush; M3 throwing store → no crash + one recorded error (new throwing `IProfileStore` + recording `IDialogService` fakes).
- **Startup:** S1 `FindByExecutable("paint.net")` matches `paint.net.exe`; S2 `BuildCreateArguments` shape; S4 `intersects` predicate.
- **Hook (pure decision helpers only — LL/timer paths aren't unit‑testable):** H1 should‑launch given held‑set; H2 which held key to release on up; H3 tap‑vs‑hold given `elapsed/threshold/finalState`.

**Manual repro matrix** (for the non‑unit‑testable runtime paths): C4/C8 (multi‑monitor NVIDIA), C5 (sleep/resume), C7 (hot‑plug), H1 (hold Win+NumPad), H2 (hold Q→Shift then disable), H3 (rapid taps under load), H5 (exit during tap), H6 (Alt+right‑click with a Right binding), S2/S3 (Program Files install + unelevated disable), S4 (left/top monitor), S5 (OS restart), S6 (tray icon vanishes + log tail present).

---

## 11. Risk register

| Risk | Where | Mitigation |
|---|---|---|
| C1 diff changes a behavior a test encodes | `ApplyColorPlan` | Explicitly flip the dedup test `1→0`; add cold‑start/restore tests; `Apply` itself untouched so slider‑revert unaffected. |
| C5 re‑apply re‑wipes calibration on resume | `ProfileActivationService` | Route forced re‑apply through the C1 diff; **ship C1+C5 together**. |
| Migration mis‑maps legacy data | `DeserializeCombinedMappings`/`CapsLock`/AltMouse | Semantic equivalence proven from master source; new‑format tried first; round‑trip tests per legacy shape. |
| Rename/identity change corrupts files | `DetermineProfilePath`/`ProfileManager`/VM | Collision‑avoid only on empty `SourcePath`; keep same `Profile` instance; built‑ins excluded; integration test for clobber. |
| H2 lock on every key‑up adds hot‑path cost | `HandleCombinedMappings` | `_activeCombinedOverrideCount` volatile fast‑path skips the lock when empty. |
| H3 "fix" that only uses an epoch token | `HandleMouseDown` | Documented insufficient — use **elapsed‑time** guard. |
| Shutdown change hangs exit | `App.OnExit` | Bounded `.Wait(5s)`; `StopAsync` capped at 2s; off‑dispatcher `Task.Run`. |
| C7 handler leak (singleton→transient) | `ColorSettingsViewModel` | Mandatory unsubscribe on teardown; Dispatcher‑marshalled rebuild. |
| Deleting "dead code" that's actually used | cleanup sweep | Reference‑check each before removal (see §12); only `SendKey.bypassHook` is confirmed dead. |

---

## 12. Low‑priority cleanup (Phase 6, correctness always outranks)

- **Dead code — verify‑then‑remove** (FABLE listed; only some independently confirmed): `SendKey.bypassHook` (confirmed dead, H8); `NvidiaColorControlService.TryApplyGammaRamp(profile)` null‑overload (appears unused post‑C8); `MouseButtonBindingViewModel`, `AvailableKeysConverter`, `IniExtensions.Get/SetDouble` — **run a reference check (Serena `find_referencing_symbols` once the .NET‑10 LSP is available, or Grep) before deleting.**
- **Duplication:** color‑block serialization (P5), the clamp helpers, and the 4 exe‑normalizers (S1) — unify.
- **Magic‑name built‑in special‑casing (~28 sites):** left as‑is for now (mitigated because the UI blocks renaming built‑ins); a future refactor could introduce a `ProfileKind` enum. Out of scope for a regression‑safe bug‑fix pass.

---

## 13. FABLE → plan status ledger

| FABLE item | Plan | Status |
|---|---|---|
| 1 Startup wipes calibration | **C1** | REPRO |
| 2 Autosave drops edits | **M1** | REPRO |
| 3 Rename destroys another profile's file | **M2 + P4 + P3** | REPRO |
| 4 Upgrade drops right‑mouse | **P1** | REPRO |
| 5 Upgrade resets CapsLock Hold | **P2** | REPRO |
| 6 One exception kills switching | **C2** | REPRO |
| 7 Crash on autosave failure | **M3** | REPRO |
| 8 Color worker races UI dict | **C3** | REPRO |
| 9 Dotted exe never activate | **S1** | REPRO |
| 10 Admin autostart broken (spaces) | **S2** | REPRO |
| 11 Launcher hotkey spawns dozens | **H1** | REPRO |
| 12 Per‑display vibrance wrong monitor | **C4** | REPRO |
| 13 Disabling mapping while held sticks key | **H2** | REPRO |
| 14 Fast tap emits hold key | **H3** | REPRO |
| 15 Colors wrong after sleep/resume | **C5** | REPRO |
| Duplicate names break save | **P3** | REPRO |
| schtasks /Delete swallows denied | **S3** | REPRO |
| NvAPI logs useless | **C6** | REPRO |
| Hot‑plug monitors absent | **C7** | REPRO |
| Window left/above primary | **S4** | REPRO |
| Partial hook‑install leak | **H4** | REPRO |
| Dispose throw / stuck key | **H5** | REPRO |
| OS shutdown starts hidden | **S5** | REPRO |
| Gamma to primary on CreateDC fail | **C8** | REPRO |
| OnExit truncation | **S6** | REPRO |
| Hold‑breath for suppressed Alt+RMB | **H6** | REPRO |
| Logger loses final lines | **S7** | REPRO |
| Hand‑edited INI key quirks | **P6** | REPRO |
| exe‑normalization copied 4 ways | **S1** | REPRO |
| color serialization duplicated | **P5** | REPRO |
| sliders sync GDI+NvAPI per tick | **C9** | REPRO |
| per‑keystroke lock/alloc + LogDebug | **H7** | PARTIAL |
| dead `bypassHook` etc. | **H8** + §12 | REPRO |
| IniDocument mid‑file/bare‑name | **P7** | UNREACHABLE |
| *(new)* AltMouse Button4/5 migration gap | **P8** | REPRO |

---

## 14. Codex review — incorporated amendments

> Reviewed by **Codex (gpt‑5.5, high reasoning)** on 2026‑07‑06. Verdict: **Sound with fixes.** Codex independently confirmed the core regression‑safety decisions — C1's orchestration‑layer diff (an `IsEnabled` short‑circuit inside `Apply` *would* break the live‑slider revert at `DisplayColorSettingsViewModel.cs:185‑217`), H2 release‑by‑held‑key, H3 elapsed‑time over an epoch token, the migration semantics, and that live‑slider `_lastAppliedColorPlan` staleness is **not** a C1 blocker (the slider updates the model before applying, so the next worker plan differs and re‑applies). The six refinements below are folded in.

**14.1 — C1: failure‑aware dedup (also closes FABLE #15's second half).** Today `_lastAppliedColorPlan = plan` is recorded even when `Apply` returns `false`, so a failed *enabled* apply is masked and never retried. Make `ApplyColorPlan` return `bool` (false if any enabled display's `Apply()` returned false or its `DisplayInfo` wasn't found) and set `_lastAppliedColorPlan = plan` **only when true** — a failed enabled apply stays un‑deduped and is retried on the next foreground/resume event. Deliberate fail‑closed *skips* (C4 unmappable‑DVC, C8 CreateDC) return a distinct "skipped" result **treated as applied**, so they don't cause a per‑event retry storm. *(Amends §4 C1; §3 Phase 3.)*

**14.2 — C5: stale process‑name race.** The resume/display handler must not enqueue a *previous* foreground app. Update `_lastProcessName` in `OnForegroundChanged` (on newest event arrival), never at end‑of‑processing; the force handler then `TryWrite`s the genuinely‑current name (and the channel's `DropOldest` keeps a newer real event if one races in). Failure scenario avoided: last‑processed = `notepad`, worker mid‑processing `chrome`, resume fires → without this fix it would reactivate `notepad`'s profile while Chrome is foreground. Alternative: write a force *sentinel* and resolve the current foreground inside the worker. *(Amends §4 C5.)*

**14.3 — C3: snapshot the serialization path too.** `ColorSettings.DisplayProfiles` exposes the live dictionary and INI serialization enumerates it directly (`IniProfileStore.cs` color write, ~`:417`/`:441`). Once **M1** lets autosave run from pool continuations, a UI/hot‑plug mutation can race the serialization `foreach` ("collection was modified"). Route serialization through `SnapshotProfiles()` as well. Couples C3 with **M1** (§6) and **P5** (§5). *(Amends §4 C3; §3 Phase 1/2.)*

**14.4 — P3: suffix custom profiles that collide with reserved built‑in names.** Built‑in identity is name‑derived (`Profile.IsWindowsProfile`/`IsColorProfile`, `Profile.cs:41‑45`). A custom on‑disk file named `Windows` or `Color Settings` would otherwise become undeletable/unrenamable or shadow the built‑in lookup. Dedup‑on‑load must **suffix** such custom collisions (e.g. `Windows (2)`), not merely skip the reserved names. *(Amends §5 P3.)*

**14.5 — S6: dispose on the dispatcher, and don't let one `Dispose` skip another.** Run `StopAsync` off‑dispatcher (avoids the sync‑over‑async deadlock) but call `_host.Dispose()` on the dispatcher thread in a `finally` so `SystemTrayService.Dispose()` runs on the tray's creating thread and is always reached (see the updated §9 S6 sketch). Additionally make `SystemTrayService.Dispose()` and `FileLoggerService.Dispose()` **individually exception‑safe** (swallow+log internally) — a throw from one container‑disposed singleton can otherwise skip the other's cleanup (missed tray removal *or* missed log flush). Log the outer catch; don't silently swallow. *(Amends §9 S6.)*

**14.6 — S2/S3: invoke `schtasks` by absolute path.** Because this manages *elevated* startup, call `%SystemRoot%\System32\schtasks.exe` (from `Environment.GetEnvironmentVariable("SystemRoot")` / `GetFolderPath(System)`) rather than the bare `schtasks.exe`, to avoid a PATH‑order hijack of an elevated launch. Apply the same to any other elevated bare‑binary launch (e.g. `explorer.exe`) as a related hardening item. *(Amends §9 S2/S3.)*

**Sequencing amendments:** Phase 3's C1+C5 bundle now also includes 14.1 (no dedup on failed applies). Phase 1's C3 snapshot is consumed by both worker planning *and* INI serialization (14.3), so it must land before M1 moves autosave off the UI thread. None of the six block **Phase 0**; they refine Phases 1–3 and 5. **Disposition: approved to implement with these folded in.**

**Resume:** this Codex session can be continued (`codex resume`) for deeper analysis or to review the eventual diff.

---

*End of plan. Next: `/codex` review (see request), with focus on regression traps in C1/C5 (calibration), the identity/rename bundle (§7), and the H3 elapsed‑time argument.*
