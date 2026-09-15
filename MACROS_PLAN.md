# Macros Implementation Plan

> For agentic workers: use superpowers:subagent-driven-development or superpowers:executing-plans to execute this plan task by task. Use the parent model for coding workers, use Serena when available, and keep shared input-runtime edits under one owner.

**Date:** 2026-09-13
**Status:** Feature implemented on `feat/macros` in PR #25. The editor at `f779947` passed hosted CI and independent review; the container, naming-dialog and enable-control follow-up below is in progress. `MACROS_PLAN_REVIEW.md` retains the original planning review history.
**Baseline inspected:** main, 0bf8192.
**Goal:** Add a Macros tab immediately after Advanced, supporting labeled macros, assigned shortcuts, an editable sequence of keyboard/mouse/wait actions, and Record/Stop assistance.
**Architecture:** Store macros with their owning profiles, publish detached runtime snapshots, and add a recorder and a playback coordinator to the existing input service. Reuse InputExecutor and WindowsInputSender for delivery and release accounting; macro waits run outside the shared executor.
**Tech stack:** Existing .NET 10 WPF, CommunityToolkit.Mvvm 8.4.2, Win32 low-level hooks/SendInput, INI persistence, and xUnit v3/manual fakes. No new package dependencies.
**Spec:** The scope and behavior sections in this root document are the design specification. Keep design and execution steps together here, as requested.
**Delivery authorization:** The user requested a commit and PR once implementation is complete. Create a feature branch, implement and validate, then commit/push/open the PR. Finalizing this plan does not trigger a commit, and merging is outside the requested delivery.

## Confirmed product decisions

| Decision | Final scope |
|---|---|
| Macro scope | Per application profile; shortcuts operate only in its settled foreground app. Keep the new tab in the custom-profile editor. |
| Playback | By default a fresh keyboard shortcut executes once. Optional per-macro ToggleMode repeats the sequence until the next fresh press of that same shortcut cancels it. Holding a shortcut never repeatedly toggles; initial playback still waits for key/modifier release. Other recognized shortcuts while busy stay consumed without restart or queue. No normal playback Stop button. |
| Coordinate system | Signed absolute physical screen pixels across the entire virtual desktop, including negative monitor origins. |
| Mouse recording | Record button edges, click locations, and wheel input, without recording free cursor motion or drag trajectories. Manual MoveTo and button DOWN/UP remain available. |
| Playback movement | Generate a quick path toward each required endpoint with an internal speed cap and slight path deviation. Land on the exact specified pixel. The speed cap determines the brief travel duration. |
| Shortcut types | Keyboard shortcuts only, including modifier combinations. An unassigned shortcut is allowed while editing. |
| Temporary cancellation | Include the user-permitted hard-coded F12 emergency stop during recording/playback for early troubleshooting. It is not a configurable app setting or a normal playback control; keep its binding in one constant for easy later removal. |

Implementation defaults: playback requires Advanced Mode; the editor remains available while playback is locked. Recording retains the originally requested Record/Stop button and starts after the Record button is released. Unwanted starting actions remain editable. Record appends a take after the selected row, or at the end, and never silently replaces existing steps.

No additional product decision blocks the plan. Internal movement constants, overlap handling, lifecycle cleanup, and persistence details are implementation choices described below.

## Global constraints and data contract

- Keep the existing .NET 10/WPF/MVVM/INI stack, native sender, executor completion, and manual fakes. Add no packages, scripting layer, standalone automation engine, or configurable Stop/movement settings.
- Macro settings default disabled with an empty array. New macros have a new nonempty GUID, label "New macro", disabled state, unassigned Key.None shortcut, no modifiers, and no steps. Duplicate creates a new GUID, disabled state, unassigned shortcut, and a detached copy of the steps.
- MacroDefinition owns ID, Label, IsEnabled, ToggleMode (default false), CancelOnMouseMovement (default false), ShortcutKey, ShortcutModifiers, and ordered Steps. MacroStep is a value with Kind, Key, optional MouseButton, X, Y, DurationMs, signed WheelDelta, and HorizontalWheel. Only the fields relevant to Kind are serialized. Published arrays are privately owned and never mutated after publication. The optional per-macro ToggleMode and CancelOnMouseMovement INI keys default to false when absent; explicitly malformed values retain source protection.
- MacroStepKind contains KeyPress, KeyDown, KeyUp, Wait, MouseClick, MouseDown, MouseUp, MoveTo, and MouseWheel. Keyboard actions are logical Windows keys using the existing KeySerializer/KeyInteropUtilities semantics; retaining scan metadata in the capture buffer does not promise a new scan-code or text-layout playback mode.
- Label length is 1-100 characters after trimming; CR/LF and other control characters are invalid. Duplicate labels are allowed because GUIDs carry identity. Profile macro IDs must be unique. Unknown enum values, unsupported keys, empty GUIDs, duplicate IDs, negative counts, and missing declared sections are format failures.
- Enforce 64 definitions/profile and 1,000 rows/definition. Durations use whole milliseconds from 0 through 3,600,000; 0 is automatic for KeyPress/MouseClick and an explicit zero for Wait. Wheel delta is a nonzero signed 16-bit value, preserving the hook's signed high word. Modifier masks accept only Ctrl/Alt/Shift/Win bits. Use checked long arithmetic for totals.
- Structural validity and playability are different: empty sequences, unassigned shortcuts, or unbalanced DOWN/UP drafts can be saved and edited, but are excluded from the runtime lookup. A duplicate keyboard DOWN is a repeat of an already-held key; a duplicate mouse DOWN or unmatched UP is an invalid draft. Composite KeyPress/MouseClick cannot target an input already held earlier in that macro. All held inputs must balance at the end of a runnable macro.
- Loading and saving use the same format validation. Distinguish absent optional values from present-but-malformed values; a malformed coordinate or duration cannot silently become 0. Add the smallest strict TryGet typed overloads in IniExtensions and source-presence access in IniDocument needed by macro loading, preserving existing feature readers.
- Existing rules remain: hook callbacks add no allocations/locks/waits; service awaits use ConfigureAwait(false); _profileLock precedes any feature lock and enqueue; the executor takes no feature lock. Test lifecycle behavior through production entry points and preserve already-admitted release obligations.

## UI/runtime handoff

Use the existing IInputHookService boundary, with no IMacroService wrapper:

~~~csharp
event EventHandler? MacroSessionChanged;
MacroSessionSnapshot GetMacroSession();
Task<MacroRecordingResult> RecordMacroAsync(
    Profile owner, Guid macroId, int availableRows,
    CancellationToken cancellationToken = default);
void StopMacroRecording();
~~~

