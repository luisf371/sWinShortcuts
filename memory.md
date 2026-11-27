# Lessons learned

### 2026-08-26
- `UpdateCheckService` is the only network client. It queries the GitHub latest-release endpoint with redirects disabled; release URLs live in `Utilities/GitHubUrls.cs`.
- Update checks are opt-in. Only a literal `true` in `[App] CheckForUpdates` enables them. Service tests must set `CurrentBuildNumber`, because CI build numbers are numeric.

### 2026-08-30
- Anti-AFK and Auto-Run serialize each posted tap through `TryBeginAntiAfkTap` and `EndAntiAfkTap`. Always release that latch in `finally`.
- `AntiAfkTarget` is a positional record, so `==` compares by value. Guard removal and invalidation logs with `ReferenceEquals`.
- `PostMessage` from a medium-integrity build to an elevated game fails with Win32 error 5. Capture `GetLastWin32Error` right after the post.

### 2026-08-31
- Hot-path log helpers take raw values and build the message only inside the `IsEnabled` branch, as `WindowsInputSender.SendInputLogged` does.

### 2026-09-05
- Before deleting a member, count XAML bindings, generated MVVM members, P/Invoke ABI declarations, and reflection-based test helpers as callers.
- NVAPI DLLs ship with the driver, not the app. On .NET 10 single-file, default P/Invokes still search beside the exe; `DefaultDllImportSearchPaths(System32)` excludes it. https://learn.microsoft.com/dotnet/core/compatibility/interop/10.0/native-library-search

### 2026-09-06
- AMD and NVIDIA disposal close admission, run one serialized cleanup, and wait 100 ms. Never unload native code that is still running.

### 2026-09-07
- Key deserialization accepts only defined keys that have a Windows virtual-key mapping. Other values would load as remaps that emit nothing.
- Running elevated from a writable folder allows exe replacement. This risk is accepted, so do not add certificate or install-location requirements without a new decision.

### 2026-09-08
- Vendor color tests run blocking native fakes and timed probes on a `LongRunning` `TaskScheduler.Default` thread. On `Task.Run`, the 500 ms deadlines flaked in CI.
- `InputExecutor.Enqueue` takes `_enqueueLock` although the header comment calls the hook path lock-free. Queued gesture delivery relies on it.
- A new `InputTrigger` kind needs per-feature validation. Hold-breath panic accepts only keyboard and mouse buttons, not wheel.
- Deterministic `InputHookService` tests need the internal constructor's clock and key-state delegates. A fake `IInputSender` alone still reads `GetAsyncKeyState`.
- Hook-free test fixtures cannot prove native Start, hook reinstall, or session resume. Report those as manual checks, including the ones not run.
- Test extensions may call production reset and seeding routines and clean up fixtures. Never duplicate lifecycle logic or replace fields where a production transition belongs.
- Wheel busy-target checks see executor-tracked and `GetAsyncKeyState` holds only. Background Auto-Run and Anti-AFK post with `PostMessage`, which they cannot see.
- Set `_keysDown` only after a successful send, so a failed UP keeps its hold bit. Complete guards in the drain `finally` to refund reservations once.
- Never reset `_pendingWheelTaps` on wheel epoch invalidation or lifecycle reset. Accepted taps refund only on executor completion.
- Mapping-change handlers must ignore per-row `SourceValidator` notifications. They fire on attach and detach and would republish the model and autosave.

### 2026-09-09
- AMD vibrance needs Adrenalin 25.3.1 or later. README states this in its requirements, troubleshooting, and FAQ, so update them together.

### 2026-09-11
- `ProfilePersistenceSnapshot` has one `Clone*` method per settings type. Copy new fields there, or queued autosaves write them back stale.
- A new `CrosshairSettings` field must also join the `CrosshairService` dedup, or live edits skip the redraw.
- Crosshair offsets are physical pixels added after centering in `ComputeOverlayBounds`. Centered or offset mode is kept per profile for the session.
- Crosshair offset toggles must match the applied profile and foreground generation. Advance the stored generation even on deduped applies.
- `CrosshairService.Stop` closes admission and publishes a hidden configuration. Overlay work stays `BeginInvoke`-only so app exit never blocks on the dispatcher.
- Never bind `Slider.Minimum` or `Maximum` to `x:Static` int constants. The deferred template load crashes at startup; use XAML literals.
- `Tests` is nested in the app project, whose default Page glob covers `Tests/**/*.xaml`. Link copied XAML fixtures as `*.xaml.testdata`.
- Elevated startup tasks are named `sWinShortcuts_AutoStart_<SID>`. Adopt or delete the legacy shared name only after its principal resolves to the current SID.
- Settings skips unchanged startup options, so an existing task keeps its old policy until the user saves startup off, then on, elevated.
- A solution `dotnet restore` that exited 1 with no diagnostics succeeded when rerun with `-m:1`.

