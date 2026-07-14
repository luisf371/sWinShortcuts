# Codebase Review Report

**Repository:** `sWinShortcuts`  
**Project type:** .NET 8 Windows desktop utility (`WPF` + WinForms tray) using low-level keyboard/mouse hooks, per-process profiles, INI persistence, Windows Task Scheduler, GDI gamma ramps, and NVIDIA NVAPI  
**Branch:** `main`  
**Commit reviewed:** `aa1155b2f089ba391773873582e0b847949802f9`  
**Review date:** 2026-07-10  
**Review mode:** Static review plus local Release build, full test run, NuGet advisory/deprecation/outdated queries, repository secret-pattern sweep, and current Microsoft/NVIDIA documentation checks

The runtime entry points are `App.OnStartup()`, `ProfileActivationService`, `ForegroundWatcher`, and `InputHookService`. Persistent state is under `%APPDATA%\sWinShortcuts`; the application may optionally register itself as a highest-privilege logon task. There is no web server, remote API, database, authentication layer, container, or cloud deployment surface in this repository.

### Validation performed

| Check | Result |
|---|---|
| `dotnet build .\sWinShortcuts.csproj -c Release --no-incremental` | Passed: `0` errors, `163` warnings, predominantly duplicated `CA1416` platform warnings; the sandboxed restore also emitted `NU1900` before feed access was granted. |
| `dotnet test .\Tests\Tests.csproj -c Release --no-restore` | Passed: `105/105` outside the workspace sandbox. The first sandboxed run failed `12` persistence tests because they write to the real `%APPDATA%`, which is itself finding F-021. |
| Production NuGet audit | No known vulnerable packages reported by `dotnet list ... --vulnerable --include-transitive`. |
| Test NuGet audit | Two High-advisory transitive packages reported: `System.Net.Http 4.3.0` and `System.Text.RegularExpressions 4.3.0`. |
| NuGet deprecation query | `xunit 2.6.1` and its v2 graph are marked `Legacy`; NuGet recommends `xunit.v3`. |
| Secret-pattern sweep | No obvious private keys, GitHub tokens, AWS keys, API-key assignments, or password assignments found in tracked source/config files. |
| CI/release inventory | No CI workflow, package lock, SDK pin, installer, application manifest, signing setup, SBOM, or publish profile found. |

Hardware-dependent behavior was not executed against a real NVIDIA GPU, HDR display, ICC calibration, elevated target process, or live low-level hook chain. Findings in those areas are derived from complete call-chain inspection and the documented Windows/NVIDIA contracts.

### Severity scale used

| Severity | Definition |
|---|---|
| ⛔ **Critical** | Likely exploit, data loss, system compromise, or frequent crash with little precondition. |
| 🔴 **High** | Serious risk, plausible exploit, or major functional breakage. |
| 🟠 **Medium** | Meaningful issue with a workaround or narrower precondition; can escalate in realistic conditions. |
| 🟡 **Low** | Minor correctness, lifecycle, diagnostics, or quality issue. |
| 🔵 **Info** | Non-blocking suggestion or polish. |

## Executive Summary

- **Finding count:** Critical `0`, High `8`, Medium `14`, Low `3`, Info `0` — `25` total.
- The most urgent correctness risk is architectural: foreground profile switching waits behind synchronous color enumeration/application on a capacity-one coalescing channel. The previous app's remaps can therefore remain active in the new foreground process.
- Core hook callbacks still reach synchronous `SendInput` calls. The code itself records approximately 300 ms foreign-hook stalls, while Microsoft warns that slow callbacks can be silently removed by Windows.
- NVAPI display enumeration can loop forever on valid error statuses or a partially resolved delegate set. Because color work precedes input activation, this can permanently stop profile switching.
- Two local elevation boundaries are unsafe: the highest-privilege startup task accepts a potentially user-writable executable path, and an elevated instance trusts admin-launch definitions from user-writable `Win.ini`.
- Display hot-plug refreshes can write a neutral gamma/DVC state to displays explicitly created as disabled, defeating the calibration-preservation design. Color disable/exit also does not restore the exact pre-application hardware baseline.
- Profile identity is derived from mutable display names. A custom INI named `Windows` or `Color Settings` is misclassified and can overwrite a built-in INI.
- The Release build and all `105` tests pass, but the riskiest 3,763-line hook state machine has no deterministic behavior tests beyond the pure watchdog decision function; the solution and repository CI do not run tests by default.
- Production dependencies are free of known advisories, but the test graph is Legacy and resolves two High-advisory packages. The project also needs a planned move from .NET 8 before its 2026-11-10 end of support.
- Several earlier risks recorded in project history are fixed at current HEAD: atomic INI replacement, per-profile debounce, legacy RightMouse/CapsLock/Button4-5 migrations, bounded foreground work, locked combined-override storage, dotted executable normalization, and safer `schtasks` quoting/path resolution.

## Scorecard

| Category | Rating | Notes | Key Risks |
|---|---:|---|---|
| Security | D | No remote/auth surface or embedded secrets, but optional elevated operation crosses two user-writable trust boundaries. | Writable HIGHEST task target; user-writable admin launcher configuration. |
| Reliability | D | Core startup and happy-path tests pass, but color, hook, shutdown, and persistence ordering contain major failure modes. | Stale profile isolation, hook stalls, infinite NVAPI loop, failed-save data loss. |
| Maintainability | C | MVVM and service boundaries are recognizable, but `InputHookService` is a 3,763-line multi-threaded state machine and warning noise is high. | State coupling, synchronous native calls, implicit INI schema, dead artifacts. |
| Testing | D | `105` cases cover models, manager CRUD, current INI round-trips, color plans, and watchdog decisions. | No hook behavior tests, no UI/view-model tests, real-AppData integration tests, timing sleeps. |
| Dependencies | C | Production advisory scan is clean. | Legacy xUnit graph with two High advisories, stale packages, .NET 8 near EOS, no lock file. |
| DevOps | F | No automated build/test/security/release pipeline exists. | Tests absent from solution; no CI, signing, SBOM, SDK pin, locked restore, or release provenance. |

## Findings Index