- MacroSessionSnapshot is a detached read-only value containing session ID, owner profile/macro ID, mode, elapsed time, current row/event count, and an optional failure reason. Modes cover Idle, PreparingPlayback, WaitingForShortcutRelease, WaitingForPhysicalModifiers, Playing, PreparingRecording, Recording, Finishing, and Faulted. UI statuses can group preparation modes. Notifications only mean "re-query"; subscribers marshal asynchronously and re-read current status.
- RecordMacroAsync starts capture and completes once with a detached MacroRecordingResult after Stop/limit/interruption. The result contains session ID, owner/macro identity, finalized rows, end reason, and whether balancing releases were appended. StopMacroRecording only requests capture completion; it is not a playback command.
- MainViewModel owns the pending recording task and the captured destination/insertion index. Child VMs request recording through its existing composition; they do not independently retain service subscriptions. A recording result is applied once on the dispatcher to that exact still-managed macro, not to whatever is selected when completion arrives.
- Freeze mutations/deletion of the recording's destination until finalization is applied. Navigating away requests finalization and retains the result for its original destination; do not drop it because a view unloaded. Profile deletion awaits capture finalization before performing the existing delete flow.
- Route explicit tray/window Exit through one asynchronous pre-close operation while the dispatcher is alive: request recording completion, await MainViewModel's row application and pending-save flush, then set the existing allow-close flag and close. Repeated Exit requests share that operation. MainWindow.ExitFromTray/SystemTrayService call this path; App.OnExit remains a bounded fallback and must not synchronously wait for dispatcher-dependent recording application. Native hook loss/Stop finalizes the captured prefix on the worker. Forced termination and OS session teardown that ends the dispatcher have only best-effort unsaved-take retention; do not block or cancel OS shutdown to promise durability.
- PreparingPlayback/WaitingForShortcutRelease/WaitingForPhysicalModifiers/Playing/Finishing and PreparingRecording/Recording/Finishing share one admission slot. A failed start leaves the slot Idle with a visible reason; unresolved release obligations leave it Faulted and cannot permit another run. Explicit editor recording requests while busy report busy; keyboard activations while busy retain the specified ignored-pair behavior except that the active toggled macro's own shortcut requests cancellation.
- Compile new profile macro lookup/revision snapshots during ReconcileProfileSettings after UI model publication, not on the next keyboard callback. Cancel the affected run before accepting changed executable steps or ownership. Preserve the existing MainViewModel autosave snapshot flow and the suppression latches for keys whose DOWN was already consumed.
- Recompute runtime lookup and editor conflict results when their dependencies change: macro/profile bindings and enabled flags, global Windows Launcher bindings, and all three app-toggle setters (color, crosshair, Rapid Fire), including startup hydration and Settings rollback. Use the existing feature's actual match rules: app toggles reserve their base key across every modifier combination. Publish a new validation revision and notify the editor even when no profile changed. Preserve the saved assignment but exclude a conflicting macro from activation; cancel an affected pending/running macro before replacement and retain owed activation-key UP suppression.

## Scope and behavior

### Macros tab and editor

- Insert Macros between Advanced and Display, visible for custom profiles. Preserve the existing tab style and selected-tab coercion.
- At the app's existing 960-pixel width, use a compact macro selector/list above the editor rather than another wide sidebar.
- Provide a per-profile Enable macros checkbox, plus New, Duplicate, Delete, label editing, and per-macro enable/disable. Use a stable ID for runtime identity; renaming a label does not create a different macro.
- Assign shortcuts with the existing key catalog in a key picker plus Ctrl/Alt/Shift/Win checkboxes, including an Unassigned option. This first version has no press-to-capture shortcut mode: the user need not execute a chord to assign it, so global hooks cannot steal the assignment gesture or activate another feature during capture. Ordinary keyboard navigation remains available.
- Show ordered step rows with an action type and only that action's relevant fields. Provide insert, edit, duplicate, delete, and Move Up/Move Down; keyboard operation must cover every action.
- Provide Record and Stop recording, recording duration/action count, and clear Preparing/Recording/Playing/Finishing/Idle status. Stop recording remains operable while capture is active. Playback has a status indicator and a temporary F12 hint, without a playback Stop button.
- Disable sequence/selection changes while recording. Apply the finished take as one editor change so thousands of captured events do not create thousands of autosaves.
- Allow temporarily incomplete sequences as editable drafts. Invalid drafts never become runnable; show the offending row and reason. Existing autosave stores representable draft data and publishes only validated runtime data.
- Give shortcut conflicts a visible explanation: another macro in the same scope, reserved F12, an app-level toggle, or an existing enabled binding that consumes the same chord. Do not let two features silently fire from one assigned macro shortcut.
- When a tab/profile is left or the editor is disposed, finish an active recording into its original draft. A foreground change to the target app during recording does not count as leaving the editor.

### Action vocabulary

| Action | Editable data | Playback behavior |
|---|---|---|
| Key press | Key, automatic or explicit hold milliseconds | DOWN, timed hold, UP. |
| Key down / Key up | Key, including left/right modifiers | Explicit edges, allowing overlapping holds and chords. |
| Wait | Milliseconds | A cancellable delay outside the input executor. |
| Mouse click | Button, screen X/Y, automatic or explicit hold milliseconds | Finish the speed-capped curved move to the exact point, DOWN, timed hold, UP. |
| Mouse down / Mouse up | Left/right/middle/X1/X2 button | Explicit edges; MoveTo rows specify positions. |
| MoveTo | Screen X/Y | Speed-capped cursor travel with a slight path deviation, ending at the exact absolute pixel. |
| Mouse wheel | Signed delta and vertical/horizontal axis | Preserve Windows wheel distance, including fractional or multiple 120-unit deltas. |

Recording emits explicit edges, MoveTo rows at mouse actions, and Wait rows. It preserves modifier overlap, non-modifier key autorepeat, and the time between physical events; repeated already-held modifier DOWNs are omitted before timing/row accounting. Repeated keyboard DOWN is a repeat of one held key, not another owner requiring another UP. Do not collapse an overlapping sequence into simple taps.

Double-clicks can be two click actions separated by a Wait. Commands, scripts, clipboard/text automation, image recognition, conditions, nested macros, and background delivery are outside this first scope.

### Timing and game input

- Reuse the current keyboard transport, extended-key handling, injection marker, and error reporting. It currently uses virtual keys with a scan-code field; it does not set KEYEVENTF_SCANCODE.
- Automatic Key press uses the existing gesture hold range, 31-53 ms. Automatic Mouse click uses the existing Rapid Fire hold range, 10-20 ms. Expose the existing timing values through a small shared internal helper only where required to prevent duplicated policies.
- Explicit Wait and press-duration values are replayed without adding jitter. Users can lengthen a hold for an application that needs more time to register input. Generated cursor travel adds time: a raw DOWN/MoveTo/UP sequence can remain held longer than the recording's original gap. Do not shorten entered waits or exceed the speed cap to disguise that difference.
- Playback waits for the activating key and its modifiers to be physically released. Never inject modifier UPs to clear the launch chord.
- During playback, new physical modifiers pass through and defer further macro DOWN/move/wheel output until release; they do not cancel the run. This preserves a second Ctrl+F6 activation as an ignored chord and keeps Alt-Tab available. Already-owed UPs and cancellation cleanup proceed. The extra deferral can extend raw held steps, and a real modifier may affect a key already held in the target application; this feature does not isolate the application from concurrent physical input.
- Timing is best-effort Windows scheduling, not a real-time guarantee. Use the existing timer-resolution lifecycle; do not add another global timer-resolution owner.
- Cancel on foreground HWND/PID/generation changes and do not resume automatically. Already-admitted releases still complete after focus loss.
- Reusing SendInput preserves the established application behavior; it does not guarantee acceptance by every game or turn injected input into hardware input.

