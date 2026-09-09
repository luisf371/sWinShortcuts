# Active project notes

- Configuration intentionally supports only the current format: app-level toggle/startup keys, `Win.ini`, current binding sections, five-field color rows, and current Caps Lock values. Do not restore readers for legacy `Color.ini`, per-button AltMouse, `RightMouse`, or old Caps aliases without an explicit migration decision.
- `IniDocument.Save` uses `File.Replace` first, with an `UnauthorizedAccessException` fallback to `File.Move(..., overwrite: true)` for restricted Windows hosts. Keep sibling temp-file cleanup on failures.
- `ObservableCollection.Clear()` is unsafe for binding rows that own handlers: it emits `Reset` without the removed items. Bulk removal must use the existing per-entry removal path.
- WPF style triggers cannot override local attributes. For profile-kind tab visibility, use the proven local `Visibility` binding pattern; disabled tab headers need `ToolTipService.ShowOnDisabled="True"`. UI Automation is the non-pixel verification route; do not take screen captures.
- `Services/Input/` contains the split input pipeline. Preserve `_profileLock` → feature-lock → executor-enqueue ordering, never take a feature lock on the executor worker, and keep previously-recorded UP releases unconditional. Auto-Run activation and background human-input edges require live HWND/PID ownership checks; cached foreground identity alone is unsafe during watcher lag.
- `CapsLockMode.DoubleNormal` teardown may enqueue only its guarded second tap after disposal begins; execution must decide whether it is needed after the first tap's acknowledgement. Do not replace this with per-press heap allocations.

# 2026-08-26 (Update checks)

- `UpdateCheckService` is the only network client. It checks GitHub's latest-release endpoint with redirects disabled, and release URLs belong in `Utilities/GitHubUrls.cs`.
- Update checks are opt-in through `[App] CheckForUpdates`; only literal `true` enables them. `dev` builds must make no request. In CI, `BuildInfo.Number` is numeric, so service tests must set `CurrentBuildNumber` explicitly.
- Open update links through the existing fail-closed `ProcessLauncher` de-elevation path using absolute `%WINDIR%\explorer.exe`; direct `UseShellExecute` could open the browser elevated. Recheck `Enabled` inside the dispatcher callback so an in-flight response cannot show a banner after opt-out.

# 2026-08-27 (Build and release)

- The CI workflow uses `${{ github.run_number }}` for both `-p:BuildNumber` and `build-N` tags. Numbers naturally skip runs that are PR-only or fail; reruns keep the original number.
- Build metadata generation must run before compile even when only `BuildNumber` changes, register its generated file in `FileWrites`, and skip `*_wpftmp.csproj` to avoid WPF temporary-project collisions.
- The release flow RID-builds `Tests/Tests.csproj`, tests with `--no-build`, and publishes with `--no-build`. Package scans after the RID build need `--no-restore`, otherwise they can overwrite the RID assets and make publish fail with `NETSDK1047`.

# 2026-08-29 (AMD vibrance)

- GPU vendor routing is based on DXGI 1.1: join `DXGI_OUTPUT_DESC.DeviceName` to `Screen.DeviceName` and classify `DXGI_ADAPTER_DESC1.VendorId`. Conflicting or missing mappings fail closed. AMD is only `0x1002`; `0x1022` remains unknown.
- AMD control uses one serialized ADL2 context. Match `AdapterInfo.strDisplayName` to `\\.\DISPLAYn`, flush driver data after a successful saturation change, and refresh the ADL context under its lock after topology changes. The unmatched single-display fallback is permitted only after Windows has already classified that display as AMD.
- Keep gamma in `WindowsGammaService`, NVAPI-only behavior in `NvidiaColorControlService`, and routing/order in `CompositeColorControlService`. Preserve the gamma-first `ColorApplyOutcome` retry contract.
- Clamp stepped ADL saturation values to the driver-reported default rather than the minimum; reject a step larger than the full supported range.

# 2026-08-29 (memory cleanup)