| ID | Severity | Category | Short title | Component/Area | File(s) | Confidence | Status |
|---|---|---|---|---|---|---|---|
| F-001 | 🔴 High | 🐞 Bug / 🧵 Concurrency | Foreground profile isolation waits behind color I/O | Profile activation | `ProfileActivationService.cs` | High | New |
| F-002 | 🔴 High | 🛡️ Reliability / 🧵 Concurrency | Hook callbacks call synchronous `SendInput` | Input hooks | `InputHookService.cs` | High | New |
| F-003 | 🔴 High | 🛡️ Reliability | NVAPI enumeration can loop forever | Display color / NVAPI | `NvidiaColorControlService.cs` | High | New |
| F-004 | 🔴 High | 🔒 Security | HIGHEST autostart trusts a writable executable | Startup / elevation | `StartupService.cs` | High | New |
| F-005 | 🔴 High | 🔒 Security | Elevated launcher trusts user-writable `Win.ini` | Windows Launcher | `IniProfileStore.cs`; `InputHookService.cs`; `ProcessLauncher.cs` | High | New |
| F-006 | 🔴 High | 🐞 Bug / 🛡️ Reliability | Hot-plug rebuild overwrites disabled-display calibration | Color UI / hardware | `ColorSettings.cs`; `ColorSettingsViewModel.cs`; `DisplayColorSettingsViewModel.cs` | High | New |
| F-007 | 🔴 High | 🐞 Bug / 🛡️ Reliability | Reserved custom names can clobber built-in INIs | Profile identity / persistence | `Profile.cs`; `IniProfileStore.cs`; `ProfileManager.cs` | High | New |
| F-008 | 🔴 High | 🛡️ Reliability | Built-in profile I/O failure aborts startup | Startup / persistence | `IniProfileStore.cs`; `MainWindow.xaml.cs`; `App.xaml.cs` | High | New |
| F-009 | 🟠 Medium | 🛡️ Reliability | Color disable/exit does not restore hardware baseline | Display color | `ProfileActivationService.cs`; `NvidiaColorControlService.cs` | High | New |
| F-010 | 🟠 Medium | 🛡️ Reliability / 🧵 Concurrency | Canceled shutdown abandons active color work | Hosted-service shutdown | `ProfileActivationService.cs` | High | New |
| F-011 | 🟠 Medium | 🐞 Bug | Shared mapping targets are not reference-counted | Combined mappings | `InputHookService.cs` | High | New |
| F-012 | 🟠 Medium | 🐞 Bug | Live Caps Lock edits can leave injected state stuck | Caps Lock remapping | `InputHookService.cs` | High | New |
| F-013 | 🟠 Medium | 🛡️ Reliability | Failed key-up injection is forgotten | Input injection | `InputHookService.cs` | Med | New |
| F-014 | 🟠 Medium | 🛡️ Reliability | Failed autosave permanently clears dirty state | Profile autosave | `MainViewModel.cs` | High | New |
| F-015 | 🟠 Medium | 🛡️ Reliability | Profile deletion mutates memory before durable delete | Profile CRUD | `ProfileManager.cs`; `IniProfileStore.cs`; `MainViewModel.cs` | High | New |
| F-016 | 🟠 Medium | 🐞 Bug / 📘 Docs/UX | Settings Cancel and save failure are not transactional | Settings dialog | `SettingsViewModel.cs`; `SettingsWindow.xaml.cs`; `MainWindow.xaml.cs` | High | New |
| F-017 | 🟠 Medium | 🐞 Bug / 📘 Input Validation | Inline executable edit bypasses validation | Profile editor | `MainWindow.xaml`; `ProfileViewModel.cs`; `ProfileManager.cs` | High | New |
| F-018 | 🟠 Medium | 🧵 Concurrency | Color snapshots do not synchronize value mutation | Color model / autosave | `ColorSettings.cs`; `DisplayColorSettingsViewModel.cs`; `ProfileActivationService.cs` | High | New |
| F-019 | 🟠 Medium | 📦 Dependencies | Legacy test graph, near-EOS runtime, non-reproducible restore | Dependencies / runtime | `Tests.csproj`; `sWinShortcuts.csproj` | High | New |
| F-020 | 🟠 Medium | 🧪 Testing | Critical hook state machines lack behavior tests | Input hooks | `InputHookService.cs`; `InputHookWatchdogTests.cs` | High | New |
| F-021 | 🟠 Medium | 🧪 Testing / 🛡️ Reliability | Integration tests modify real user profiles | Persistence tests | `IniProfileStoreIntegrationTests.cs`; `IniProfileStore.cs` | High | New |
| F-022 | 🟠 Medium | ⚙️ Config/DevOps / 🧪 Testing | Solution and repository automation omit tests | Build / CI | `sWinShortcuts.sln`; `Tests.csproj` | High | New |
| F-023 | 🟡 Low | 🛡️ Reliability | WinEvent unhook runs on the wrong thread | Foreground watcher | `ForegroundWatcher.cs`; `App.xaml.cs` | High | New |
| F-024 | 🟡 Low | 🛡️ Reliability | Malformed INI fields silently become defaults | Configuration parsing | `IniDocument.cs`; `IniExtensions.cs`; `IniProfileStore.cs` | High | New |
| F-025 | 🟡 Low | 🧹 Maintainability / ⚙️ Config/DevOps | Release build has a 163-warning baseline | Build diagnostics | `sWinShortcuts.csproj`; `AssemblyInfo.cs` | High | New |

## Detailed Findings

### F-001 — Foreground profile isolation waits behind color I/O

> 🔴 **High** | 🐞 **Bug** / 🧵 **Concurrency** | **Confidence: High** | **Status: New**

**What:** All foreground events enter a capacity-one `DropOldest` channel, and the worker performs display enumeration plus gamma/NVAPI work before activating or deactivating the input profile. Only foreground Auto-Run is released eagerly; Alt+Mouse, combined mappings, Caps Lock, hold-breath, and other profile state remain associated with the old app.

**Where:** `ProfileActivationService.cs:23-29` under `_foregroundChanges`; `ProfileActivationService.cs:203-239` under `ProcessForegroundChange()`.

**Why it matters:** Microsoft documents that `SetDeviceGammaRamp` may take up to 200 ms. During an A→B switch, B can receive A's mappings during that delay. During A→B→A while the worker is blocked, B may be dropped entirely, so the service never observes a boundary on which it can release A's full state. This is a cross-application input-isolation failure, not just UI lag. [Microsoft `SetDeviceGammaRamp` documentation](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setdevicegammaramp)

**How to reproduce:** Use a blocking fake `IColorControlService`; activate profile A; raise foreground B and then A while the first color apply is blocked; press an A-mapped key while B is actually foreground. The hook still exposes A's non-Auto-Run mappings.

**Recommended fix:**

1. Split foreground handling into a fast, non-coalesced input-identity/profile transition and a separately coalesced color-plan worker.
2. Publish and apply deactivation/release synchronously on every real foreground identity boundary before hardware I/O.
3. Keep color deduplication independent of input activation and make color failures incapable of blocking profile switches.
4. As a defense in depth, make hook feature paths validate the latest foreground generation/identity, as Auto-Run already does.

**Fix complexity:** L  
**Risk of change:** High

**Suggested tests:** Blocking color fake with A→B and A→B→A; assert immediate `ReleaseAllState`, no A injection while B is foreground, correct final profile, and independent color coalescing.

### F-002 — Hook callbacks call synchronous `SendInput`

> 🔴 **High** | 🛡️ **Reliability** / 🧵 **Concurrency** | **Confidence: High** | **Status: New**

**What:** Multiple keyboard and mouse callback paths call `SendKey()`, `FireTapKey()`, `ForceCapsLockState()`, or `SendDummyKeyEvent()` synchronously; those functions allocate input arrays and call `NativeMethods.SendInput()` before the hook returns.

**Where:** `InputHookService.cs:1510-1611` under `HandleCombinedMappings()`; `InputHookService.cs:1624-1806` under `HandleCapsLock()`; `InputHookService.cs:1437-1479` under `HandleMouseUp()`; `InputHookService.cs:3362-3377` under `HandleWindowsLauncher()`; `InputHookService.cs:3453-3515` under `SendKey()` and `SendDummyKeyEvent()`.

**Why it matters:** The file's own concurrency comments record foreign-hook stalls around 300 ms. Microsoft requires low-level callbacks to return within `LowLevelHooksTimeout`; Windows 7+ may silently remove a timed-out hook, and Microsoft recommends handing work to a worker and returning immediately. This can cause system input stalls, lost remapping, or silent hook loss during ordinary feature use. The watchdog detects some losses after the fact but does not prevent the stall. [Microsoft `LowLevelKeyboardProc` documentation](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/ms644985%28v%3Dvs.85%29)

**How to reproduce:** Replace the native `SendInput` boundary with a fake that blocks for 300-1,100 ms, then exercise combined mapping, Caps remap, Alt+Mouse tap, and Windows Launcher dummy-key paths. The hook callback does not return until the fake releases.

**Recommended fix:** Route every hook-originated injection through one ordered, dedicated injector. Keep only suppression/state decisions in the callback. Use generations plus acknowledgements so queued DOWN/UP events remain ordered across profile switches, failed sends, and `Stop()`; do not add per-feature worker queues that can reorder releases.

**Fix complexity:** L  
**Risk of change:** High

**Suggested tests:** A blocking native adapter must not delay callbacks; verify FIFO DOWN/UP pairing, release epochs, profile-switch cancellation, failure reporting, and no injection after stop.

### F-003 — NVAPI enumeration can loop forever

> 🔴 **High** | 🛡️ **Reliability** | **Confidence: High** | **Status: New**

**What:** Both NVIDIA display-handle loops stop only on exactly `NVAPI_END_ENUMERATION`. Every other non-OK result logs and continues with the next unbounded integer index. Function loading also reports success when only the initialize delegate exists, even if enumeration or name-resolution delegates are null.

**Where:** `NvidiaColorControlService.cs:172-190` under `TryApplyNvapiDvc()`; `NvidiaColorControlService.cs:271-319` under `FindDisplayHandle()`; `NvidiaColorControlService.cs:410-448` under `EnsureFunctionsLoaded()`; `NvidiaColorControlService.cs:504-512` under `NvAPI_EnumNvidiaDisplayHandle()`.