### Recording

- Reuse the installed keyboard and mouse hooks. Capture after injected-event rejection and before feature processing discards scan metadata/coordinates.
- Record only physical events. The existing filters reject injected input from this app and from other synthetic-input tools.
- Start/finalize on the existing hook-owning dispatcher. A fixed preallocated value buffer has one writer; other threads may only atomically request a stop.
- Keep the disabled recording path cheap. Under click-only recording, mouse moves retain the current early exit. Horizontal wheel is decoded for recording without broadening existing mapping behavior.
- Store raw timestamps/primitive values during capture; create WPF rows and calculate waits after Stop. No per-event strings, allocations, I/O, dispatcher posts, or locks are added to callbacks.
- Temporarily pause remaps and automatic input while recording, after draining their admitted output. Show Preparing until that drain completes; if it cannot complete within the existing bounded cleanup policy, report failure and do not claim recording started. Keep hooks installed and let physical input through. Calling InputHookService.Stop would uninstall the very hooks needed for recording.
- Preserve input-pair bookkeeping on recording entry/exit. A key held before recording must not become an orphan release, and an UP owed by a previously suppressed binding must still be reconciled.
- The Stop key and its full DOWN/repeat/UP pair do not enter the recording. The on-screen Stop click is also excluded: its low-level DOWN arrives before WPF's Click handler, so use the actual button interaction boundary to trim that control gesture. Unwanted ordinary starting clicks remain editable.
- On Stop, hook replacement, session interruption, duration limit, or full buffer, retain an editable partial take with a reason. Add clearly identified release rows for any captured inputs still down. Never continue by overwriting earlier events or dropping paired edges.
- Recording is limited to 10 minutes and the available saved-row budget at its captured insertion point. Reserve worst-case expansion before accepting each raw event: one Wait, one MoveTo for mouse events, one edge, plus a release row for each newly held input. Count the existing destination rows too. Stop before accepting an event that cannot be finalized within 1,000 rows; never truncate a finished take.
- Do not record free mouse movement or add a movement-recording option. During capture, keep only positions attached to button/wheel events; playback constructs its own path between required positions.

### Generated cursor movement

- Use one small helper owned by the macro playback code, with no new dependencies. The sender still sends a single absolute point per command; the macro scheduler generates and times the path outside the shared executor.
- Current internal tuning values: maximum speed 9,000 physical pixels/second (increased globally from 3,000 after user testing), requested update interval 8 ms, and perpendicular deviation up to the smaller of 8 pixels or 1% of travel distance. These values are code constants, not profile fields or Settings controls. Derive nominal point spacing from the same speed and interval so the interval floor cannot retain the old movement speed.
- Sample the actual physical starting cursor position on the worker. Use a quadratic curve with one small randomized perpendicular bend per move; disable the bend for distances below 4 pixels. A zero-distance move sends no motion. Never randomize the destination or overshoot it.
- Calculate nominal point spacing from a bound on the curve's maximum derivative. After rounding a candidate point to pixels, schedule it no earlier than the actual distance from the preceding output point divided by the speed cap, and at least one nominal update interval after the preceding acknowledged move. This keeps the cap valid after rounding.
- Use monotonic time, allow only one outstanding executor move, and wait for each next point off the executor. Advance one sampled point per completion; delays extend travel rather than causing a catch-up jump or a burst of queued moves.
- Validate every new move against run epoch, current foreground HWND/PID, and current display geometry. SendInput success reports insertion, not cursor arrival or processing by the target app. After each inserted point, observe GetPhysicalCursorPos off the executor until that pixel is reached or a bounded 50 ms settling budget expires; check cancellation between polls. Use the later of insertion completion and observed arrival as the next spacing baseline. A clipped/recentered cursor, read failure, or timeout cancels with a visible reason; never unclip the cursor or force a correction. Immediately before a coordinate click DOWN, re-read the endpoint and normal foreground guard. If it moved, cancel instead of clicking elsewhere. This closes detectable displacement; other processes can still move the cursor after the final check, so do not claim atomic targeting or game receipt.
- Keep a generated point on the visible desktop. Drop the bend if it leaves valid monitor geometry; if the straight path crosses an unmapped desktop gap, reject that move with a visible reason instead of silently redirecting it.
- Physical mouse movement cancels playback only when the per-macro CancelOnMouseMovement checkbox is enabled; it defaults off for new and existing macros. The opt-in applies throughout playback, including waits and holds. Injected movement never triggers it. Decode mouse moves only while this observation is needed; keep the ordinary idle/default-off path fast. Exact cursor-arrival checks remain required regardless of the option.
- Keep the final absolute SendInput call in per-monitor DPI awareness, restoring the caller's context afterward. Physical monitor enumeration alone is insufficient: native testing reproduced one-pixel rounding when the send returned to an unaware/system-aware context.
- Debug logging runs on the macro worker, not hook callbacks: session states, row/action progress, recording completion, and failure details. Cursor timeouts include requested/observed/previous/endpoint pixels, elapsed settling time and monitor bounds.
- On cancellation, leave the cursor at its current position. Release macro-owned held inputs without completing the curve or restoring the starting position.
- Do not change the recorded Wait rows to compensate for travel, replay captured drag paths, add overshoot/corrections, or claim exact wall-clock reproduction of recorded input.

For start-to-end distance L, unit perpendicular n, and signed bend A, use:

~~~text
P(t) = start + t * (end - start) + n * 4 * A * t * (1 - t)
maximum derivative magnitude <= sqrt(L * L + 16 * A * A)
delta_t <= max_speed * nominal_update_seconds / maximum_derivative_magnitude
minimum_delay = max(nominal_update_seconds, distance(previous_pixel, next_pixel) / max_speed)
~~~

This gives exact endpoints and bounded deviation. The last point is the requested integer endpoint, without a separate uncapped snap.

### Playback ownership and cancellation