### 2026-09-13
- Macro waits run on the macro worker, not inside `InputExecutor`, whose delays block shared input. Await `InputCommand.Completion` for each transition.
- F12 is the hard-coded emergency stop for macros (`MacroStateMachine.EMERGENCY_STOP_VK`), kept by user decision.
- Absolute `SendInput` cannot address a desktop larger than 65,536 pixels per axis, so `WindowsInputSender` rejects wider or taller ones.
- `MacroValidation.MaxSteps` is 1,000, a user decision. Validation, recording, insertion, and duplication all share it.
- Only the Caps 2x release tap whose press tap the executor sent may cross a recording pause. Enqueue, `ExecuteTap`, and `SendKey` each allow it.
- Macro cursor speed is 9 px per ms over an 8 ms interval, tuned from user testing. Faster needs closer points, not a shorter delay.
- The recorder writes a `MoveTo` before every mouse edge, including UP, and playback skips same-pixel moves. Keep release coordinates in any cleanup.

### 2026-09-14
- Headless WPF template tests have no presentation source, so `IsVisible` is always false. Assert `Visibility` and arranged bounds; materialize menus with `Measure` and `Arrange`.
- Macro menu items need a `HeaderTemplate` bound to `MenuItem.Foreground`. The implicit `TextBlock` style otherwise overrides the highlight color.
- The wait bar handles Enter only in the duration field and Escape anywhere in the bar, so Enter still activates a focused button.

### 2026-09-25
- Every Rapid Fire settings edit keeps the arm and only cancels the owner's burst. `InputHookService` and `InputFeatureHarness` duplicate this logic, so change both.
- If the suite hangs, rerun with `--blame-hang-timeout`. For a wedged testhost, collect `dotnet-dump collect --type Heap` and run `clrstack -all`.

### 2026-09-26
- Pass `foregroundDeparture: true` only from the profile switch, `DeactivateProfile`, and `ReleaseForegroundState`. Every other `ReleaseAllState` caller completes the Caps tap immediately.
- `Tests/InputHookServiceTestExtensions.cs` calls private `ReleaseAllState` by reflection with positional arguments. Adding a parameter breaks it with `TargetParameterCountException`.
- Caps 2x focus loss is fixed by blocking the accidental Win key, not by resending the closing tap on return. The user declined resending.
- A remapped Caps 2x press sets `RemapStateMachine.BlocksWinKey`. `CapsHoldWinKeyBlocker` then swallows that Win press, its repeats, and its UP; a preheld Win passes.
- Call `_winKeyBlocker.Handle` in `DispatchDecodedKeyboardEvent` right after `NotePhysicalKeyboardActivity`, before Auto-Run observation and `_macros.HandleKey`.
- Win-block debt must also be retired by `ObserveUnfiltered` in the `!featuresActive` branch and by `Reset` in `SeedAppTogglePhysicalState`, or Win stays stuck.
- A missed Caps UP can leave `BlocksWinKey` set; the flag clears at the next Caps press.
- `SendTap` passes held Shift, Ctrl, Alt, and Win through, so a tap sent during a held modifier reaches the game as a chord.

### 2026-09-27
- `HandleLeftButton` starts the real timer. Rapid Fire tests must wait with `SpinWait.SpinUntil(TimerState == TIMER_ARMED)` and assert timing with lower bounds only.

### 2026-10-05
- Profile-off graying sits on each page's `StackPanel` with `IsEnabled="{Binding CanEditContent}"`. A `TabControl` presents selected content outside its `TabItem`.

### 2026-11-02
- `GetAsyncKeyState` counts this app's injected keys while the hook skips them. `GestureChordStateMachine` tracks `_physicalAlt` separately from `_injectedAlt` for that reason.
- `AdoptHeldAltFromWindows` must always bump the wheel epoch. `ServiceBoundary_InvalidatesQueuedWheelAndRemainder("rederive")` fails otherwise.
- `RemapStateMachine.HandleCapsLock` has an open bug. It reads the Caps generation outside `_capsLock`, so a racing state release passes a suppressed Caps press to Windows.
- Its log line is "Caps down passed through; input state was released during the press". Fix by re-reading generation and settings under `_capsLock`.
- `NullLoggerService.Log` locks `Messages`, but cross-thread order is undefined. Assert that each line is present, not their sequence.

### 2026-11-04
- `InputTriggerSerializer` and `KeySerializer` accept comma lists, because `Enum.TryParse` does not reject them. Only `IniExtensions.TryGetKey` and `TryGetEnum` reject commas.
- Template tests parse XAML at runtime: rewrite namespaces to `;assembly=sWinShortcuts`, and find controls inside collapsed GroupBoxes with `LogicalTreeHelper`.
- Never clean `bin\Release\net10.0-windows\win-x64\publish`. The user runs the app from that folder and keeps backup exes in it.
- Never kill the app from a script. It may run elevated, and a hard kill skips its shutdown cleanup of colors and gamma.

### 2026-11-06
- New optional macro INI keys need no version bump, because older builds ignore unknown keys. An unknown step `Kind` is a malformed macro to them.

