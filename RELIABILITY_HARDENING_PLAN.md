# Runtime Reliability Hardening Plan

**Scope:** the July 2026 follow-up audit of profile activation, low-level hook execution,
timers, live setting changes, and paired synthetic input.

**Mandate:** gaming behavior must remain deterministic: no stale-profile input, no
hook-thread stalls, no unmatched synthetic `DOWN`/`UP`, no lost physical binding edges,
and no automation continuing after its controlling feature is disabled.

## Non-negotiable invariants

1. Every process-wide `SendInput` transition is emitted by one FIFO executor. A state owner
   records the transition and enqueues it while holding its own lock; it never calls
   `SendInput` from a low-level hook callback. Background Auto-Run's window-targeted
   `PostMessage` transport remains serialized by its dedicated thread and `_autoRunLock`.
2. A release is unconditional once its matching press may have landed. Generation and
   feature gates may reject only new `DOWN` work, never a recorded `UP`.
3. Foreground identity is published synchronously with a monotonic generation. Profile
   input activation uses a non-dropping FIFO independent of coalesced color work. While
   published and active generations differ, profile-scoped new presses fail open.
4. Live configuration changes reconcile in-flight state explicitly. Disabling a master
   profile or Auto-Run hard-releases owned automation; Alt-Mouse/Hold-Breath rebinds cancel
   their old gesture; consumed physical presses retain their original release decision.
5. Timer callbacks are treated as potentially queued after `Change(Infinite, ...)`.
   Every callback validates an arm token, elapsed-time boundary, current generation, and
   controlling feature before deciding a new press.
6. The hook installer always runs on the WPF dispatcher. Service initialization may use
   background work, but hook installation is explicitly marshalled back to that dispatcher.

## Implementation order

### Phase 1 — regression seams

- Extract the native key-emission boundary behind an injectable sender.
- Add deterministic tests for FIFO transitions, release-after-cancel, failed/stale `DOWN`,
  launcher acknowledgement, foreground A→B→A, live Auto-Run disable, and profile master
  disable/re-enable.

### Phase 2 — foreground and live-profile authority

- Split the current foreground worker into a non-dropping input channel and a bounded,
  coalescing color channel.
- Carry `{generation, hwnd, pid, exe, profile}` in both channels.
- Add a runtime profile-change notification path from `ProfileViewModel` through
  `MainViewModel` to `ProfileActivationService`/`InputHookService`.
- Reconcile feature-specific state without resetting unrelated active input.

### Phase 3 — one ordered injection lane

- Generalize the existing Hold-Breath injector into the sole key executor.
- Move combined-target, Caps remap/state, Alt-Mouse taps, and launcher dummy input into
  that lane. Enqueue state transitions under their owner lock so release cannot overtake
  press.
- Keep launcher process launch asynchronous and gated by completion of the queued dummy
  command; never wait in the hook callback.

### Phase 4 — state-machine fixes

- Gate Hold-Breath panic by profile master, Hold-Breath enable, Advanced Mode, and current
  foreground generation.
- Revalidate Hold-Breath arm identity under `_holdBreathLock`; invalidate stale callbacks.
- Give Alt-Mouse an immutable down-time binding snapshot and cancel it on Alt release,
  disable, removal, rebind, profile switch, or generation change.
- Handle Windows Launcher key-up cleanup before live enable/profile gates.
- Return the recorded combined-mapping suppression decision for typematic repeats.

### Phase 5 — lifecycle and verification

- Serialize foreground generation allocation, identity publication, and both channel writes so concurrent
  WinEvent/profile-edit publishers cannot enqueue generations out of order.
- Capture each worker run's channel/token state immutably; own SetWinEventHook, its message loop, and
  UnhookWinEvent on one dedicated thread.
- Capture deep profile persistence snapshots on the dispatcher before background autosave; validate and
  serialize that exact snapshot. Resolve duplicate on-disk executable identities by failing closed until
  repaired, and cancel stale saves that lost a successful removal race.
- Marshal `InputHookService.Start()` explicitly to the application dispatcher.
- Run Release build and all tests, inspect every `SendInput` call site and every release
  path, then perform a full static review of startup, foreground switching, live settings,
  stop/session-switch, and hook-reinstall boundaries.

## Acceptance scenarios

- A→B→A while color application is blocked observes both release boundaries and enables
  input only for the final generation.
- Profile master off/on takes effect without another Alt-Tab; removing an active profile
  cannot leave its bindings live.
- Auto-Run off always releases W/sprint, including Background transport.
- Combined target ownership produces exactly one `DOWN` on 0→1 and one `UP` on 1→0 under
  concurrent source release, profile switch, Advanced Mode change, and stop.
- Alt-Mouse quick tap emits only Tap; hold emits only Hold; a live rebind never executes
  the old or new binding for the already-started gesture.
- Hold-Breath cannot arm or panic-consume input after disable, Advanced Mode off, profile
  switch, or stop.
- Disabling Windows Launcher during a held hotkey still consumes and clears the matching
  key-up; the next press launches normally.
- No low-level hook callback reaches `NativeMethods.SendInput` directly.
- Concurrent foreground/profile republish calls preserve strictly increasing input generations.
- An edit made during an in-flight save produces two coherent snapshots (never one mixed live object);
  a queued save after successful removal is a no-op and cannot resurrect the deleted profile.

## Completion status — 2026-07-11

All five phases and acceptance scenarios are implemented. Final validation:

- Release build: 0 warnings, 0 errors.
- Focused reliability suite: 52/52 passed, then passed in 10 consecutive runs.
- Full sandbox-runnable suite: 180/180 passed.
- Two additional INI integration tests reach File.Replace, which this restricted sandbox denies;
  both previously pass under normal Windows filesystem permissions.
- Static scan confirms every process-wide NativeMethods.SendInput call is isolated in
  WindowsInputSender and consumed through the single FIFO executor.
- The one permitted Claude invocation timed out without feedback; per user direction it was not retried.
  Final review therefore used the completed local review findings, deterministic tests, Release build,
  and static call-site/lifecycle inspection.