1. Compile valid, enabled macro snapshots and shortcut lookup data away from hooks. The hook only latches physical events and publishes a start/stop request to a pre-existing worker/wakeup.
2. Admit one macro run, including its retirement/cleanup period. A run captures its profile, macro ID, revision, foreground generation, HWND/PID identity, and cancellation epoch.
3. Enqueue one due transition with the existing InputCommand.Completion, await the sender's insertion result, then perform its cancellable wait outside InputExecutor. Cursor arrival has the additional observation above; key insertion cannot prove game processing. Do not put an entire macro in InputCommandKind.Sequence or use DelayBeforeMs for long holds.
4. Add InputHoldOwner.Macro = 32 and equivalent fixed mouse-button release accounting. Repeated keyboard DOWN preserves a single owner. A failed DOWN does not create a release obligation.
5. Cancellation invalidates the epoch first, wakes the scheduler, then orders an executor command that releases only Macro ownership while the queue is still open. Its completion is the cleanup fence; no newer run starts before it.
6. Cancellation during native DOWN must still be covered: ownership is recorded after that call returns, before the later cleanup fence executes. Old producer continuations cannot enqueue fresh DOWN after cancellation or into a newer run.
7. Failed/throwing UP retains a bounded retry obligation including its macro origin, not only its key/button. A cleanup fence must not report the macro reusable while unresolved releases remain; report the release failure and block conflicting input until recovery. Stop/dispose waits remain bounded.
8. Hook-derived physical state is separate from synthetic ownership. Refuse a macro DOWN onto an already-physical or conflicting synthetic hold. A new conflicting nonmodifier physical DOWN cancels the macro, except a recognized busy activation pair, which is consumed first and follows the one-shot-ignore or same-macro-toggle-stop rule. Physical modifiers use deferral, not cancellation. For a fresh takeover pair with no earlier suppression owner, give that pair pass-through priority before toggles/remaps and publish physical takeover; continue passing its repeats/UP. A previously suppressed pair keeps its original cleanup route and cannot be treated as a replacement physical hold merely because a raw DOWN was seen.
9. Macro-owned targets cannot accept a new conflicting remap/hold-breath owner during the run. Existing owners are never released by macro cleanup. Preserve prior physical UP and suppression obligations through every early return.

Reserve macro modifier admission on the executor before the first output. The reservation command sits after already-queued work and fails visibly if any non-Macro synthetic modifier is down, owned, or pending release, including unowned tap/sequence state. While reserved, the shared native-key send boundary rejects any new non-Macro modifier DOWN; cover direct tap/sequence paths as well as SendTransition. Intentional Macro modifier DOWNs remain allowed and foreign UPs/retries remain admissible. Keep the reservation through cleanup/Faulted and release it only after macro obligations are resolved. This prevents an X-to-Ctrl remap from turning macro S into Ctrl+S, including a remap attempted during a Wait, without releasing another feature's key. Use the existing worker serialization and a small reservation state, not a new lock or scheduling service.

All macro release paths share the physical-takeover exception: ordinary queued UP, failed-UP retry, and cleanup fence. "Unconditional release" means independent of foreground/cancellation; a confirmed passed-through physical hold removes Macro ownership and its retry obligation without sending an UP over that hold. Retain origin in pending release records so other features' obligations are not cleared. A queued macro UP that no longer owns the input is a no-op. Cover the final native-call race by tagging macro release events separately from ordinary INPUT_IGNORE events and checking that tag on the hook thread before the general injected-event early return: suppress only a macro UP whose input currently has a confirmed passed-through physical hold. This prevents a physical DOWN arriving between the worker check and native UP from being undone. The hook-side check uses fixed state, adds no allocation/wait, never records the injected event, and leaves normal injected filtering intact. Clear takeover on its real physical UP; retain pair state until that edge even if the macro finishes first.

Physical-modifier deferral is checked before enqueue and again at executor admission. Keep the existing bool completion for other features; extend InputCommandAcknowledgement with a per-attempt DeferredForPhysicalModifiers flag set only when a macro command was refused before native insertion for that reason. The macro worker then waits outside the executor and retries the same step after release, rather than interpreting that refusal as cancellation/native failure. Use a fresh acknowledgement per attempt, preserve run/generation checks, and never retry an uncertain or failed native send as a deferral. The full busy chord is recognized from physical state before same-target takeover; its base-key DOWN/repeats/UP remain consumed even if a macro targets that key.

Track the macro's still-intended modifier holds separately from effective native ownership. If physical takeover overlaps a macro-owned modifier, defer new output; after its physical UP, reassert that modifier before resuming only if the macro still intends it down. An owed macro UP completed during deferral clears that intent and must not be reasserted. This keeps an intended Ctrl+S from resuming as plain S without releasing the user's hold.

A physical mouse-button hold before generated movement cancels/refuses that output so it cannot become an unintended drag. Explicit macro-owned button holds may still MoveTo for manually authored drags. Do not use GetAsyncKeyState as proof that a hold was physical; reseed conservative unknown/pre-held state after Start/reinstall and require a clean release before admitting an affected target.

Cancel on temporary F12, a conflicting nonmodifier physical key/button press outside the busy activation route, physical mouse movement when that macro opts in, macro content/shortcut/enable edits, owning-profile removal/identity/master-off, foreground change even inside the same profile, Advanced Mode off, session departure, either hook reinstall, application Stop, and disposal. A fresh activation of the same shortcut cancels an active ToggleMode macro, including preparation, waits and native delivery in flight; a default one-shot or another macro's shortcut keeps the existing ignored-busy behavior. Its physical modifier prefix defers new output until the shortcut is recognized or the modifier is released.

Rapid Fire calls the native mouse sender outside InputExecutor, and background Auto-Run/Anti-AFK use PostMessage. Therefore the executor's owner bit is not sufficient cross-feature coordination:

- Reject macro start while Auto-Run is active, pending, or its background worker is retiring; report why.
- Inhibit new Rapid Fire bursts and Anti-AFK taps during an accepted macro run, preserving the Rapid Fire arm.
- Wait on workers for any already-admitted click/posted tap to finish before the first macro output. Keep inhibition until macro release cleanup finishes.
- Reuse the existing Auto-Run/Anti-AFK admission pattern for this small two-way handshake. A lone IsMacroRunning check has a check-then-start race; do not add a general-purpose scheduling framework.

### Coordinates and storage

- Store physical pixel X/Y as signed integers. Send each scheduled path point using MOVE | ABSOLUTE | VIRTUALDESK and normalize against current virtual-desktop bounds, including its negative origin.
- Coordinate picking uses GetPhysicalCursorPos. Establish the native DPI context for desktop bounds and restore any temporary thread context. Do not mix WPF device-independent coordinates with recorded physical pixels.
- If a point is no longer on a monitor, reject the run/step with a visible reason instead of clamping the click to another location. Revalidate display changes during playback.
- Store macros in their owning profile INI through IniProfileStore and IniExtensions. Missing macro sections mean an empty feature; existing profiles require no migration.
- Use numeric indices in section names and a stable GUID field, not user labels as INI section names. Example shape:

~~~ini
[Macros]
Version=1
Enabled=True
Count=1

[Macro0]
Id=14c0ad00d57449ed9f3663a0e665c057
Label=Open inventory
Enabled=True
ShortcutKey=F6
ShortcutModifiers=0
StepCount=1

[Macro0.Step0]
Kind=KeyPress
Key=I
DurationMs=0
~~~