**Why it matters:** NVIDIA's current contract says callers enumerate until the function returns an error and lists `NVAPI_INVALID_ARGUMENT`, `NVAPI_NVIDIA_DEVICE_NOT_FOUND`, and `NVAPI_END_ENUMERATION` as terminal errors. A missing delegate returns `-1` forever in the current code. The loop holds the color-service lock and runs before input profile activation, permanently freezing profile switching. [NVIDIA NVAPI display-handle documentation](https://docs.nvidia.com/nvapi/group__disphandle.html)

**How to reproduce:** Inject an NVAPI adapter that returns `-1`, device-not-found, or invalid-argument at index 0. Alternatively, resolve initialize but return null for the enumeration delegate. Either loop increments indefinitely.

**Recommended fix:** Break on the first non-OK result, distinguish expected end/no-device from actionable failure in logs, and add a documented defensive maximum enumeration count. `EnsureFunctionsLoaded()` must require every delegate used by the enabled capability before it reports success.

**Fix complexity:** S  
**Risk of change:** Low

**Suggested tests:** Return each terminal status at index 0 and after one valid handle; simulate each missing delegate; assert bounded completion, one diagnostic, and continued input-profile switching.

### F-004 — HIGHEST autostart trusts a writable executable

> 🔴 **High** | 🔒 **Security** | **Confidence: High** | **Status: New**

**What:** Admin autostart registers the current process path verbatim as a `/RL HIGHEST` logon task. It does not verify that the executable and every ancestor directory are protected from standard-user modification or reparse-point substitution.

**Where:** `StartupService.cs:85-93` under `GetExecutablePath()`; `StartupService.cs:142-164` under `TryEnableScheduledTask()`; `StartupService.cs:265-279` under `GetSchtasksPath()` and `BuildCreateArguments()`.

**Why it matters:** If a portable/dev copy runs from Downloads, a repository, `%LOCALAPPDATA%`, or another user-writable directory, a standard-user process can replace the executable after the administrator creates the task. At the next logon, Task Scheduler runs the replacement with the user's highest privileges. Microsoft recommends installing privileged executables into a protected location such as Program Files and securing any alternate location with restrictive ACLs. [Microsoft secure-installation guidance](https://learn.microsoft.com/en-us/windows/win32/msi/guidelines-for-authoring-secure-installations), [Windows Shell installation security](https://learn.microsoft.com/en-us/windows/win32/shell/sec-shell)

**How to reproduce:** Run a copy from a standard-user-writable folder, elevate once, enable `Start as admin`, replace that EXE from a non-elevated token, then log off/on.

**Recommended fix:** Refuse HIGHEST task creation unless the executable is in an approved, admin-protected install root. Resolve the final path, reject reparse points where appropriate, and verify the file plus every ancestor ACL denies write/delete/rename to non-admin principals. Prefer a signed installer into Program Files. Also make task query results tri-state: `TryDisableScheduledTask()` currently treats any query timeout/error as “absent” at `StartupService.cs:173-196`, which can falsely report that elevated autostart was removed.

**Fix complexity:** M  
**Risk of change:** Low

**Suggested tests:** ACL matrix for Program Files versus user-writable roots; reparse-point path; failed task query/delete; task action must equal the verified canonical path.

### F-005 — Elevated launcher trusts user-writable `Win.ini`

> 🔴 **High** | 🔒 **Security** | **Confidence: High** | **Status: New**

**What:** The Windows Launcher loads executable path, arguments, and `RunAsAdmin` from `%APPDATA%\sWinShortcuts\Win.ini`. When the main app already runs elevated, `ProcessLauncher` executes a `RunAsAdmin=true` target from the elevated process without a new trust decision.

**Where:** `IniProfileStore.cs:23-30` in the constructor; `IniProfileStore.cs:128-141` under `LoadWindowsProfile()`; `InputHookService.cs:3362-3386` under `HandleWindowsLauncher()` and `LaunchProcess()`; `ProcessLauncher.cs:13-46` under `Launch()`.

**Why it matters:** A standard-user process for the same account can rewrite the user-owned INI to point a launcher binding at an arbitrary payload. If sWinShortcuts later starts elevated and the user presses the configured Win+Numpad chord, the payload inherits administrator rights. The preconditions narrow the attack—local write access, elevated app, and a trigger—but this is still a realistic confused-deputy boundary.

**How to reproduce:** While the app is closed, edit `Win.ini` from a non-elevated process, set a launcher path to a test payload and `RunAsAdmin=true`, start sWinShortcuts via its HIGHEST task, then press the binding.

**Recommended fix:** Do not consume privileged execution allowlists from user-writable storage in an elevated process. Store admin-approved targets in admin-protected configuration with an explicit authorization flow, validate canonical paths/signatures as policy requires, and keep ordinary launcher preferences separate. The strongest architecture is an unelevated hook/UI process plus a narrow privileged broker that approves only pre-authorized actions.

**Fix complexity:** L  
**Risk of change:** High

**Suggested tests:** Tamper `Win.ini` under a standard token and assert an elevated instance refuses the changed admin binding; test canonical-path replacement, argument changes, and explicit re-authorization.

### F-006 — Hot-plug rebuild overwrites disabled-display calibration

> 🔴 **High** | 🐞 **Bug** / 🛡️ **Reliability** | **Confidence: High** | **Status: New**

**What:** New displays are deliberately created disabled so cold-start activation leaves their calibration untouched. However, a display-list rebuild calls `NotifyMasterEnabledChanged()` on every row; that method performs a hardware apply/revert, and the disabled branch writes a neutral gamma ramp and DVC value.

**Where:** `ColorSettings.cs:49-69` under `GetOrCreateProfile()`; `ColorSettingsViewModel.cs:63-98` under `OnDisplaysChanged()` and `RebuildDisplayViewModels()`; `DisplayColorSettingsViewModel.cs:165-218` under `NotifyMasterEnabledChanged()` and `ApplyToHardwareOrRevert()`.

**Why it matters:** Connecting, disconnecting, or re-enumerating a monitor can overwrite ICC, Night Light, f.lux/third-party gamma, or NVIDIA vibrance on a display the user never opted into sWinShortcuts color control. Microsoft calls `SetDeviceGammaRamp` global, undefined with HDR and calibration solutions, and strongly recommends not using it for calibration. [Microsoft `SetDeviceGammaRamp` documentation](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setdevicegammaramp)

**How to reproduce:** Create the global Color profile VM with live updates, leave a display disabled, raise `IDisplayService.DisplaysChanged`, and observe a disabled neutral profile passed to `IColorControlService.Apply()`.

**Recommended fix:** Separate UI-state notification from hardware side effects. Rebuilds should only raise `AreControlsEnabled`; only explicit user toggles should apply/revert. Route topology reapplication through `ProfileActivationService`, whose plan diff can decide whether hardware was previously owned by the app.

**Fix complexity:** S-M  
**Risk of change:** Medium

**Suggested tests:** Hot-plug with a new/disabled display makes zero hardware calls; explicit enabled→disabled makes one restore call; enabled displays are reapplied only by the activation pipeline.

### F-007 — Reserved custom names can clobber built-in INIs

> 🔴 **High** | 🐞 **Bug** / 🛡️ **Reliability** | **Confidence: High** | **Status: New**

**What:** Built-in profile type is computed entirely from mutable `Profile.Name`. A custom INI can declare `Name=Windows` or `Name=Color Settings`; loading accepts it verbatim, and name deduplication excludes it because it already appears to be built-in. Saving routes it to the canonical built-in file.

**Where:** `Profile.cs:43-49` under `IsWindowsProfile` and `IsColorProfile`; `IniProfileStore.cs:72-85` under `SaveProfileAsync()`; `IniProfileStore.cs:175-195` under `LoadProfile()`; `ProfileManager.cs:74-106` under `DeduplicateProfileNames()`.

**Why it matters:** A malformed, hand-edited, or legacy custom profile becomes undeletable and can overwrite `Win.ini` or `Color.ini`, causing built-in configuration loss. It also creates duplicate special profiles with ambiguous UI and manager behavior.

**How to reproduce:** Place `Profiles\Reserved.ini` containing `[Profile]` and `Name=Windows`; launch, edit/save that entry, then inspect `Win.ini`.