- Removed superseded plans, historical review loops, branch/commit references, and obsolete test totals. Keep this file to durable constraints and current implementation gotchas; project structure and broad conventions live in `AGENTS.md`.

# 2026-08-30 (Anti-AFK background and forced modes)

- `AntiAfkSendMode` is persisted in `[AntiAfk] SendMode` and must be copied by `ProfilePersistenceSnapshot`; missing or invalid values fall back to `Foreground`.
- Background/Forced posting retains the last activated profile window and revalidates its PID before every DOWN. Snapshot the retained target once per tick so gating and dispatch use the same profile, and recheck `ProfileInputGenerationIsCurrent()` before every posted step.
- Retained Anti-AFK targets release on removal, identity change, master-disable, hard deactivation, session teardown, or stop. Anti-AFK setting edits do not release the target because mode and interval are read live.
- Anti-AFK and Auto-Run arbitrate each posted tap through `TryBeginAntiAfkTap`/`EndAntiAfkTap`; always release that latch in `finally`.
- PR 21 conflict resolution: merge `origin/main` by keeping main's cleaned `memory.md` as the base and appending only durable Anti-AFK notes. Release verification after the merge: `dotnet build .\\sWinShortcuts.csproj -c Release --no-incremental` completed with 0 warnings/errors; `dotnet test .\\Tests\\Tests.csproj -c Release --no-restore` passed 551/551.
- PR 21 review fixes: resolve a same-PID `GW_CHILD` before retaining the Anti-AFK posting target; reject taps while a retiring Background Auto-Run worker is alive; recapture the settled active target on session unlock/connect; and recheck all live guards after Auto-Run arbitration before posting DOWN. The four focused regression tests failed before their fixes and pass afterward (47/47 combined runtime tests).
- Final Anti-AFK guard order is blocking PID validation first, then `CanPostBackgroundStep` immediately before DOWN; reversing them leaves a stale-DOWN race during the native lookup. Final Release verification: build completed with 0 warnings/errors, full tests passed 555/555, and the four new regressions passed in 5 consecutive runs.
- Forced/Background Anti-AFK uses `PostMessage`, so Windows UIPI rejects delivery from medium-integrity sWinShortcuts to a high-integrity game with Win32 error 5. Capture `GetLastWin32Error()` immediately after the failed post, report the integrity mismatch once per retained target, and call successful delivery "queued" because it does not prove the game processed the message.
- Anti-AFK runtime diagnostics log the captured HWND/PID and report invalidation when that exact window is destroyed or reused. The delivery-diagnostics regression plus the full Release suite passed 556/556; use an elevated build for high-integrity games.
- Live validation with a High-integrity game confirmed all three modes: Forced and Background queued WASD to the retained window while unfocused, and Foreground fired through SendInput while active. A normal Medium-integrity launch still fails with UIPI error 5, so match the target's integrity level.

# 2026-08-31 (Debug logging review)

- Debug-log conventions: subsystem/service entries carry a `[Tag]` (`[Input]`, `[Color]`, `[Settings]`, `[UI]`, `[Launcher]`); input state-machine entries stay untagged prose. Release wording is "release requested" — injected UPs are queued to the executor or signaled to the Background worker, not completed on return. Rapid Fire's per-press cadence line says "press started" so it cannot be confused with the sticky-toggle "armed" line.
- `AntiAfkTarget` is a positional record, so `==` is value equality (Profile compares by reference): every CAS-result comparison guarding an Anti-AFK removal/invalidation log must use `ReferenceEquals`, or a racing same-valued recapture reads as a removal that never happened.
- `SendInput` failure facts exist only at the `WindowsInputSender` boundary (inserted-event count + last error captured immediately after the call; UIPI blocking is not reported). `IInputSender`'s bare bool lacks those native details, so the executor/Rapid Fire layers keep no duplicate bool-failure logging.
- The debug-logging toggle is order-sensitive with the logger gate: enablement flips `IsEnabled` before logging the entry, disablement logs before flipping; INI hydration and dialog-cancel rollback go through `SetEnableDebugLoggingProgrammatically`/`RollBackEnableDebugLogging` so they never claim "via settings".
- Hot-path diagnostic helpers must take raw values, not pre-built interpolated descriptions — an argument string is built on every call, including successes with logging disabled. `WindowsInputSender.SendInputLogged` takes the key/direction or virtual key plus a `SendInputKind` and constructs the description only after a short count, inside the `IsEnabled` branch.
- State-machine `Log(string)` helpers cannot defer interpolation: callback-reachable Auto-Run/Rapid Fire diagnostics must check `ILoggerService.IsEnabled` before constructing the message. The disabled Rapid Fire release regression uses `GC.GetAllocatedBytesForCurrentThread` to keep that path allocation-free.