DurationMs=0 means automatic only for KeyPress/MouseClick; Wait=0 is an explicit zero wait. Other action fields use separate typed keys. Persist modifier flags with SetInt32/GetInt32 and validate the allowed bit mask: existing GetEnum rejects combined ModifierKeys values.

- Deep-copy macro definitions and their steps in ProfilePersistenceSnapshot. UI publication builds and swaps complete arrays; runtime playback never enumerates a mutable ObservableCollection.
- A missing Macros section with no MacroN/step sections means the legacy empty feature. An existing section requires Version=1, a valid Enabled value and Count, contiguous MacroN sections, valid unique IDs, and exactly the declared contiguous step sections. Orphan or undeclared indexed macro sections are malformed. Parse present values strictly with invariant culture; an explicitly empty numeric/bool/key/enum field is malformed even when that field could have been omitted for its documented default.
- Extend IniDocument with section existence/names and a strict source-value accessor that distinguishes missing from an explicitly empty loaded value. Retain the minimal presence metadata before Load passes values to SetValue, which currently discards empty values. Keep existing GetValue/GetSection/SetValue behavior unchanged for other features; no general parser rewrite. Strict macro reads use this source accessor through IniExtensions.
- Catch a macro-format failure inside LoadProfile after the other feature sections have loaded: keep those parsed features, publish empty disabled macro settings, set IsPersistenceSuspended=true, and retain a nonpersisted macro load-error string for an editor banner. Existing SaveProfileAsync then refuses writes instead of erasing the unread source. Recovery is to correct the file and reload the app; do not silently clear this suspension on an ordinary edit. Representable incomplete drafts do not suspend persistence.
- Do not persist F12 or add a stop-key Settings control. Reserve it for the temporary emergency handler while a macro session is active, ahead of ordinary feature dispatch; otherwise retain normal F12 behavior. Existing assignments remain saved. Reject F12 as a macro activation key, ignore injected F12 for cancellation, and retain the physical F12 suppression latch through its UP even if cleanup finishes first.

## Existing integration points

| File / locator | Required use |
|---|---|
| MainWindow.xaml:911, :1376 | Insert Macros after Advanced and before Display. |
| MainViewModel.cs:134, :541; MainViewModelTabTests.cs | Add TabIndexMacros=3; move Display/System to 4/5; update profile-kind coercion. |
| Profile.cs:26; ProfileChangeKind.cs:24 | Add macro settings and a Macros change bit in AllRuntime. |
| ProfilePersistenceSnapshot.cs:15 | Deep-copy definitions and step arrays. |
| IniProfileStore.cs:240, :510; IniExtensions.cs:64; IniDocument.cs:69 | Add strict typed macro persistence and source presence without treating flags as a single enum value. |
| ProfileViewModel.cs:975; MainViewModel.cs:647 | Route changes through existing runtime notification and debounced autosave. |
| InputHookService.cs:190, :1317, :1512 | Compose macro components and capture original physical hook payloads. |
| InputExecutor.cs:64, :508, :537, :556 | Reuse completion, DOWN admission, foreground validation, and hold ownership. |
| InputExecutor.cs:373, :516 | These paths sleep on the shared worker; do not use them for long macro sequences/waits. |
| WindowsInputSender.cs:19, :69, :134 | Reuse keyboard/error boundary; add generic mouse transitions/movement/wheel. |
| NativeMethods.cs:499, :509, :541, :608 | Reuse native structs and centralize new mouse/DPI constants and P/Invokes. |
| InputHookService.cs:135, :467, :545, :913, :967, :1096, :1643, :1662 | Mode, Stop, session, reinstall, edit, foreground, and release cancellation seams. |
| RapidFireStateMachine.cs:344; AutoRunStateMachine.cs:352, :370 | Coordinate native clicks and background posted input before macro output. |
| RecordingInputSender.cs; FakeAutoRunTransport.cs; InputHookServiceTestExtensions.cs:28 | Extend current fakes and test actual production transitions without injecting into the desktop. |

## Implementation tasks

### 1. Establish the branch, baseline, and macro data contract

**Files:** Create Models/MacroSettings.cs and Utilities/MacroValidation.cs. Modify Models/Profile.cs, Models/ProfileChangeKind.cs, Factories/ProfileFactory.cs. Add Tests/MacroModelTests.cs; extend Tests/ProfileFactoryTests.cs.

- [x] Record the user's confirmed product decisions, including generated cursor travel and temporary hard-coded F12.
- [ ] Read memory.md and current AGENTS.md; use the worktree skill at execution time if isolation is needed. Start feat/macros from the intended base while preserving unrelated local changes.
- [ ] Record baseline Release build/test results using the commands under Final verification.
- [ ] Define a plain MacroSettings collection, stable MacroDefinition identity/label/shortcut, a typed MacroStep value, and session status/result values. Keep these small related types together; no generic action/plugin framework.
- [ ] Implement the exact defaults, fields, limits, structural rules, and playable-draft distinction under Global constraints and data contract.
- [ ] Put key/enum/flags/coordinate/duration/label/sequence validation in one helper consumed by loader, editor, and runtime publication. Permit draft incompleteness but never publish it for playback.
- [ ] Add a failing check for legal overlapping Ctrl+C edges, repeated DOWN, unmatched UP, unsupported key/flag values, and a tap with automatic versus explicit duration. Implement and rerun it.

Contract for keyboard shortcut-mask validation:

~~~csharp
const ModifierKeys allowed = ModifierKeys.Control | ModifierKeys.Alt
    | ModifierKeys.Shift | ModifierKeys.Windows;
bool validModifiers = (modifiers & ~allowed) == ModifierKeys.None;
~~~

### 2. Persist macros and publish detached snapshots

**Files:** Modify Configuration/IniProfileStore.cs, Utilities/IniExtensions.cs, Utilities/IniDocument.cs, and Models/ProfilePersistenceSnapshot.cs. Add Tests/IniDocumentTests.cs; extend Tests/IniProfileStoreIntegrationTests.cs and Tests/ProfilePersistenceSnapshotTests.cs.

- [ ] Add a failing round-trip check covering two macros, ordered key/mouse/wait steps, Unicode labels, negative coordinates, Ctrl+Alt, and an unassigned shortcut under a non-English culture.
- [ ] Implement the indexed INI format above using IniExtensions. Validate counts before allocating/iterating and retain source-preservation behavior on unsupported/corrupt data.
- [ ] Deep-copy definitions/steps; prove that changing an editor step after snapshot capture cannot change the pending save.
- [ ] Verify that macro persistence introduces no app-level Stop-key or movement-tuning settings.
- [ ] Verify old profiles without macro sections load with no macros and that deleting a macro removes its sections on save.
- [ ] Prove malformed fields, unsupported version, count overflow, duplicate IDs, missing declared sections, an empty [Macros], explicitly empty optional DurationMs, and orphan/extra step sections preserve original bytes across an attempted unrelated save while other profiles still load. Verify existing IniDocument reader semantics remain unchanged. Prove structurally valid incomplete drafts survive a save/reload and never enter runtime lookup.