**Recommended fix:** Add an immutable `ProfileKind` or stable ID assigned by the factory/load path. Persistence routing and deletion protection must use kind/canonical source identity, never display name. During migration, suffix reserved custom names while preserving their original source files.

**Fix complexity:** M  
**Risk of change:** Medium

**Suggested tests:** Custom files with both reserved names remain custom, receive deterministic display suffixes, save back to their source paths, and cannot write/delete either built-in INI.

### F-008 — Built-in profile I/O failure aborts startup

> 🔴 **High** | 🛡️ **Reliability** | **Confidence: High** | **Status: New**

**What:** `Win.ini` and `Color.ini` are loaded outside the per-file recovery used for custom profiles. File reads and profile-directory enumeration are synchronous and can throw. The async window initialization has no catch; the dispatcher handler logs but does not mark the exception handled.

**Where:** `IniProfileStore.cs:36-64` under `LoadProfilesAsync()`; `IniProfileStore.cs:114-172` under built-in loaders; `IniDocument.cs:13-24` under `Load()`; `MainWindow.xaml.cs:74-101` under `OnLoaded()`; `App.xaml.cs:160-165` under `OnDispatcherUnhandledException()`.

**Why it matters:** A transient exclusive lock, ACL change, damaged directory, or unavailable redirected AppData can prevent the entire app—including input cleanup and tray diagnostics—from launching. Custom profiles degrade individually, so the built-in asymmetry is surprising.

**How to reproduce:** Hold `Win.ini` or `Color.ini` open with `FileShare.None`, deny read access, or make the Profiles directory non-enumerable, then launch.

**Recommended fix:** Isolate each built-in load. Preserve the unreadable source file, log contextual diagnostics, start with in-memory defaults, and show one nonfatal warning. Guard directory enumeration separately. A configurable storage root/filesystem seam is needed to test these cases safely.

**Fix complexity:** M  
**Risk of change:** Medium

**Suggested tests:** Independent failures for `Win.ini`, `Color.ini`, Profiles enumeration, and one custom file; assert startup continues, files remain untouched, and diagnostics identify the exact source.

### F-009 — Color disable/exit does not restore hardware baseline

> 🟠 **Medium** | 🛡️ **Reliability** | **Confidence: High** | **Status: New**

**What:** The service calls a disabled neutral profile a “restore,” but it never captures the gamma ramp or NVIDIA DVC state that existed before sWinShortcuts wrote hardware. Disabling writes an identity/default-derived ramp and NVAPI level 0; exiting while enabled only unloads NVAPI and leaves the app's settings active.

**Where:** `ProfileActivationService.cs:276-334` under `BuildDisplayColorPlan()` and `ApplyColorPlan()`; `ProfileActivationService.cs:120-151` under `StopAsync()`; `NvidiaColorControlService.cs:67-89` under `BuildGammaRamp()`; `NvidiaColorControlService.cs:229-236` under `ConvertPercentToNvLevel()`; `NvidiaColorControlService.cs:341-360` under `Dispose()`.

**Why it matters:** Users who opt into color control cannot reliably return to an ICC/Night Light/third-party calibration or an earlier NVIDIA Control Panel value. The application also leaves global display state changed after exit. Microsoft documents undefined interaction with HDR/calibration and warns that even a TRUE result can mean the requested ramp was silently refused.

**How to reproduce:** Apply a non-neutral ICC/gamma or DVC value, enable a profile, then disable it or exit. Compare the post-action state with the exact pre-application state; it is not restored.

**Recommended fix:** Capture an exact per-display baseline before the first successful write and restore it on disable, stop, and partial-failure rollback. Invalidate/re-capture on topology or modeset changes. If reliable capture is unavailable, explicitly document the limitation and migrate calibration use cases toward WCS/ICC or supported vendor APIs; gate HDR.

**Fix complexity:** L  
**Risk of change:** High

**Suggested tests:** Baseline capture/restore for enable→disable, profile switch, app stop, modeset, and partial apply failure; hardware smoke matrix for ICC, Night Light, HDR, and NVCP DVC.

### F-010 — Canceled shutdown abandons active color work

> 🟠 **Medium** | 🛡️ **Reliability** / 🧵 **Concurrency** | **Confidence: High** | **Status: New**

**What:** `StopAsync()` cancels the channel reader and waits with the caller's token, but an in-flight synchronous display/NVAPI call has no cancellation path. If the wait is canceled, shutdown catches the exception and continues stopping/nulling state while the worker remains live.

**Where:** `ProfileActivationService.cs:120-151` under `StopAsync()`; `ProfileActivationService.cs:184-240` under the worker and `ProcessForegroundChange()`.

**Why it matters:** When the native call finally returns, the abandoned worker can activate/deactivate input, update the tray, or call color services after input stop and container disposal. App shutdown currently bounds host stop, making this race realistic on slow/hung hardware.

**How to reproduce:** Block a fake color `Apply()`, call `StopAsync()` with a short cancellation token, let stop return, then unblock the fake and observe post-stop side effects.

**Recommended fix:** Introduce a stopping generation checked before every worker side effect, retain and observe the worker task until completion, and make late native completion side-effect-free. Do not dispose worker-owned state until termination is confirmed; isolate unbounded native calls if a hard shutdown bound is required.

**Fix complexity:** M  
**Risk of change:** Medium

**Suggested tests:** Blocked apply plus canceled stop, late completion, repeated stop, and container disposal; assert no activation/tray/color calls occur after stopping begins.

### F-011 — Shared mapping targets are not reference-counted

> 🟠 **Medium** | 🐞 **Bug** | **Confidence: High** | **Status: New**

**What:** Combined override ownership is recorded per source key, but output state is emitted directly per source. If two source keys map to the same target, each sends target DOWN and the first source released sends target UP even though the second remains held.

**Where:** `InputHookService.cs:1510-1531` on key-up; `InputHookService.cs:1582-1611` on key-down; `InputHookService.cs:3542-3574` and `InputHookService.cs:3625-3648` in forced release paths.

**Why it matters:** Many games/actions treat the shared target as released prematurely. Forced right-click/profile teardown can also emit redundant or incorrectly ordered UPs.

**How to reproduce:** Configure `A→X` and `B→X`; hold A, hold B, release A. X receives UP although B is still physically down.

**Recommended fix:** Maintain target-key reference counts under `_combinedOverridesLock`. Emit DOWN only on `0→1` and UP only on `1→0`. Centralize all normal and forced release paths through the same ownership function.

**Fix complexity:** M  
**Risk of change:** Medium

**Suggested tests:** Two and three sources to one target, every release order, right-click-only combinations, profile switch, stop, and failed target DOWN.

### F-012 — Live Caps Lock edits can leave injected state stuck

> 🟠 **Medium** | 🐞 **Bug** | **Confidence: High** | **Status: New**

**What:** `HandleCapsLock()` checks the current settings before processing recorded key-up state. If Hold or Remap is disabled/changed while Caps is physically held, the eventual UP bypasses release. `ReleaseCapsState()` only forces Caps OFF when current settings still say Hold; otherwise it sends an unrelated LeftShift UP.

**Where:** `InputHookService.cs:1624-1703` under `HandleCapsLock()`; `InputHookService.cs:1737-1765` under `ReleaseCapsState()`.

**Why it matters:** A live settings edit can leave Caps Lock forced on or a remapped key logically down until another recovery boundary. That can corrupt typing or game input outside the profile.

**How to reproduce:** In Hold mode, press Caps, disable/change the mode before releasing, then release. Repeat in Remap mode while changing the target or disabling the feature.

**Recommended fix:** Release recorded state before consulting current enable/mode gates. If `_capsShiftEngaged` is true, always perform its matching Caps-off action. Release `_capsRemappedKey` by the recorded key on disable, retarget, profile change, and physical UP.

**Fix complexity:** S-M  
**Risk of change:** Medium

**Suggested tests:** Mutate enabled/mode/target between DOWN and UP; assert exact paired release, including profile change, stop, and global-versus-active Caps settings.

### F-013 — Failed key-up injection is forgotten

> 🟠 **Medium** | 🛡️ **Reliability** | **Confidence: Med** | **Status: New**

**What:** `SendKey()` returns `void` and only logs when `SendInput` returns zero. Multiple release paths remove or clear ownership before/without knowing whether the UP succeeded.