# 2026-09-05 (Codebase review)

- `AGENTS.md` still describes the former monolithic input service. Review current input behavior in `Services/Input/` and use the lock ordering documented above; do not infer current ownership or timer behavior from the old code-map counts.
- Review regressions reproduced two unresolved boundaries: Background Auto-Run `SprintActivation.Press` can lose its UP on cancellation, and a pre-rename autosave snapshot can persist its old `Name` after rename succeeds. Fixes must preserve paired releases and reconcile queued snapshot identity, respectively.
- Additional fake-driven review probes confirmed: inactive Auto-Run owners survive master-disable, Anti-AFK can post after disable during its third PID lookup, and the default color editor writes an inactive preset directly to hardware. Recheck owner invalidation, place DOWN guards after all native preparation, and keep hardware writes coordinated with the activation plan.
- Native review constraints: obtain the desktop shell through `FindWindowSW(SWC_DESKTOP)` rather than ordinary ShellWindows enumeration; normalize display identifiers and compare them exactly; restrict system driver DLL searches as the AMD loader already does.
- NVAPI DLLs are driver-installed runtime dependencies, not bundled assets. .NET 10 single-file publishing still searches beside the executable for default P/Invokes; explicit `DefaultDllImportSearchPaths(DllImportSearchPath.System32)` excludes that directory (Microsoft: `dotnet/core/compatibility/interop/10.0/native-library-search`).

# 2026-09-05 (Review fix planning)