Core flags persistence is intentionally numeric:

~~~csharp
document.SetInt32(section, "ShortcutModifiers", (int)macro.ShortcutModifiers);
// Loading uses the strict TryGet overload and rejects a present malformed value.
~~~

### 3. Extend the existing sender/executor for macro-owned mouse input and cleanup

**Files:** Modify Services/IInputSender.cs, Services/WindowsInputSender.cs, Services/Input/InputExecutor.cs, Interop/NativeMethods.cs. Extend Tests/Fakes/RecordingInputSender.cs and the private senders in Tests/AutoRunStateMachineTests.cs and Tests/WheelInputExecutorTests.cs. Add Tests/MacroInputExecutorTests.cs and Tests/MacroCoordinateTests.cs.

- [ ] Extend the sender with individual button transitions, physical absolute movement, and signed wheel output. Use the existing diagnostic boundary and injection filtering, with a distinct macro-release tag for the takeover race described above; do not route long holds through SendLeftClick.
- [ ] Add corresponding InputCommand kinds/payloads and InputHoldOwner.Macro=32. Use fixed button state/release slots, with the same conservative failed-UP handling as keyboard input.
- [ ] Add the executor modifier reservation and enforce it at the shared key-send boundary, including unowned sequence/tap calls. Test a pre-held X-to-Ctrl remap, the same remap starting during a macro Wait, a pending modifier release, and an intentional macro-owned Ctrl+S; foreign releases must still complete.
- [ ] Treat MoveTo and wheel commands as new output for admission/shutdown even though they are not a key/button DOWN. They must never inherit the unconditional admission reserved for an already-owned UP.
- [ ] Add an executor command to release Macro ownership and return a completion result. Keep it admissible during shutdown until the existing queue closes.
- [ ] Preserve physical holds and other feature owners across ordinary UPs, retry records, and cleanup. Test a queued/failed UP followed by passed-through physical takeover, including physical DOWN between the worker check and tagged-UP hook dispatch; the real hold lasts until physical UP and no stale macro retry remains. Also test a previously suppressed pair that must not transfer ownership. A cleanup attempt cannot report successful reuse while release obligations remain unresolved.
- [ ] Test DOWN failure, UP failure/throw, cancellation during blocked DOWN, another feature owning the same key, X1/X2 output, negative-origin mapping, monitor gaps, and display removal with fake native boundaries.
- [ ] Rerun existing executor reliability, wheel, gesture, and Rapid Fire tests before continuing.

New native sender operations have concrete responsibilities:

~~~csharp
bool SendMouseButton(sWinShortcuts.Models.MouseButton button, bool isDown);
bool MoveMouseTo(int physicalX, int physicalY);
bool SendMouseWheel(int delta, bool horizontal);
~~~

### 4. Implement cancellable playback and shortcut routing

**Files:** Create Services/Input/MacroStateMachine.cs; keep its small cursor-path helper in the same file. Modify Services/InputHookService.cs, Services/IInputHookService.cs, Services/Input/InputRuntimeState.cs, Services/Input/RapidFireStateMachine.cs, Services/Input/AutoRunStateMachine.cs, Services/Input/AntiAfkStateMachine.cs, Services/Input/GestureChordStateMachine.cs as required for shared timing/admission. Add Tests/MacroPlaybackTests.cs and Tests/MacroShortcutTests.cs; extend Tests/MacroCoordinateTests.cs and actual-service dispatcher/lifecycle tests.

- [ ] Add a failing test that leaves a macro in a long Wait while an unrelated executor UP completes immediately.
- [ ] Implement one pending/active/retiring run, generation checks, a pre-existing worker signal, and a shortcut lookup built outside callbacks. Keyboard autorepeat starts once; additional activation shortcuts while busy do not start, restart, or queue playback. A fresh same-macro shortcut cancels ToggleMode playback; one-shot and other-macro busy shortcuts remain ignored.
- [ ] Revalidate lookup and visible conflicts on profile bindings, global Launcher bindings, and every app-toggle setter, including Settings rollback. Add a reassign/rollback test for a macro using the same base key with modifiers; test the actual dispatch path and the editor notification.
- [ ] Match physical modifier combinations, consume only the activation key's owned pair, wait for chord release, and keep existing modifier/physical-state observers and prior-UP cleanup functioning.
- [ ] Test the full second Ctrl-DOWN/F6-DOWN/F6-UP/Ctrl-UP dispatch during one-shot playback: no cancellation, restart, queued run, or new macro DOWN while the modifier is physically held. In ToggleMode that same fresh chord instead cancels and releases owned input. Include an executor-blocked attempt that defers without being mistaken for native failure; owed UPs still complete. Test physical overlap with an intended macro modifier, with and without its macro UP occurring during deferral, and busy activation whose base key is also a macro output target.
- [ ] Schedule one transition at a time and await its existing completion; use cancellable delays for waits/holds. Preserve explicit Wait and press-duration values while allowing generated cursor travel to add time.
- [ ] Add the generated movement loop described above with internal constants, one bend per move, rounded-point speed checks, and an exact final endpoint. Test long, short, zero-distance, and negative-coordinate moves; an RNG/clock seam makes those checks deterministic.
- [ ] Test delayed worker wakes, failed movement sends, clipped/recentered cursor observations and settling timeout, display changes, and stale foreground admission. No late frame may cause a catch-up burst or click before the endpoint is observed. Native insertion success alone must not permit a misplaced click.
- [ ] Add the hard-coded F12 emergency handler before macro capture/feature dispatch, only while a macro session is active. Consume its physical pair, ignore injected F12, reject F12 activation assignments, and preserve normal F12 behavior when idle. Add no stop setting or normal playback Stop button.
- [ ] Implement cancellation ordering and cleanup fencing, including a DOWN blocked inside native delivery.
- [ ] Add worker-side native foreground validation before every new key/button/move/wheel output; recheck generation after native preparation. Already-admitted releases ignore foreground/cancellation but honor confirmed physical takeover on every release path.
- [ ] Add the physical collision policy and the Rapid Fire/Anti-AFK admission-and-drain handshake; reject active/pending/retiring Auto-Run. Preserve RF arm status through temporary inhibition.
- [ ] Wire every listed lifecycle boundary and ensure cancellation never resumes an old run on profile return.

Existing completion pattern to use on the macro worker:

~~~csharp
var completion = new TaskCompletionSource<bool>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var acknowledgement = new InputCommandAcknowledgement();
var command = new InputCommand(key, isDown,
    Guard: this, Generation: runGeneration,
    ForegroundGeneration: foregroundGeneration, ExpectedProfile: profile,
    HoldOwner: InputHoldOwner.Macro, Acknowledgement: acknowledgement,
    Completion: completion);
if (!inputQueue.Enqueue(in command))
{
    return MacroAttemptResult.Failed;
}
if (await completion.Task.ConfigureAwait(false)) return MacroAttemptResult.Sent;
return acknowledgement.DeferredForPhysicalModifiers
    ? MacroAttemptResult.Deferred : MacroAttemptResult.Failed;
