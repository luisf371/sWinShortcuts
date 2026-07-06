# Code Review: `fixupv2` (reviewed standalone — master ignored)

> xhigh-effort recall review of the entire `fixupv2` branch. Reviewed as current code on its own (master is stale and this branch replaces it). 2026-07-05.

**Architectural note that reframes several candidates:** the low-level keyboard/mouse hooks install on the WPF dispatcher thread (the startup `await` chain never actually yields, because profile loading is synchronous). So hook callbacks run *on the UI thread*, which means the scary-looking "hook iterates `Mappings` while the UI rebuilds it" races are actually serialized and safe — **but only by accident**. If profile load ever becomes truly async, the hooks move to a pool thread and those become real crashes.

## The 15 most severe (all CONFIRMED unless noted)

1. **Startup wipes your monitor calibration on every launch** — `Services/ProfileActivationService.cs:197` + `Services/NvidiaColorControlService.cs:46`. The initial foreground event builds a color plan of *disabled* default entries (50/50/1.0/50), which isn't equal to `ColorPlan.Empty`, so it applies — and `Apply` never checks `profile.IsEnabled`. Launching with all color features off still calls `SetDeviceGammaRamp` (identity ramp overwrites any loaded ICC profile / Night Light) and `NvAPI_DVC_SetLevel` (resets NVIDIA Control Panel vibrance). Every launch, every NVIDIA user.

2. **Autosave silently drops edits** — `ViewModels/MainViewModel.cs:366`. One shared `_saveCts` debounces *all* profiles. Edit profile A, then touch profile B within 500 ms, and A's pending save is cancelled and never re-queued. There's no shutdown flush, so A's changes are lost on exit.

3. **Renaming a profile can destroy another profile's file** — `ViewModels/MainViewModel.cs:266` + `Configuration/IniProfileStore.cs:493`. Rename only sets `Model.Name`; `DetermineProfilePath` keeps writing to the stale `SourcePath`. Create a new profile with the *old* name and it resolves to the same `.ini`, overwrites it, and then both profiles point at one file (delete-either removes both).

4. **Upgrade silently drops old right-mouse mappings** — `Configuration/IniProfileStore.cs:266`. Deserialization reads only the new `[KeyMappings]`/`[KeyMappingsOverrides]` sections; there's no fallback for the old `[RightMouse]`/`[RightMouseOverrides]` (AltMouse *did* get one). Since this branch replaces master, every upgrading user with right-mouse overrides loads with zero mappings, and the first autosave erases them permanently.

5. **Upgrade silently resets CapsLock "Hold" mode** — `Models/CapsLockSettings.cs:18` + `Configuration/IniProfileStore.cs:300`. `MomentaryShift` was renamed to `Hold`, so old INIs with `Mode=MomentaryShift` fail `Enum.TryParse`, fall back to `Normal`, then persist `Normal`. Config loss on upgrade.

6. **One exception permanently kills all profile switching** — `Services/ProfileActivationService.cs:109`. The channel worker's `await foreach` has no per-item try/catch. Any throw inside (a long tray tooltip, the dictionary race in #8, a color-apply failure) faults the worker task; nothing restarts it, so profiles stop activating and colors stop applying for the rest of the session while the app looks alive. This is the amplifier behind several other findings.

7. **App crashes on any autosave failure** — `ViewModels/MainViewModel.cs:390` + `Services/DialogService.cs:66`. The catch calls `ShowError` after `ConfigureAwait(false)`; `MessageBox.Show(Application.Current.MainWindow, …)` runs `VerifyAccess` on a pool thread and throws, escaping the `async void` `QueueAutoSave` (which catches only `OperationCanceledException`); `DispatcherUnhandledException` never sets `Handled` → process terminates.

8. **Color worker races the UI over a shared dictionary** — `Services/ProfileActivationService.cs:176`. The worker (pool thread) does `DisplayProfiles.TryGetValue` on the live `ColorSettings` instance while the UI thread inserts into it via `GetOrCreateProfile` (`ViewModels/ColorSettingsViewModel.cs:39`). No lock. A `TryGetValue` during a bucket resize throws → kills the unguarded worker (#6). Triggered by first-enable of color or a new monitor.

9. **Profiles for dotted exe names never activate** — `Services/ForegroundWatcher.cs:98` + `Services/ProfileManager.cs:178`. The watcher returns `Process.ProcessName` (already extensionless, e.g. `paint.net`), then `FindByExecutable` re-runs `GetFileNameWithoutExtension` → `paint`, which never equals the stored `paint.net`. The add-profile dialog *requires* a `.exe`, so there's no workaround. Any app with a dot in its base name silently never triggers its profile.

10. **Admin autostart is silently broken for normal install paths** — `Services/StartupService.cs:147`. `/TR` gets only one quote layer; schtasks strips it and stores the action unquoted. For any path with spaces (`C:\Program Files\…`, the default) the logon action resolves to `C:\Program`. schtasks only warns and exits 0, so the success check passes. Needs `/TR "\"{exe}\""`.

11. **Holding a launcher hotkey spawns dozens of processes** — `Services/InputHookService.cs:975`. `HandleWindowsLauncher` guards only `!isKeyDown`; the LL hook receives every typematic auto-repeat as a fresh `WM_KEYDOWN`, and each queues `LaunchProcess`. Hold Win+NumPad for a second → ~30 instances. Needs an "already handled while held" latch.

12. **Per-display vibrance can hit the wrong monitor** — `Services/NvidiaColorControlService.cs:170`. When `FindDisplayHandle` can't map a GDI name to an NvAPI handle (hybrid/Optimus laptop, name mismatch), it falls back to applying *that display's* level to **every** NVIDIA handle. Because the plan iterates displays sequentially including disabled ones (default 50 → level 0), the last unmappable entry clobbers every monitor's vibrance.

13. **Disabling a mapping while its key is held sticks the key down** — `Services/InputHookService.cs:617`. The key-up release sits *below* the guards at `:545` (`!IsEnabled`) and `:567` (`entry is null`). Hold Q→Shift (suppress), uncheck the feature or delete the row, release Q → the method returns before `_activeCombinedOverrides.Remove` + `SendKey(false)`, so Shift stays pressed system-wide until the next foreground change.

14. **Fast tap can emit the hold key instead of the tap key** — `Services/InputHookService.cs:438`. The Alt-mouse `HoldTimer.Change(threshold)` is armed one line *before* `HoldCallback` is assigned, and `Timer.Change(Infinite)` doesn't cancel an already-queued callback (there's no generation token). A stale callback from a previous press can win the new press's `ARMED→FIRED` CAS and inject the hold key immediately; the quick tap then sees `FIRED` and suppresses the tap key.