- Requested implementation-plan scope is review findings 1–9 and 11–14 only; elevated-startup location protection (#10), unrelated dead-code removal, and CI cleanup are excluded. The Auto-Run lock/native-work change (#13) follows the input correctness fixes (#1–3) so their release/ownership regressions protect the refactor.
- The staged fix plan is `docs/superpowers/plans/2026-09-05-review-fixes.md` (locally saved; existing `Docs/` ignore rule applies). Native cleanup must be bounded inside the AMD/NVIDIA services, not by moving the whole host disposal off the UI thread; the tray has creating-thread affinity.
- Planning checks: `schtasks /Query /TN <fresh-nonexistent-name> /HRESULT` returned `0x80070002` without startup mutation. `IniDocument.Load` currently uses `File.Exists`, which can conflate absence with unreadable/directory paths; the settings-load fix must distinguish those before treating defaults as a valid baseline.

# 2026-09-05 (Cleanup planning)

- Cleanup is a separate, behavior-preserving plan from the review fixes. Count WPF bindings/resources, generated MVVM members, native ABI declarations, and reflection-based test helpers as consumers; no-C#-caller results alone do not justify deletion. Keep current-format persistence safety tests and useful fake/service boundaries.

# 2026-09-06 (Cleanup plan)

- Closed cleanup list C1–C8 is saved in `docs/superpowers/plans/2026-09-05-code-cleanup.md`; start with orphaned UI artifacts. No package, native ABI, artwork, or local-directory purge is planned. Retiring the unused `SuppressOriginalWhileAltIsHeld` projections also retires exactly eight tests of those projections, not runtime suppression coverage.

# 2026-09-06 (Review fixes)

- Desktop-user COM launching resolves ShellWindows.FindWindowSW(SWC_DESKTOP), then IServiceProvider → IShellBrowser → IShellView → folder Application. Keep full SDK vtable layouts including IOleWindow slots and release each acquired RCW once; never final-release cast aliases. Read-only STA/MTA regressions reproduced the enumeration failure and pass with the supported lookup.
- Startup task queries use /HRESULT: only completed 0 means present and 0x80070002 means absent. Timeout/access/other errors abort before any task/Run-key mutation; registry errors propagate. A fresh nonexistent task query confirmed 0x80070002 without changing startup configuration.
- Queued profile snapshots reconcile only Name to the managed profile inside ProfileManager's gate; retain captured feature values so a rename cannot be undone by a delayed autosave.
- Settings hydration reads/parses one IniDocument before live setters; only true missing-file exceptions count as defaults. IsIniLoaded gates editing, Save and rollback baseline capture, independently of startup readiness.
- Production color editors notify the activation worker; standalone direct writes additionally require the edited variant still be active when execution occurs. Do not bypass active/game/forced-preview precedence with a second color writer.
- NVIDIA handle matching uses exact names after the leading \\.\ display prefix; unmatched single-handle fallback requires known NVIDIA ownership. Both NVAPI imports explicitly use System32-only DLL search.
- Crosshair events publish desired held state under the configuration gate; dispatcher callbacks read the latest policy, and ungated profiles clear held state. Keep RMB callbacks enqueue-only and keep assets/window work outside the gate.
- Background Auto-Run retains an activation-captured run/target through worker retirement, even before target resolution finishes: a suppressed physical W-up is already a release obligation. Native result accounting must carry movement vs sprint explicitly because SprintKey=W is valid. Keep epoch invalidation before reset and account successful DOWN before detach.
- AMD/NVIDIA disposal closes admission before either lock acquisition, schedules one serialized cleanup, and waits only 100 ms. Timed-out/hung cleanup stays rooted and deferred; never unload in-flight native code or retry cleanup synchronously. Host disposal remains on the UI thread.
- Anti-AFK validates captured HWND/PID again after AttachThreadInput, for both DOWN and UP, then performs the final live DOWN guard. Attachment can block while a window is destroyed/reused; rejected posts still detach and restore keyboard state.

# 2026-09-07 (Review remediation)

- Auto-Run-consumed key-UP still reconciles prior Caps, combined-remap and launcher ownership through `ReleaseOwnedKeyUp`; W handoff performs that cleanup earlier. Foreground Press sprint on W restores movement through the existing generation/foreground-guarded DOWN, while prior UP remains unconditional.
- Startup changes read both task and Run-key state before mutation. `/Create /F` replaces without pre-deleting; failed transitions query actual state before compensating and report restoration failure plus known/unknown resulting state. Keep startup tests on fake delegates, never real task/registry writes.
- Log retention trims to 75% of the same threshold to leave batch headroom; the helper also serves crash.log. Temp-file regressions cover repeated append/trim cycles and newest-record retention.
- Accepted scope: executable replacement in a writable install location when running elevated is a known deployment risk. Do not require certificates, Program Files installation, or executable-path lockdown as remediation without a new user decision.
- Color-plan dedup is invalidated before native writes; attempted enabled displays retain separate worker-owned restore obligations until an Applied restore. Do not clear that set from StopAsync while an uncancelable worker could still be running. Preview hotkeys toggle from the visible preview variant; nonfinite gamma defaults to 1.0 before finite clamping.
- A hide for a never-created crosshair must remain asynchronous: Application.Exit can block its dispatcher before HasShutdownStarted becomes true.
- App-settings reads and complete read/modify/write transactions share the AppSettings queue. Capture WPF values before enqueuing, and include AppSettings.FlushAsync in the existing bounded exit flush; UI continuations must not be needed to finish storage. Disable Settings editing during Save so captured values and live previews cannot diverge.
- Combined profile identity edits validate/persist a captured proposed name+executable together, publishing identity only on success. Queued autosave snapshots reconcile both identity fields while keeping their captured feature values.
- Key deserialization must accept only defined keys with a Windows virtual-key mapping. Undefined numeric values and WPF-only sentinel keys otherwise form suppressed remaps that emit nothing. Keep bare digits mapped to D0-D9, aliases and defined numeric key values compatible.
- Startup Advanced Mode inference requires a successful settings read with an absent key. On read failure, preserve live state and skip persistence; a later successful write must not replace an explicit preference with inferred defaults. The regression releases a temporary exclusive file lock from the error logger before any fallback write could run.
- SystemEvents captures the subscribing synchronization context and uses Send; display callbacks subscribed during WPF startup run on the hook-owning dispatcher. AMD/NVIDIA callbacks only mark pending invalidation atomically; the next apply refreshes caches under the native-operation lock, retaining events that arrive during in-flight work.
- DisplayService invalidation must not take the enumeration lock. GetDisplays consumes pending changes on its worker and publishes a cache only after an uninterrupted successful enumeration. Color editors use coalesced async snapshots on their owning context, discard stale/disposed results, retain rows after failures, and reuse the snapshot when switching presets; gated tests cover responsiveness without native displays.
- Successful Settings hydration must synchronize live watchdog/Advanced Mode/toggle-key services even when INI values equal fresh VM defaults. Keep normal UI notification deduplication and parse the whole snapshot before applying anything; repeated service assignments are idempotent, including an unchanged Rapid Fire toggle key.
- Optional logger construction performs no directory I/O. Create storage only inside caught nonempty worker/final-drain writes, trim after successful appends, and leave idle shutdown storage untouched; this lets profile-store inaccessible-storage recovery run and permits later writes when the directory becomes available.

# 2026-09-08 (PR 23 merge CI diagnosis)

- PR 23 head, its green synthetic merge, and main squash 6c96fba have identical trees. CI run 34299207336 failed only NvidiaColorControlServiceTests.Dispose_WhenNativeApplyBlocked_ReturnsBeforeNativeCleanup at the post-dispose Task.Run/500 ms WaitAsync (line 103); local full Release/RID suite passed 775/775. Setting DOTNET_PROCESSOR_COUNT=2 for the AMD/NVIDIA test classes reproduced that exact NVIDIA failure plus AMD's matching disposal timeout (45/47 passed). Their intentionally blocked pool work makes short queue-inclusive deadlines scheduling-sensitive; isolate blocking test work/probes on dedicated threads and retain release-order assertions instead of changing production disposal or merely increasing deadlines.
- CI setup-dotnet resolves channel 10.0 and global.json allows latestFeature: the green run used SDK 10.0.400/runtime 10.0.11, red used SDK 10.0.401/runtime 10.0.12 despite the same runner image. This drift is observed, not established as the failure cause.
- Local CI-test fix: both vendor test classes use a standard LongRunning TaskFactory with TaskScheduler.Default for synchronous blocked native calls and timed probes, including topology tests with the same scheduling dependency. Keep the 500 ms deadlines and cleanup/rejection assertions; production scheduling and disposal are unchanged.
- Verification: original AMD/NVIDIA constrained run failed 2/47 before the test-only change; afterward 5 consecutive constrained runs passed 47/47. Final Release/RID full suite passed 775/775 both normally and with DOTNET_PROCESSOR_COUNT=2; build had 0 warnings/errors and git diff --check passed.

# 2026-09-08 (Wheel plan revision)

- Wheel support is planned in `docs/superpowers/plans/2026-09-06-mouse-wheel-support.md`: existing Key Mapping sources/Right Click Only plus AltMouse tap targets, not a separate RMB feature. Preserve signed wheel distance, including fractional or multiple 120-unit increments.
- General InputTrigger wheel support requires feature-specific validation: hold-breath panic currently supports only keyboard/mouse buttons. Existing executor enqueue takes a lock; old blanket lock-free callback documentation does not describe queued gesture delivery.
- Plan review boundary: Background Auto-Run holds use PostMessage and are invisible to executor/OS key-state checks. Wheel busy-target protection covers executor-owned/OS-reported holds only; document posted-target overlap rather than claiming cross-transport ownership coordination.
- Deterministic actual-service wheel tests need clock/key-state delegates forwarded through an internal InputHookService constructor; a fake IInputSender alone still leaves native keyboard-state reads. Lifecycle checks must use production transitions, with test extensions reserved for initial setup.
- Native Start/reinstall/session resume cannot be proven by hook-free fixture worker restarts. Exercise shared production wheel resets and safe service transitions automatically; report actual hook/session integration separately as manual checks, including checks not run.
- Clarification to the constructor-test note: test extensions may also invoke shared production reset/seeding routines and clean up fixtures; the restriction is against duplicated lifecycle logic or direct field replacement standing in for a production transition.

# 2026-09-08 (Wheel implementation)

- Implementation branch `feat/mouse-wheel-mappings` starts at `807d3b7`; baseline Release build passed with 0 warnings/errors and full tests passed 773 with 2 existing desktop-dependent skips (775 total). Keep the reviewed plan under ignored `docs/superpowers/plans/2026-09-06-mouse-wheel-support.md` local.
- Wheel producer/executor share injected Stopwatch ticks; only worker-side native state checks gate busy targets. Preserve physical Alt during mapped output and keep Background Auto-Run posted holds outside executor ownership accounting.
- Wheel settings keep existing INI conventions: SetKey writes unassigned nullable targets as empty values (None also reads unassigned). Source-choice list notifications are presentation-only; ignore SelectableSources in mapping-change handlers to prevent repeated model publication/autosave during picker refreshes.
- InputExecutor tracks successful sends for held-target protection: a failed UP must retain its virtual-key hold bit. Guard completion belongs in the drain finally so cancellation, send failures and shutdown still refund accepted wheel reservations exactly once.
- Never reset `_pendingWheelTaps` during wheel epoch invalidation or lifecycle reset. Accepted stale commands keep their reservations until executor completion; only rejected admission refunds synchronously.

# 2026-09-09 (Review remediation loop)

- PR 24 uses `feat/mouse-wheel-mappings`; remediation starts at `7adb684`. Keep input foreground checks at worker-side DOWN admission, preserve global bindings and prior UP obligations, and treat Background Auto-Run's posted holds as a separate transport.
- Shared executor holds need stable owner identity: Caps typematic repeats must not create extra owners. A failed UP needs bounded recovery without clearing conservative busy-target state from an ambiguous native key-state read.
- Crosshair display-change notifications bypass unchanged-config/HWND dedup and enqueue the existing full apply; dispatch reads current desired state, and disposal detaches the static SystemEvents handler. Focused regression check passed 11/11.
- Composite color restoration tracks gamma/vibrance independently before native calls; retain pending components through failed, thrown or skipped attempts until an applied restore, while never-supported components remain non-retrying. A real AMD service with fake enumeration loss reproduced the missing restore; focused color tests passed 75/75 after correction.
- Profile-scoped executor DOWNs and Rapid Fire clicks validate live HWND/PID through the existing transport on workers, then recheck published state; global bindings and recorded UPs retain their admission rules. Test fixtures inject the transport through construction, and default construction is checked for native wiring.
- Executor holds use fixed feature-owner flags with combined mappings retaining their source counts. Failed or throwing UPs remain pending per virtual key, retry once at each worker/drain opportunity, and block new holders until recovery; Auto-Run Press sprint on W preserves its deliberate pulse/restore.
- Remediation verification: `dotnet test Tests/Tests.csproj -c Release --no-restore` passed 911 tests with 2 existing Explorer-dependent skips and no build/analyzer warnings. Independent input diff inspection and `git diff --check` found no issues; live hook/display/GPU behavior remains a manual check.
- Fresh review after green CI at `c0a6a1a` reproduced Auto-Run cancellation during worker PID lookup. Its commands use `ExpectedExecutable` rather than `ExpectedProfile`, so its own `CanExecute` must recheck lifecycle/generations and captured foreground-guard identity after native lookup; executor profile checks do not cover this producer.
- Auto-Run post-lookup revalidation passed four regressions that first failed for movement/sprint cancellation with and without focus publication, preserving an admitted W release. Focused checks passed 152/152; full Release tests passed 915 with 2 existing Explorer-dependent skips and no build/analyzer warnings.