~~~

The production caller handles the local Sent/Deferred/Failed outcome with the epoch checks and cleanup fence described above. Deferred retries wait off the executor; a cancelled await must not abandon knowledge of a DOWN still in flight.

### 5. Implement bounded recording and take finalization

**Files:** Create Services/Input/MacroRecorder.cs. Modify Services/InputHookService.cs and Services/IInputHookService.cs. Extend Tests/Fakes/FakeInputHookService.cs; add Tests/MacroRecorderTests.cs and actual-callback boundary tests.

- [ ] Add a failing recording case for Ctrl DOWN, C DOWN, C UP, Ctrl UP with known timestamps and verify exact ordering/gaps.
- [ ] Capture original keyboard/mouse payloads into the fixed value buffer. Use one writer and atomic stop admission; no row creation or collection mutation on hooks.
- [ ] Add recording entry/exit coordination that pauses automation without uninstalling hooks and preserves owed suppressed UPs.
- [ ] Exclude injected events and the full Stop-key pair. Exclude the actual Stop-button gesture using its WPF input boundary.
- [ ] Convert the take into explicit event/Wait/MoveTo rows, preserving signed wheel values, X1/X2 identity from mouseData, and repeats. Append to the captured destination macro in one publication; round-trip both X buttons independently.
- [ ] Test pre-held inputs, partial holds at Stop, buffer/duration exhaustion, stop racing a callback, interrupted hooks/session shutdown, and rejected input after finalization.
- [ ] Verify that recording ignores free mouse motion and retains button/wheel coordinates. Preserve the idle mouse-move fast path; the separate physical-motion observation during generated playback is cancellation-only.

Capture representation uses values, not UI objects:

~~~csharp
internal readonly record struct RecordedMacroEvent(
    long Timestamp, int Message, int VirtualKey, uint ScanCode,
    uint Flags, uint MouseData, int X, int Y, int WheelDelta);
~~~

### 6. Build the editor and insert the tab

**Files:** Create Views/MacrosView.xaml and its minimal code-behind, ViewModels/MacrosViewModel.cs, ViewModels/MacroViewModel.cs, ViewModels/MacroStepViewModel.cs. Modify MainWindow.xaml, MainWindow.xaml.cs, Services/SystemTrayService.cs, App.xaml.cs for close/constructor wiring as needed, ViewModels/ProfileViewModel.cs, ViewModels/MainViewModel.cs, and Views/SettingsWindow.xaml only for the existing Advanced Mode description. Add Tests/MacroEditorTests.cs; extend Tests/MainViewModelTabTests.cs, Tests/ProfileRuntimeNotificationTests.cs, Tests/MainViewModelSaveTests.cs.

- [ ] Add a failing tab-coercion test for the new Macros position and built-in/custom switches.
- [ ] Bind the tab to the selected profile's macro editor. Reuse existing brushes, styles, key catalog/display, selection behavior, and error presentation.
- [ ] Implement UI/runtime handoff exactly once in MainViewModel: capture destination/insertion, apply the awaited result once before deletion/exit flush, and expose the persistence-suspension load banner. Extend shutdown/save tests for a recording finishing during close, repeated Exit requests, and teardown with an unavailable dispatcher; never wait synchronously on dispatcher-dependent work in OnExit.
- [ ] Implement all list and row operations from Scope, both profile/macro enable controls, labels, key picker and modifier checkboxes, coordinate picking, and relevant-field editors. Verify assigning a conflicting launcher/toggle chord through these controls changes no existing feature and shows the conflict; no keyboard-capture service mode is needed.
- [ ] Implement Record/Stop recording and playback status with a temporary F12 hint, without a normal playback Stop control. Avoid synchronous UI waits. Marshal status notifications to the owning dispatcher; stale events re-read current state.
- [ ] Publish completed edits through ProfileChangeKind.Macros and existing autosave. Unsubscribe removed rows individually; ObservableCollection.Clear does not provide removed items to detach.
- [ ] Cancel a running macro before publishing edits to it. Retain stopped recordings in the original draft through tab/profile changes and close.
- [ ] Verify keyboard access, focus, accessible names, visible validation, minimum-height layout, and Stop recording availability. Update the existing Advanced Mode description to include macro playback, without adding a settings option.
- [ ] Load the real deferred XAML in an STA test. Copied XAML fixtures must use .xaml.testdata, as existing tests do, to avoid the parent WPF project compiling them.

The resulting indices:

~~~csharp
public const int TabIndexLauncher = 0;
public const int TabIndexKeys = 1;
public const int TabIndexAdvanced = 2;
public const int TabIndexMacros = 3;
public const int TabIndexDisplay = 4;
public const int TabIndexSystem = 5;
~~~

### 7. Validate the integrated behavior, document, commit, and open the PR

**Files:** Update README.md, AGENTS.md only where its behavior map changes, and append brief durable notes to memory.md. Retain this plan locally unless including it in the PR is desired.

- [ ] Run focused new tests and the existing input, persistence, settings, and tab suites. Prefer fake clocks/blocked native calls over arbitrary sleeps.
- [ ] Run Final verification once the integrated implementation is stable.
- [ ] Conduct a fresh independent review of changed input ownership, recording boundaries, profile persistence, and UI lifetime; fix supported findings and rerun affected checks.
- [ ] Manually create/rename/delete a macro; assign a shortcut; edit/reorder/delete recording noise; restart and verify persistence.
- [ ] Manually verify an exact hold/chord/click sequence, generated curved travel to the exact pixel, temporary F12 cancellation during movement/holds, focus loss, toggling Advanced Mode, profile removal, and no stuck input after cancellation. A second shortcut press must leave the current run alone.
- [ ] Check mixed-DPI monitors, negative origins, monitor removal, standard/extended keys, mouse buttons/wheel, and a target application/game. Check raw-input limitations honestly; do not substitute a fake-driven test for a live hook claim.
- [ ] Use UI Automation if available; do not take screen captures, per project memory. If native GUI automation is unavailable, identify which manual checks remain unrun.
- [ ] Review git diff --check and stage only intended feature, test, documentation, and memory changes.
- [ ] Commit with a factual Conventional Commit subject such as feat(macros): add macro editor, recorder, and playback; adjust it to the final diff.
- [ ] Push the feature branch and open a PR against main. Use a structured body or gh --body-file with actual newlines, describe implemented behavior and validation, and include any unrun manual checks.
- [ ] Check hosted CI and resolve feature-related failures. Provide the PR URL and commit hash. Do not merge the PR as part of this request.

## Final verification

Use the current CI's build shape; keep the RID restore intact:

~~~powershell
dotnet restore sWinShortcuts.sln -r win-x64
dotnet build Tests/Tests.csproj -c Release -r win-x64 --no-restore -p:SelfContained=false -p:PublishSingleFile=true
dotnet test Tests/Tests.csproj -c Release -r win-x64 --no-build
dotnet publish sWinShortcuts.csproj -c Release -r win-x64 --self-contained false --no-build -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o .tmp/macros-publish
git diff --check
~~~