**Where:** `InputHookService.cs:3453-3491` under `SendKey()`; `InputHookService.cs:1518-1529` combined release; `InputHookService.cs:1696-1700` Caps release; `InputHookService.cs:3628-3678` forced/transient releases; `InputHookService.cs:2564-2610` Auto-Run cleanup.

**Why it matters:** `SendInput` can return zero when UIPI blocks injection, without identifying UIPI as the cause. If a DOWN succeeded in an equal-integrity context and focus moves to a higher-integrity target before UP, the app can forget a failed release and lose its no-stuck-key recovery record.

**How to reproduce:** Deterministically use a native adapter that fails selected UP calls. A live approximation is an unelevated instance injecting DOWN, followed by a switch to an elevated foreground before release.

**Recommended fix:** Return structured injection outcomes. Retain failed-UP ownership in a bounded recovery set and retry when a permissible desktop/context returns, while suppressing duplicate DOWN. Integrate this with the ordered injector proposed in F-002.

**Fix complexity:** M-L  
**Risk of change:** High

**Suggested tests:** Fail each UP once/permanently; assert ownership retention, bounded retry, no duplicate DOWN, and final cleanup on profile/session/stop boundaries.

### F-014 — Failed autosave permanently clears dirty state

> 🟠 **Medium** | 🛡️ **Reliability** | **Confidence: High** | **Status: New**

**What:** `SaveIfDirtyAsync()` removes a profile from `_dirty` before persistence. `SaveProfileInternalAsync()` catches and displays the exception but does not report failure or requeue the profile. Exit flush therefore treats the edit as handled.

**Where:** `MainViewModel.cs:440-489` under `QueueAutoSave()`, `DebouncedSaveAsync()`, and `SaveIfDirtyAsync()`; `MainViewModel.cs:496-524` under `FlushPendingSavesAsync()` and `SaveProfileInternalAsync()`.

**Why it matters:** A transient file lock, disk error, or antivirus interference loses the latest edit unless the user changes another field. Shutdown can report a completed flush while the data was never saved.

**How to reproduce:** Use a store fake that fails once; trigger autosave; recover the store; call `FlushPendingSavesAsync()`. No retry occurs.

**Recommended fix:** Remove dirty state only after confirmed success, or atomically re-add it on failure. Return a save result and add bounded retry/backoff for transient I/O; preserve edit-during-save generations so an older success cannot clear newer changes.

**Fix complexity:** S-M  
**Risk of change:** Medium

**Suggested tests:** Fail-once, persistent failure, edit during save/retry, concurrent flush, and shutdown after storage recovery.

### F-015 — Profile deletion mutates memory before durable delete

> 🟠 **Medium** | 🛡️ **Reliability** | **Confidence: High** | **Status: New**

**What:** The manager removes the profile and rebuilds its snapshot before deleting the INI. If `File.Delete` throws, the manager no longer owns the profile, the removal event is never raised, and the UI command does not catch the exception.

**Where:** `ProfileManager.cs:143-175` under `RemoveProfileAsync()`; `IniProfileStore.cs:90-111` under `DeleteProfileAsync()`; `MainViewModel.cs:144-153` under `RemoveProfileAsync()`.

**Why it matters:** The UI and manager can disagree, later autosaves fail as “not managed,” the file remains on disk, and restart resurrects the deleted profile.

**How to reproduce:** Hold the profile INI open against deletion or deny delete permission, then click Remove.

**Recommended fix:** Delete durably before mutating the list, or roll back list/snapshot state on failure. Cancel any pending autosave only after the delete transaction succeeds, and surface an actionable error from the command.

**Fix complexity:** S  
**Risk of change:** Low

**Suggested tests:** Locked-file rollback, event suppression, pending autosave, successful retry, and restart consistency.

### F-016 — Settings Cancel and save failure are not transactional

> 🟠 **Medium** | 🐞 **Bug** / 📘 **Docs/UX** | **Confidence: High** | **Status: New**

**What:** Debug logging, hook watchdog, and Advanced Mode mutate live services in property setters. Clicking Cancel or closing the dialog performs no rollback. Conversely, Save swallows every INI exception, closes with `DialogResult=true`, and leaves users believing changes persisted.

**Where:** `SettingsViewModel.cs:48-90` in the three live-setting properties; `SettingsWindow.xaml.cs:67-100` under `SaveIni()` and `OnSaveClick()`; `SettingsWindow.xaml:65-67` on the Cancel button; `MainWindow.xaml.cs:243-253` under `SettingsButton_Click()`.

**Why it matters:** Cancel can still release Advanced-Mode-gated input state or disable hook recovery. A read-only/locked settings file appears to save successfully and then silently reverts on restart.

**How to reproduce:** Toggle Advanced Mode or Hook Watchdog and click Cancel; inspect the live service. Separately make `sWinShortcuts.ini` read-only, click Save, close/restart, and compare state.

**Recommended fix:** Use a side-effect-free draft VM and apply live/service/startup changes only after all persistence and OS changes succeed. If live preview is required, snapshot and roll back on Cancel, title-bar close, or any save/apply failure. Make `SaveIni()` return/throw a result and keep the dialog open with a clear error.

**Fix complexity:** S-M  
**Risk of change:** Low

**Suggested tests:** Save, Cancel, title-bar close, INI failure, and startup-task failure must each leave service state and persisted state in a defined, matching condition.

### F-017 — Inline executable edit bypasses validation

> 🟠 **Medium** | 🐞 **Bug** / 📘 **Input Validation** | **Confidence: High** | **Status: New**

**What:** The profile editor exposes an immediately updating executable `TextBox`. Its setter only trims and mutates the model. Extension and duplicate-executable checks exist in the Modify dialog path, while normal manager save validates only duplicate profile names.

**Where:** `MainWindow.xaml:290-303` on the Executable editor; `ProfileViewModel.cs:91-104` under `Executable`; `MainViewModel.cs:255-307` under Modify validation; `ProfileManager.cs:247-266` under `SaveProfileAsync()`.

**Why it matters:** Users can persist an empty/non-`.exe` target or duplicate another normalized executable. Such profiles never activate or are shadowed by list order, creating hard-to-diagnose behavior.

**How to reproduce:** Directly type `notepad` or another profile's executable into the inline field and wait for autosave.

**Recommended fix:** Make the inline field read-only and use the validated Modify flow, or centralize normalization, `.exe` policy, non-empty checks, and duplicate detection in `ProfileManager.SaveProfileAsync()` so every caller is protected. Roll the VM back to the prior valid value on rejection.

**Fix complexity:** S-M  
**Risk of change:** Low-Medium

**Suggested tests:** Empty, whitespace, non-`.exe`, path-qualified, case-only, dotted, and duplicate normalized executable values through direct manager save and VM edit.

### F-018 — Color snapshots do not synchronize value mutation

> 🟠 **Medium** | 🧵 **Concurrency** | **Confidence: High** | **Status: New**

**What:** `ColorSettings.SnapshotProfiles()` locks and deep-copies mutable `DisplayColorProfile` objects, but the UI setters mutate those same objects without taking that lock. The lock protects dictionary structure, not the fields it claims to snapshot atomically.

**Where:** `ColorSettings.cs:17-47` under `SnapshotProfiles()`; `ColorSettings.cs:49-81` under profile access; `DisplayColorSettingsViewModel.cs:74-154` in live field setters; `ProfileActivationService.cs:242-255` in plan construction; `IniProfileStore.cs:541-559` in serialization.

**Why it matters:** A foreground change or autosave can capture a mixture of pre/post-edit values, especially during multi-field reset or coordinated edits. On a 32-bit process, unsynchronized `double` gamma access also lacks the atomicity guarantee assumed here.

**How to reproduce:** Coordinate a reset/series of slider writes with repeated snapshots using barriers; without a mutation seam, a stress loop can expose mixed tuples probabilistically.

**Recommended fix:** Use immutable `DisplayColorProfile` values with copy-on-write replacement under `_sync`, or provide model update methods that copy/mutate under the same lock and stop exposing internal mutable instances.

**Fix complexity:** M  
**Risk of change:** Medium

**Suggested tests:** Barrier-controlled snapshot versus multi-field update must yield wholly before or wholly after tuples; stress plan building and INI serialization during edits.

