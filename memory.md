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