### 2026-11-07
- Run package scans with `--no-restore` after the RID build. `dotnet test -r win-x64 --no-restore` fails if the last restore had no RID.

### 2026-11-11
- Unreal reads a repeat-flagged W DOWN as a one-frame press. Background Auto-Run posts fresh W ticks for 500 ms after focus returns (`BG_W_REENGAGE_FRESH_MS`).

### 2026-11-16
- A swallowed low-level-hook W UP leaves the Windows key state stuck, so a background Auto-Run release must also inject a W UP (`_backgroundSwallowedWUp`).

### 2026-11-20
- Playback tests must wait for `{ SessionId: 1, Mode: Idle }`. A bare `Mode == Idle` returns before the worker starts.
- Exclusive pickers use `SegmentRadioButtonStyle` on `ToggleButton` controls. Radio-style buttons cannot be deselected and have no text search.
- A `ToolTip` bound with `StringFormat` shows the raw value, so use `<ToolTip ContentStringFormat=...>`. Hide closed-box content with a `ComboBoxItem` ancestor trigger.
- Git Bash `sed -i` rewrites CRLF files as LF. Disk line endings are mixed, so count CR bytes per file and prefer the Edit tool.

### 2026-11-21
- `NotifyProfileChanged` must invalidate the color owner and republish the latest foreground inside one `_publicationLock` block, or a removed profile's colors are re-armed.
- Track process exits through `WindowsProcessExitWatch`: `OpenProcess(SYNCHRONIZE)` plus a registered thread-pool wait. `Process.Exited` is not used.

### 2026-11-22
- `Views/MacrosView.xaml` can only `BasedOn` styles defined in that file, such as `MacroButton`. For app styles from `Styles.xaml`, repeat setters with `DynamicResource`.
- Keep the "Step N:" prefix in macro validation messages. `MacroViewModel.FindStepNumber` parses it to route the error to its row.
- A macro stops with "Physical input took over a macro key" when a pressed key is one the macro holds, such as strafing with D.

### 2026-11-23
- `[AutoRun] TriggerModifier` reads `Control+Alt` style values through `IniExtensions.GetModifierKeys` and writes them back with `SetModifierKeys` (`+` separated).
- `KeyCaptureBox` is code-only. Template tests parse XAML at runtime, and a BAML `UserControl` with `StaticResource` would fail outside the app tree.
- The key-capture gate runs in `KeyboardCallback` right after the injected-event filter and before the `!featuresActive` branch.
- `KeyCaptureBox.CaptureSource` is a process static. Its tests run in the non-parallel `KeyCaptureBoxCollection` and reset it in `Dispose`.
- WPF `ButtonBase` drops mouse capture before `OnClick`, so a `LostMouseCapture` cancel restarted the capture. While capturing, handle `PreviewMouseDown` over Assign and Clear.
- Keybinds keep their modifier side and match it exactly. Older files without `TriggerRightModifiers` or `ShortcutRightModifiers` load as left-sided.
- Auto-Run tests that re-press the trigger must clear its `_triggerKeyDownVk` latch by handling the UP, not only by observing physical events.
- Esc assigns a key only where `KeyCaptureBox.AcceptsEscape` is true, the macro step Key boxes. Every other key box uses Esc to cancel.

### 2026-11-25
- Layered windows pass clicks through pixels with zero alpha. The measure ruler uses a `#01000000` background to catch them. https://learn.microsoft.com/windows/win32/winmsg/window-features
- `ContextMenuItemStyle` has no `PART_Popup`, so any submenu needs its own template.
- `MainWindow.ExitCoreAsync` closes the window in its `finally`, so any check that can cancel Exit must return before that `try`.
- `CHANGELOG.md` uses dated headings and CI `build-N` release tags, not semantic versions. The csproj has no version, so there is nothing to bump.
- Configuration supports only the current format. Legacy `Color.ini`, `RightMouse`, and old Caps readers were removed on purpose. Do not restore them without a migration decision.
- `IniDocument.Save` tries `File.Replace` first and falls back to `File.Move` with overwrite on `UnauthorizedAccessException`. Keep its temp-file cleanup on failure.
- `InputHookService` serializes owner selection and publication under `_profileLock`. `InputExecutor` takes only `_enqueueLock`, so never take a feature lock on its worker.
- A teardown during a remapped Caps 2x press may enqueue only the guarded second tap. The executor sends it only after the first tap's acknowledgement.
- `git status` ignores an LF working copy of a `text eol=crlf` file such as `BUILD_WINDOWS.bat`. Check `git ls-files --eol`; restore by deleting and checking out.

### 2026-11-26
- With every option on, the Playback button has 18 px spare at 650 px. By user decision, new options stay out of `MacroViewModel.PlaybackSummary`.
- `MacroStateMachine` raises `Changed` on its worker right after each publish, so a test handler calling `GetMacroSession()` sees every published value exactly.
- Input hooks run on the UI dispatcher (`StartInputHookOnDispatcher`). A first WPF window show blocks all input about 55 ms, so prebuild overlays used during playback at `ApplicationIdle`.