### F-019 — Legacy test graph, near-EOS runtime, non-reproducible restore

> 🟠 **Medium** | 📦 **Dependencies** | **Confidence: High** | **Status: New**

**What:** The test project pins Legacy xUnit v2 packages whose resolved graph contains two High-advisory packages. The application targets .NET 8, uses `LangVersion=latest`, and has no SDK pin or package lock. Several direct packages are behind current stable releases.

**Where:** `Tests.csproj:3-18` in target/language/package references; `sWinShortcuts.csproj:3-20` in target/language/package references; absent `global.json` and `packages.lock.json`.

**Why it matters:** The advisories affect test/developer processes rather than the shipped app, so production exposure is limited, but security gates remain red: `System.Net.Http 4.3.0` is affected below 4.3.4 and `System.Text.RegularExpressions 4.3.0` below 4.3.1. NuGet traces both through `NETStandard.Library 1.6.1` from `xunit.extensibility.* 2.6.1`. .NET 8 is in maintenance and reaches end of support on 2026-11-10. Unpinned SDK/language and unlocked transitive graphs reduce build reproducibility. [HTTP advisory](https://github.com/advisories/GHSA-7jgj-8wvc-jh57), [Regex advisory](https://github.com/advisories/GHSA-cmhx-cq75-c4mj), [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy), [NuGet lock-file guidance](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files)

**How to reproduce:** Run `dotnet list .\Tests\Tests.csproj package --vulnerable --include-transitive`, `--deprecated --include-transitive`, and `--outdated --include-transitive`.

**Recommended fix:** Migrate to `xunit.v3`, current test SDK/runner, and then update production packages in isolated increments. Plan a .NET 10 LTS migration before November. Add `global.json` with an intentional roll-forward policy, avoid `LangVersion=latest` unless deliberately required, generate lock files, and enforce `restore --locked-mode` in CI.

**Fix complexity:** M  
**Risk of change:** Medium

**Suggested tests:** Preserve discovery of all `105` cases in CLI and Visual Studio; full Debug/Release suite on the pinned SDK; rerun vulnerable/deprecated/outdated audits; clean-machine locked restore.

### F-020 — Critical hook state machines lack behavior tests

> 🟠 **Medium** | 🧪 **Testing** | **Confidence: High** | **Status: New**

**What:** `InputHookService` is 3,763 lines of timers, hooks, queues, native calls, and shared state. Direct tests cover only the pure watchdog decision. There are no deterministic tests for callbacks, combined mappings, Caps state, Alt+Mouse, hold-breath, Auto-Run/Anti-AFK runtime, injection failures, foreground generations, or stop races.

**Where:** `InputHookWatchdogTests.cs:6-19` explicitly limits coverage to `DecideWatchdogAction()` and says Win32 plumbing is untested; runtime implementation throughout `InputHookService.cs`; Auto-Run/Anti-AFK tests in `IniProfileStoreIntegrationTests.cs:181-259` cover serialization only.

**Why it matters:** Regressions here can suppress physical input, inject into the wrong process, leave keys held, or cause Windows to remove a hook. The current green suite cannot detect F-001, F-002, F-011, F-012, or F-013.

**How to reproduce:** Search test call sites for `KeyboardCallback`, `MouseCallback`, combined/Caps runtime handlers, or native injection; none exist outside the watchdog decision seam.

**Recommended fix:** Extract native operations behind an interface, isolate state transitions into deterministic internal components, and inject a fake clock/barriers. Model key ownership explicitly and test invariants rather than private implementation details. Keep a small Windows-only manual/automated smoke layer for actual hook installation.

**Fix complexity:** L  
**Risk of change:** Low for test seams; Medium for extraction

**Suggested tests:** Every DOWN has one eventual UP; stale generations cannot affect replacement runs; callback returns under blocking native fake; overlapping mappings; live setting edits; focus/session changes; queue failure; stop/dispose interleavings.

### F-021 — Integration tests modify real user profiles

> 🟠 **Medium** | 🧪 **Testing** / 🛡️ **Reliability** | **Confidence: High** | **Status: New**

**What:** `IniProfileStoreIntegrationTests` intentionally constructs the production store rooted in `%APPDATA%\sWinShortcuts`. Cleanup blocks with `.Wait()` and suppresses all errors.

**Where:** `IniProfileStoreIntegrationTests.cs:10-37` in class setup/disposal; `IniProfileStore.cs:23-34` in the default constructor.

**Why it matters:** Tests can race a running app, enumerate a user's malformed profiles, fail due to real ACLs/locks, or leave test INIs behind. The first sandboxed review run failed 12 cases with `UnauthorizedAccessException`, demonstrating environment coupling. Cleanup failures are invisible.

**How to reproduce:** Run the suite with AppData write access denied or with the app/profile files locked, then inspect the user's Profiles directory.

**Recommended fix:** Add an internal root-directory constructor or filesystem abstraction. Give each test a unique temporary root, use deterministic async cleanup, and fail visibly if cleanup cannot complete. Keep real-AppData coverage as an explicit opt-in smoke category only.

**Fix complexity:** M  
**Risk of change:** Low

**Suggested tests:** Parallel isolated stores, cleanup after assertion failure, read-only root, locked file, cancellation, and proof that the real application directory remains unchanged.

### F-022 — Solution and repository automation omit tests

> 🟠 **Medium** | ⚙️ **Config/DevOps** / 🧪 **Testing** | **Confidence: High** | **Status: New**

**What:** The solution includes only the application project. There is no CI definition, so a standard solution build/test path can succeed without discovering any tests, advisory scan, or release gate.

**Where:** `sWinShortcuts.sln:1-24` lists only `sWinShortcuts.csproj`; `Tests.csproj:1-25` exists outside the solution; no tracked `.github`, GitLab CI, Azure Pipelines, Jenkins, or equivalent configuration.

**Why it matters:** Regressions and vulnerable dependency changes can land without automated feedback. There is also no reproducible signed release, artifact checksum, SBOM, or provenance trail for an application that installs global hooks and may run elevated.

**How to reproduce:** Run `dotnet sln .\sWinShortcuts.sln list`; only the application appears.

**Recommended fix:** Add the test project to the solution. Add Windows CI for locked restore, Release build, all tests, vulnerable/deprecated package checks, and artifact generation. Pin workflow actions by commit SHA with minimum permissions; make the workflow required. Add signing/SBOM/provenance when a release channel is defined.

**Fix complexity:** S-M  
**Risk of change:** Low

**Suggested tests:** Deliberately fail one test and confirm `dotnet test .\sWinShortcuts.sln` and CI fail; reject a test dependency advisory; verify artifact hash/signature generation.

### F-023 — WinEvent unhook runs on the wrong thread

> 🟡 **Low** | 🛡️ **Reliability** | **Confidence: High** | **Status: New**

**What:** `ForegroundWatcher.Start()` installs the WinEvent hook on the startup/dispatcher thread. Normal application exit deliberately runs host `StopAsync()` on a pool thread. `Stop()` calls `UnhookWinEvent` there, ignores its Boolean result, and clears the handle even on failure.

**Where:** `ForegroundWatcher.cs:17-57` under `Start()` and `Stop()`; `App.xaml.cs:91-104` under exit host stop.

**Why it matters:** Microsoft requires `UnhookWinEvent` on the same thread that installed the hook and states that a cross-thread call fails. Clearing the handle prevents retry from the owning thread. Current impact is bounded because the process exits soon afterward, but host restart, prolonged shutdown, or future lifecycle reuse would retain callbacks unexpectedly. [Microsoft `UnhookWinEvent` documentation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-unhookwinevent)

**How to reproduce:** Record install/uninstall thread IDs behind a native seam; normal exit uses different IDs and the real API returns false.

**Recommended fix:** Own the WinEvent hook on one message-loop thread and marshal both install/uninstall there. Check the result and preserve the handle on failure. Avoid depending on the current synchronous profile load to keep `Start()` on a dispatcher-pumped thread.

**Fix complexity:** M  
**Risk of change:** Medium

**Suggested tests:** Install/uninstall thread identity, failed-unhook handle retention, repeated start/stop, callback suppression after successful stop, and failed startup cleanup.

### F-024 — Malformed INI fields silently become defaults

> 🟡 **Low** | 🛡️ **Reliability** | **Confidence: High** | **Status: New**

**What:** Lines without `=` are skipped and invalid booleans, numbers, enums, or keys quietly return defaults. The profile-load diagnostic logs only thrown exceptions, so most corruption never produces the promised warning and can later be overwritten by autosave.

**Where:** `IniDocument.cs:13-63` under `Load()`; `IniExtensions.cs:10-81` in typed getters; `IniProfileStore.cs:46-61` in custom-file recovery.

**Why it matters:** A typo or partial corruption can reset one feature silently while the rest of the profile loads. The next save normalizes the default back to disk, reducing recoverability.

**How to reproduce:** Put an invalid Caps enum, key name, Boolean, or numeric value in a profile; load and save it. No field-level warning is emitted, and the invalid value becomes a default.

**Recommended fix:** Add schema-aware parsing that records file, section, key, and reason for invalid fields. Preserve the source and consider a read-only/degraded state until the user acknowledges repair. Add an explicit `SchemaVersion` and ordered migrations.

**Fix complexity:** M  
**Risk of change:** Medium

**Suggested tests:** Invalid values for every typed getter, truncated lines, duplicate keys/sections, warning aggregation, and no destructive rewrite before acknowledgement.

### F-025 — Release build has a 163-warning baseline

> 🟡 **Low** | 🧹 **Maintainability** / ⚙️ **Config/DevOps** | **Confidence: High** | **Status: New**

**What:** The reviewed Release build passes with `163` warnings, predominantly duplicate `CA1416` platform warnings from the WPF temporary project and final project. The initial sandboxed restore also produced `NU1900`; the later authorized NuGet audit succeeded.

**Where:** Windows-only calls across `InputHookService.cs`, `DisplayService.cs`, `SystemTrayService.cs`, `StartupService.cs`, `ProfileActivationService.cs`, and `NvidiaColorControlService.cs`; target configuration at `sWinShortcuts.csproj:3-15`; assembly metadata at `AssemblyInfo.cs:1-13`.

**Why it matters:** A large accepted baseline hides new actionable diagnostics and prevents a practical warnings-as-errors gate. Most warnings are platform-intent noise, but they should be resolved precisely rather than globally suppressed.

**How to reproduce:** Run `dotnet build .\sWinShortcuts.csproj -c Release --no-incremental`.

**Recommended fix:** Declare the supported Windows platform/version at assembly/project level, choose an explicit Windows TFM minimum matching supported Windows 10/11, then address remaining warnings individually. In CI, make restore advisory-feed failure explicit and progressively enforce warnings-as-errors for new warnings.

**Fix complexity:** S  
**Risk of change:** Low

**Suggested tests:** Clean Release rebuild of both generated WPF and final projects; confirm warning count reaches zero or a reviewed narrow baseline and CI fails on a new warning.

## Deprecated/Outdated Patterns

| Pattern/API | Location | Modern alternative | Migration notes and risks |
|---|---|---|---|
| Legacy xUnit v2 package graph | `Tests.csproj:13-18` | `xunit.v3`, current test SDK and runner | NuGet explicitly marks v2 Legacy. Migrate discovery/attributes in a dedicated change and preserve all 105 cases. |
| .NET 8 in maintenance; EOS 2026-11-10 | `sWinShortcuts.csproj:5`; `Tests.csproj:4` | .NET 10 LTS | Validate WPF/WinForms, source-generated MVVM, WMI, P/Invoke, and deployment behavior; do not combine with hook architecture changes. |
| `LangVersion=latest` without SDK pin | `sWinShortcuts.csproj:10`; `Tests.csproj:9` | Target-framework default language version plus `global.json` | Pin an SDK/roll-forward policy first so local and CI compilers agree. |
| Obsolete `SelectedDisplayId` remains emitted | `ColorSettings.cs:13-15`; `IniProfileStore.cs:408-413`; `IniProfileStore.cs:544-550` | Explicit schema migration, then stop writing legacy key | Continue reading during a compatibility window; stop writing only after supported upgrade paths are tested. |
| INI format inferred by section presence/pipe field count | `IniProfileStore.cs:198-348`; `IniProfileStore.cs:398-443` | `SchemaVersion` plus ordered, fixture-tested migrations | Add version without breaking existing files; migrate in memory and preserve source on failure. |
| Synchronous disk I/O wrapped in completed `Task` | `IniProfileStore.cs:36-111`; `IniDocument.cs:13-24`; `IniDocument.cs:145-173` | Honest synchronous API or serialized async persistence queue | Moving I/O off the dispatcher requires immutable save snapshots and sequencing with autosave/delete. |
| `SetDeviceGammaRamp` for application color control | `NvidiaColorControlService.cs:67-139` | Prefer WCS/ICC for calibration; use supported OS/vendor APIs for other effects | Microsoft strongly recommends against this API, notes global state, HDR/calibration undefined behavior, and unreliable success. A migration changes product behavior and needs hardware UX design. |
| Hard-coded NVAPI QueryInterface IDs/signatures | `NvidiaColorControlService.cs:373-448` | Public supported NVAPI surface or an isolated capability/version adapter | DVC is not clearly represented in the current public reference. Require every delegate, fail closed, and test driver-version capability before use. |
| Runtime P/Invoke via `DllImport` | `NativeMethods.cs:36-270`; NVAPI declarations | `LibraryImport` source generation where compatible | Migrate stable Win32 signatures incrementally; dynamic NVAPI delegates and some marshalling cases may remain runtime-bound. |

## Dead Code and Cleanup Opportunities

| Item | Location | Why unused/suspicious | Safe removal steps | Risk |
|---|---|---|---|---|
| `MouseButtonBindingViewModel` and commented debug statements | `MouseButtonBindingViewModel.cs:7-59` | No repository references outside its own file; current Alt-Mouse UI uses `AltMouseBindingEntryViewModel`. | Remove file, build, and exercise Alt-Mouse binding edits. | Low |
| `AvailableKeysConverter` plus resource | `AvailableKeysConverter.cs:11-46`; `MainWindow.xaml:33` | Resource is declared but no `StaticResource AvailableKeysConverter` use exists. | Remove XAML resource then class; compile XAML. | Low |
| `BooleanNegationConverter` / `NotConverter` | `BooleanNegationConverter.cs:7-26`; `MainWindow.xaml:29` | Declared but no `StaticResource NotConverter` use exists. | Remove declaration/class after one final repository search; compile XAML. | Low |
| Unused tray APIs | `ISystemTrayService.cs:8-14`; `SystemTrayService.cs:50-87` | `ShowBalloon` and `SetIcon` have no production call sites; only fakes implement them. | Remove interface methods, implementations, and fake members together. | Low |
| Three unused image/icon assets | `swinshortcuts_officia2l.png`; `swinshortcuts_official.png`; `swinshortcuts_official.ico` | Only `Icon.ico` is referenced by project, XAML, and tray loading. | Confirm packaging expectations, then remove unused binaries. | Low |
| `AddProfileDialogOptions.IsProfileNameReadOnly` | `AddProfileDialog.xaml.cs:209-214`; `DialogService.cs:12-31` | Never read; both callers pass `false`. | Remove parameter or implement the intended read-only behavior. | Low |
| `DisplayColorProfile.ResetToDefaults()` | `ColorSettings.cs:115-121` | No call sites; VM reset duplicates the assignments. | Either route reset through a model update seam or remove after tests. | Low |
| Unused `WaitForAsync` test helpers | `ProfileActivationServiceDeduplicationTests.cs:46-60`; `ProfileActivationServiceShutdownTests.cs:42-56` | Defined but fixed `Task.Delay(100)` waits are used instead. | Replace sleeps with deterministic signals/helpers, then remove any remainder. | Low |
| Unused whole-desktop gamma helper/interop | `NvidiaColorControlService.cs:62-65`; `NvidiaColorControlService.cs:112-139`; `NativeMethods.cs:102-106` | `TryApplyGammaRamp()` has no caller, making its null-device `GetDC/ReleaseDC` branch unreachable. | Remove helper, unreachable branch, and P/Invokes after confirming no planned whole-desktop call. | Low |
| Per-button timers not disposed | `InputHookService.cs:68-76`; nested state near `InputHookService.cs:3738-3745`; dispose near `InputHookService.cs:1119-1128` | Five button timers survive service disposal; singleton/process lifetime hides the leak in normal use. | Dispose timers and clear callbacks during `Dispose()`; add repeated create/dispose test. | Low |
| Explorer COM RCWs rely on GC | `ProcessLauncher.cs:91-147` | Repeated de-elevated launches create ShellWindows/item/desktop RCWs with no explicit release. | Add carefully scoped `Marshal.FinalReleaseComObject` in `finally` only for locally acquired RCWs. | Medium |
| Unused `System.Reflection` import | `ProcessLauncher.cs:4` | No reflection APIs are used. | Remove import and build. | None |
| Superseded review/plan files in repository root | `FABLE.md`; `codex_review.md`; `FABLE_Plan.md`; `INPUT_SLEEP_Plan.md`; `AUTORUN_ANTIAFK_ADVANCED_Plan.md`; `INJECTOR_MIGRATION_Plan.md` | Several describe old branches, dirty trees, fixed findings, or old test counts and can be mistaken for current instructions. | Move to `docs/history` with commit/date/status banners, or remove after preserving needed decisions in maintained docs. | Low-Medium |

## Security Posture Notes

**Authentication and authorization:** Not applicable in the web/API sense; there are no accounts, sessions, remote endpoints, or multi-user data APIs. The relevant authorization boundary is Windows integrity level. The app can run elevated, install hooks, register a HIGHEST task, and launch child processes, so local configuration integrity must be treated as security-sensitive.

**Secrets handling:** No embedded credentials, tokens, private keys, or tracked secret files were found. Launcher arguments are persisted in plaintext (`IniProfileStore.cs:573-579`), so users should not place tokens/passwords there. Crash/profile logs include local paths and exception text (`App.xaml.cs:167-179`; `IniProfileStore.cs:55-60`); support bundles may therefore contain identifying workstation information.

**Input validation:** There is no SQL/template/HTTP/file-upload surface. The important inputs are INI files, executable paths/arguments, profile names, and native event data. Atomic INI replacement and path sanitization are positives, but reserved-name classification, inline executable validation, silent typed fallbacks, and privileged launcher trust need correction.

**Privilege handling:** De-elevation failure correctly fails closed in `ProcessLauncher.cs:17-31`, and current code uses absolute system-tool paths and corrected task quoting. Those protections do not address the writable task target or user-writable admin-launch policy in F-004/F-005.

**Dependencies and supply chain:** Production NuGet audit is clean as of the review date. Test dependencies contain two High advisories and a Legacy framework. No CI, locked restore, source mapping, signing, installer ACL enforcement, SBOM, checksums, or artifact provenance is present.

## Most Critical to Fix

1. **F-001 — Decouple input switching from color work.** Why now: the wrong profile can remain active in another foreground application during normal focus changes. Expected outcome: every app boundary immediately releases old input state regardless of display latency/failure.
2. **F-002 — Remove synchronous injection from hook callbacks.** Why now: Windows can silently remove slow hooks, and the code already documents near-timeout stalls. Expected outcome: bounded callback latency and stable system input under hostile/slow foreign hooks.
3. **F-003 — Bound NVAPI enumeration.** Why now: a single ordinary error or missing delegate can make the worker infinite and stop profile activation. Expected outcome: fast fail-closed color degradation with input switching still operational.
4. **F-004 — Refuse HIGHEST task targets in writable locations.** Why now: portable/dev installs can become durable local elevation paths. Expected outcome: elevated autostart exists only for an ACL-protected installed binary.
5. **F-005 — Remove user-writable admin-launch policy.** Why now: an elevated app currently acts as a confused deputy for tampered `Win.ini`. Expected outcome: privileged launches require an admin-protected allowlist or narrow broker authorization.
6. **F-006 — Stop hot-plug refresh from touching disabled displays.** Why now: routine monitor changes can overwrite calibration without opt-in. Expected outcome: disabled/new displays remain hardware-untouched.
7. **F-007 — Introduce immutable profile kind/identity.** Why now: one reserved custom name can overwrite built-in state. Expected outcome: display-name edits and malformed files cannot change persistence destination or deletion privileges.
8. **F-008 — Make built-in profile failures nonfatal.** Why now: a single locked/unreadable INI prevents all functionality. Expected outcome: preserved source files, defaults in memory, and actionable diagnostics instead of startup termination.
9. **F-009/F-010 — Define color ownership and shutdown.** Why now: the app neither restores exact baseline state nor safely contains late native completion. Expected outcome: reversible color changes and no post-stop side effects.
10. **F-020/F-022 — Establish hook tests and CI before invasive fixes.** Why now: the highest-risk changes otherwise have no regression gate. Expected outcome: deterministic key-ownership/state tests plus required Release build/test/advisory checks.

## Next Steps

### 7-day plan: quick wins and safety nets

| Priority | Action | Expected result |
|---|---|---|
| 1 | Fix F-003 with terminal-error handling, delegate completeness, and enumeration cap. | Removes an infinite-loop path with a small, low-risk patch. |
| 2 | Split `NotifyMasterEnabledChanged()` into UI-only notification and explicit hardware action (F-006). | Hot-plug no longer writes disabled displays. |
| 3 | Reject/suffix reserved custom names before classification and add fixtures (F-007). | Built-in files cannot be clobbered by custom profile names. |
| 4 | Requeue dirty profiles after failed save and roll back failed deletes (F-014/F-015). | Transient I/O no longer silently loses edits or corrupts manager/UI state. |
| 5 | Make Settings apply/save transactional and centralize executable validation (F-016/F-017). | UI behavior matches Save/Cancel and invalid targets cannot persist. |
| 6 | Isolate persistence tests to temporary roots; add `Tests.csproj` to the solution. | Tests are hermetic and standard solution commands discover them. |
| 7 | Add a Windows CI baseline for Release build, 105 tests, production/test advisory checks, and deprecated packages. | Every subsequent high-risk fix has an automated gate. |

### 14-day plan: stabilization

| Priority | Action | Expected result |
|---|---|---|
| 1 | Implement a fast foreground input-transition path separate from coalesced color work (F-001). | No stale mappings cross application boundaries. |
| 2 | Route all hook-originated injection through one ordered dedicated injector (F-002/F-013). | Hook callbacks return promptly and injection failures remain recoverable. |
| 3 | Add target refcounts and recorded-state-first Caps release (F-011/F-012). | Overlapping mappings and live edits preserve key ownership. |
| 4 | Add stop generations and late-completion guards to the foreground/color worker (F-010). | Shutdown cannot race disposed/stopped services. |
| 5 | Make color profiles immutable/copy-on-write under one synchronization boundary (F-018). | Plans and saved settings are coherent snapshots. |
| 6 | Add deterministic hook/state tests using native adapters, fake clock, and barriers (F-020). | Concurrency and ownership regressions become reproducible. |

### 30-day plan: hardening and release readiness

| Priority | Action | Expected result |
|---|---|---|
| 1 | Ship an installer to an ACL-protected location; gate HIGHEST task creation on verified path security (F-004). | Elevated persistence cannot execute user-replaceable code. |
| 2 | Redesign privileged launching around an admin-protected allowlist or narrow broker (F-005). | User-writable preferences cannot authorize admin execution. |
| 3 | Design exact color baseline capture/restore or migrate supported scenarios toward WCS/ICC/vendor APIs (F-009). | Color changes become reversible and HDR/calibration behavior is explicit. |
| 4 | Migrate to .NET 10 LTS and xUnit v3; pin SDK, lock packages, and enforce locked restore (F-019). | Supported, reproducible, advisory-clean toolchain. |
| 5 | Add application manifest/privilege documentation, code signing, SBOM, checksums, and release provenance. | Users and operators can verify integrity and understand elevation behavior. |
| 6 | Resolve CA1416 warning noise and enable a no-new-warnings policy (F-025). | Build diagnostics become actionable. |
| 7 | Run Windows hardware/integrity smoke matrices: NVIDIA/non-NVIDIA, ICC/HDR, elevated/unelevated targets, session lock/resume, hot-plug, and slow foreign hooks. | Static findings are validated against real platform behavior before release. |