15. **Colors stay wrong after sleep/resume until you switch profiles** — `Services/ProfileActivationService.cs:124`. `_lastAppliedColorPlan` is never invalidated when the OS/driver resets gamma and DVC (resume from sleep, monitor replug, another app). The next foreground change rebuilds a byte-identical plan, `Equals` is true, and the needed re-apply is skipped. Compounded by `:127` recording the plan as applied even when `Apply` returned false.

## Also confirmed (correctness, below the top-15 cut)

- **Duplicate profile names on disk break every save** — `Services/ProfileManager.cs:160`: load never dedups names (taken from INI content, not filename); save throws on duplicates → error dialog (or crash via #7) on every edit. Can be *created* by the rename bug (#3).
- **`schtasks /Delete` swallows "Access denied"** — `Services/StartupService.cs:190` returns `true` unconditionally; an unelevated "disable startup" silently leaves the elevated HIGHEST task running → unwanted elevated autostart the user can't turn off from the UI.
- **All NvAPI failure logs are useless** — `Services/NvidiaColorControlService.cs:165,189,228,268,276,402…`: doubled braces `{{status}}` inside `$"…"` log the literal text `{status}`, so every NVAPI failure is undiagnosable.
- **Hot-plugged monitors never appear in color settings** — `ViewModels/ColorSettingsViewModel.cs:37`: the display list is snapshotted at VM construction; `IDisplayService` has no change event, so nothing rebuilds it until restart.
- **Windows on a monitor left/above the primary don't restore there** — `MainWindow.xaml.cs:131`: the guard rejects negative `Left/Top` and ignores `VirtualScreenLeft/Top`.
- **Partial hook-install failure leaks the installed hook** — `Services/InputHookService.cs:175`: `_isRunning` is set only after both hooks install, so the cleanup `Stop()` hits `if (!_isRunning) return;` and never unhooks the one that succeeded.
- **`Dispose` can throw / leave a key stuck at exit** — `Services/InputHookService.cs:259` (PLAUSIBLE): disposes the `ThreadLocal<Random>` while queued tap/hold-breath work items may still deref it → `ObjectDisposedException` on a pool thread during shutdown.
- **OS shutdown makes the app start hidden next time** — `MainWindow.xaml.cs:197`: forced close routes through `MinimizeToTray`, which persists `StartMinimized=true`; no `SessionEnding` path.
- **Gamma applied to the primary monitor on `CreateDC` failure** — `Services/NvidiaColorControlService.cs:99` (PLAUSIBLE): falls back to `GetDC(NULL)` and still returns success; needs a stale device name (monitor unplugged mid-plan).
- **`OnExit` truncation** — `App.xaml.cs:51` (PLAUSIBLE): `async void` + `await StopAsync` may not run the host-dispose continuation → skipped logger flush and a ghost tray icon.
- **Hold-breath fires for a suppressed Alt+right-click** — `Services/InputHookService.cs:328` (PLAUSIBLE, narrow): armed before `HandleAltMouse` decides to suppress.
- **Logger loses its final lines** — `Services/FileLoggerService.cs:92` (low): an `OperationCanceledException` from inside the retry-backoff `catch` skips the final flush.
- **Hand-edited INI key quirks** — `Utilities/KeySerializer.cs:27` (`TapKey=5` → `Key.Clear`) and `Utilities/KeyInteropUtilities.cs:16` (`Key.None` never treated as null); low severity, app never writes these itself.

## Checked and dismissed

Hook-vs-UI collection races on `CombinedMappings.Mappings` and `AltMouse.Bindings` (**refuted** — same-thread as above); two-window settings-INI race and the `EnableDebugLogging`/`StartMinimized` boolean round-trip (**refuted** — internally consistent, single UI thread); `IniDocument` mid-file `[Default]` and bare-filename `Save` (**refuted** — unreachable from current callers).

## Cleanup themes (correctness always outranks these)

Recurring, worth one pass each: executable-normalization is copied 4 ways (one disagreeing); color-settings serialization and the clamp helpers are duplicated verbatim; ~150 lines of rewrite-orphaned dead code (`MouseButtonBindingViewModel`, `AvailableKeysConverter`, `IniExtensions.Get/SetDouble`, `TryApplyGammaRamp`, `SendKey`'s dead `bypassHook` param); per-keystroke lock+alloc and unguarded `LogDebug($…)` in the hook hot path; sliders drive synchronous GDI+NvAPI per tick; and special-casing the built-in profiles by magic name string in ~28 places (mitigated only because the UI blocks renaming them).

## Suggested priority

The most impactful single fix is guarding the foreground worker loop (#6) and making it snapshot `DisplayProfiles` — that alone neutralizes the crash-to-dead-session path behind #7, #8, and the long-name tray throw. After that, the silent-data-loss trio (#2, #3, #4/#5) is what I'd prioritize.