If restore exits 1 without diagnostics on this host, rerun it with -m:1 as documented in memory.md. Package scans, if run, use --no-restore so they do not replace the RID assets. Build/test expected result: no new warnings or failures. Publish expected result: the tested build produces a nontrivial single-file executable.

Acceptance requires both editable manual sequences and editable recorded takes, default one-shot and optional toggle-loop per-profile keyboard activation, reliable timed down/up behavior, absolute endpoint clicks with speed-capped slightly curved travel, release-safe lifecycle/F12 cancellation, and saved macros surviving restart. The recorder has Stop recording; playback has no normal Stop button; its per-macro ToggleMode checkbox selects shortcut-controlled looping. A passing build alone does not validate WPF deferred templates or live hooks.

## Primary documentation checked for this plan

Checked 2026-09-13 through Context7 and page retrieval; recheck if implementation changes the selected approach.

- [LowLevelKeyboardProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc) and [LowLevelMouseProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc): installing message-loop thread, prompt callback return, hook timeout/removal, and keyboard-state ordering.
- [KBDLLHOOKSTRUCT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-kbdllhookstruct) and [MSLLHOOKSTRUCT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-msllhookstruct): injected-event flags, raw metadata, and mouse screen coordinates.
- [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput): insertion counts, pre-existing held keys, and integrity-level limits.
- [MOUSEINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput): absolute 0-65535 coordinates, VIRTUALDESK, mouse buttons, and signed wheel semantics.
- [GetPhysicalCursorPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getphysicalcursorpos), [high-DPI desktop guidance](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows), and [SetThreadDpiAwarenessContext](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddpiawarenesscontext): maintain an explicit physical-coordinate contract.
- [ClipCursor](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-clipcursor): a target application's confinement can adjust the cursor away from an injected endpoint; observe placement and fail visibly rather than removing its confinement.

## 2026-09-14 editor follow-ups

- Duplicate labels use increasing space-separated numeric suffixes within each profile (Ammo, Ammo 1, Ammo 2), skipping visible/saved name collisions and preserving the existing disabled/unassigned clone behavior.
- Enable the existing press-to-select behavior on the macro shortcut and step-key dropdowns.
- Set all explicit Wait rows to one validated duration using the existing 0..3,600,000ms bound. Keep other raw edits and native press/click hold durations; include waits inside collapsed triplets and publish the batch once.
- Collapse presses is a default-on presentation setting. Only an adjacent matching Down/Wait/Up triple becomes one displayed press; keep canonical models, timing and recording indices intact. Grouped row operations use the entire span. Mismatched, unsupported, overlapping or interleaved sequences remain separate. Grouped key/button edits update both endpoints in one batch; the grouped hold edits the original Wait text. Invalid hold text keeps the group and selection stable, with its error shown on the group and inspector. Error navigation selects the containing displayed row. Show all steps turns off Collapse for manual source editing.
- Claude Opus5/max completed the authorized /low-priority consultation. Add Collapse presses and Set all waits… in a sequence-panel header. The bulk input opens inline beneath that header, validates text without saving, shows affected Wait/press-hold counts, and stays open after an explicit Apply. Scope Enter to the duration field and Escape to the strip; close automatically on macro/context/recording changes. No app settings, persistence changes or new dependencies.
- Finish focused/full validation, commit/push, exact-head green CI, then a fresh Astra/xhigh review; repeat supported fixes as separate commits and leave PR25 open. The original returned Claude UI was preserved in its own commit before root corrections.

## 2026-09-14 container and naming follow-up

- Checkpoint every pending project change before UI edits. Completed as `35823db`, including the previously local root plans.
- Match the neighboring tabs' outer GroupBox and header, padding, border and disabled treatment. Keep the sequence viewport finite and virtualized.
- Put the profile's master switch in the section header. Turning it off dims and disables the full editor body, while the master remains usable and recording Stop remains reachable. Keep master-toggle admission separate from editor-body enablement.
- Replace the persistent Name field with a focused naming dialog for New and Rename. Cancel changes nothing; acceptance creates or renames once using existing label validation. Both toolbar and empty-state creation use this flow. Duplicates retain their automatic incremented name.
- Place the selected macro's enable control with its identity so its scope is clear. An individually disabled macro remains editable.
- Preserve Collapse, bulk Wait editing, shortcuts, raw draft text, runtime behavior and profile persistence. The user delegated direct implementation to Claude Opus 5/max; root validates and records each supported fix separately before repeating CI and clean-context review. Do not merge PR #25.

- Implementation checkpoint: the outer container was preserved in `0ceb162`; the naming follow-up builds without warnings and passes 100 focused UI/lifecycle checks. The original 650x480 coordinate-picker bounds check now passes after compacting spacing and showing the status line with ellipsis plus its full tooltip. Full-suite, hosted CI and fresh review results follow separately.

## 2026-09-14 modifier recording correction

- Ignore repeated DOWN events for already-held modifiers before recording row budgeting and timestamp advancement. Preserve the complete elapsed gap to the next accepted event, the original modifier/click ordering, and ordinary non-modifier typematic.
- Existing saved steps remain unchanged by the software fix. The user separately authorized repair of one saved macro with a backup while the app was closed; uniform 100ms wait fragments caused by duplicate modifier events were coalesced for that repair only.


## 2026-09-15 toggle playback follow-up

- Add optional ToggleMode=false to each macro and its strict optional INI persistence; legacy sources remain one-shot. Reuse the existing record cloning, view-model Change/publication path and live-edit cancellation. No schema version bump or new service.
- A distinct shortcut press starts a single session after physical shortcut/modifier release. Repeat the validated, balanced sequence on the existing worker under the same reservation. Insert a cancellable10ms gap between passes to prevent zero-duration busy loops; preserve every saved wait/hold and movement rule.
- A second distinct press of the same macro shortcut cancels immediately through existing retirement, including preparation, long waits, movement and a native DOWN in flight. Consume the stop key's full DOWN/repeat/UP pair through the existing activation latch. Other busy macro shortcuts and default one-shot mode retain their current behavior.
- F12, foreground/owner/settings changes, master-off, mode-off, input failure and existing lifecycle cancellation stop the loop. Do not resume automatically after focus returns. Worker debug row entries include iteration and session diagnostics identify toggle mode.
- Add a clearly explained per-macro UI option without disturbing the existing frame, naming popup, disabled body or compact viewport. Direct UI edits remain delegated; preserve returned changes in a commit before corrections.
- Verify start-release/held-repeat behavior, multiple passes, mid-hold stop with keyboard/mouse cleanup, modifier chords, other busy shortcuts, native delivery in flight, cancellation and legacy/strict persistence. Then combined build/full tests, one published test EXE, exact-head green CI and a fresh Astra/xhigh code review with each correction committed separately. Leave PR25 open.
