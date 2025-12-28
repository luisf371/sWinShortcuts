# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

sWinShortcuts is a Windows keyboard/mouse remapping application built with .NET 8 WPF. It installs low-level Windows hooks (WH_KEYBOARD_LL, WH_MOUSE_LL) to intercept and remap input events on a per-application basis.

**Core concept:** Profiles are activated automatically based on the foreground window's executable. Each profile defines remapping behaviors (Alt+Mouse gestures, right-click chords, Caps Lock modes, etc.).

## Build & Run

```powershell
# Build
dotnet build sWinShortcuts/sWinShortcuts.csproj

# Run (starts minimized to tray)
dotnet run --project sWinShortcuts/sWinShortcuts.csproj
```

**Note:** The project excludes test files from compilation (see `<Compile Remove="Tests\**\*.cs" />` in csproj). There are currently no automated tests.

## Architecture

### Service Composition (Dependency Injection)

All services are registered in `App.xaml.cs` using .NET Generic Host. The host manages service lifecycles and coordinates startup/shutdown.

**Key entry point:** `ProfileActivationService` (IHostedService)
- Wires together `ForegroundWatcher`, `InputHookService`, and `ProfileManager`
- Starts hooks and foreground detection on startup
- Activates/deactivates profiles based on foreground window changes

### Core Services

| Service | Responsibility | Important Notes |
|---------|---------------|-----------------|
| `InputHookService` | Installs global keyboard/mouse hooks, interprets events, synthesizes input | **Most complex component** (1,100+ lines). Uses lock-free patterns with `Interlocked` for performance. Hot path must be kept fast. |
| `ForegroundWatcher` | Detects foreground window changes via `EVENT_SYSTEM_FOREGROUND` | Resolves process names; may fail for protected processes (access denied) |
| `ProfileManager` | CRUD operations for profiles, executable matching | Thread-safe with `SemaphoreSlim`. Enforces unique names/executables. |
| `IniProfileStore` | Persists profiles to INI files in `%APPDATA%\sWinShortcuts` | Handles migration between old/new INI formats |
| `NvidiaColorControlService` | Applies display color settings via NVAPI + Windows gamma ramps | Best-effort NVAPI; gracefully degrades if NVIDIA drivers missing |
| `SystemTrayService` | Manages notify icon, context menu, window show/hide | Couples to `MainWindow` type (not ideal but functional) |

### Special Profiles

- **Windows Profile** (`ProfileConstants.WindowsProfileName`): Global defaults, acts as fallback. Cannot be deleted. Contains Windows Launcher (numpad-based app launcher).
- **Color Profile** (`ProfileConstants.ColorProfileName`): Dedicated global color settings. Disabled features other than color control.

### MVVM Architecture

- **ViewModels:** `MainViewModel` (profile list), `ProfileViewModel` (single profile editor)
- **Models:** `Profile` with nested settings objects (`AltMouseSettings`, `CombinedMappingsSettings`, etc.)
- **Binding:** Uses `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`)

**Auto-save behavior:** Property changes in `ProfileViewModel` trigger `SaveProfileInternalAsync` via `QueueAutoSave`. **This is not debounced** - multiple rapid changes cause multiple file writes. Consider fixing this if modifying auto-save logic.

## Input Hook Architecture (Critical)

`InputHookService` is the performance-critical core. Key concepts:

### Lock-Free State Machine
- Uses `volatile` fields for runtime flags (`_isRunning`, `_altPressed`, `_rightButtonPressed`)
- Uses `Interlocked.CompareExchange` for atomic state transitions (TIMER_ARMED → TIMER_FIRED → TIMER_IDLE)
- Pre-allocates `System.Threading.Timer` instances per mouse button to avoid GC pressure

### Input Loop
1. `KeyboardCallback` / `MouseCallback` invoked by Windows on every input event
2. Check `_isRunning` flag early to exit fast if service stopped
3. Filter injected events (our own `SendInput` calls marked with `INPUT_IGNORE`)
4. Dispatch to feature handlers (CapsLock, CombinedMappings, AltMouse, WindowsLauncher)
5. Return `1` to suppress original input, or call `CallNextHookEx` to pass through

### Anti-Cheat Considerations
The hook includes "humanization" features to avoid detection by anti-cheat software:
- Thread-local `Random` with hybrid seeding (timestamp XOR thread ID)
- RNG warmup calls before key injection (breaks thread-reuse patterns)
- Jitter on timing values (hold breath delay, key press duration)

**CAUTION:** If modifying hook behavior, preserve the lock-free patterns and avoid allocations in callbacks. Even string formatting in `LogDebug` allocates - consider adding null checks before logging.

### State Management on Profile Switch
When profiles switch, `ReleaseAllState()` is called to:
- Cancel all pending timers
- Release any pressed keys (send key-up events)
- Reset mouse button states
- Clear override dictionaries

This prevents stuck keys or held state from previous profiles.

## Native Interop

All P/Invoke declarations are centralized in `Interop/NativeMethods.cs`:
- Hook management: `SetWindowsHookEx`, `UnhookWindowsHookEx`, `CallNextHookEx`
- Input injection: `SendInput`, `MapVirtualKey`
- Window management: `GetForegroundWindow`, `SetWinEventHook`
- Display control: `CreateDC`, `SetDeviceGammaRamp`

NVAPI functions are loaded dynamically via `nvapi_QueryInterface` in `NvidiaColorControlService.NvApiNative` to avoid hard dependencies on nvapi.dll.

## Profile Persistence Model

Profiles are stored as INI files:
- **Windows:** `%APPDATA%\sWinShortcuts\Win.ini`
- **Color:** `%APPDATA%\sWinShortcuts\Color.ini`
- **Custom:** `%APPDATA%\sWinShortcuts\Profiles\{SanitizedName}.ini`

**INI format notes:**
- Alt+Mouse bindings migrated from `AltMouse.Left/Right/Middle` sections to `AltMouseBindings` section (pipe-delimited: `TapKey|HoldKey`)
- Combined mappings stored as `SourceKey|TargetKey|SuppressOriginal|RightClickOnly`
- Color settings per display stored as pipe-delimited values

When modifying serialization, ensure backward compatibility or handle migration.

## Common Modification Patterns

### Adding a New Per-Profile Feature

1. Create settings class in `Models/` (e.g., `MyFeatureSettings`)
2. Add property to `Profile.cs:`
   ```csharp
   public MyFeatureSettings MyFeature { get; init; } = new();
   ```
3. Initialize in `ProfileFactory.CreateCustomProfile()` (usually disabled by default)
4. Add deserialization in `IniProfileStore.DeserializeMyFeature()`
5. Add serialization in `IniProfileStore.SerializeProfile()`
6. Add UI in `ProfileViewModel` and corresponding XAML
7. Implement hook logic in `InputHookService` (create handler method, call from callback)

### Adding a New Global Setting

1. Add property to `App.xaml.cs` or create a new settings service
2. Persist in `MainWindow`'s INI file (`%APPDATA%\sWinShortcuts\sWinShortcuts.ini`)
3. Load/Save via `LoadWindowState()` / `SaveWindowState()` pattern

## Known Limitations & Gotchas

### Foreground Detection
- Some processes (e.g., UAC dialogs, protected system processes) may not expose their executable path
- Falls back to process name only if `MainModule.FileName` throws
- Empty process name results in no profile match (falls back to Windows profile behavior)

### Display Color Control
- NVAPI digital vibrance only works on NVIDIA GPUs
- Gamma ramp works on all systems but provides less precise control
- Multiple monitors: Each display has its own color profile in settings

### Process Launching
- **De-elevation:** If the app is running as admin and launches a non-admin process, it uses COM `Shell.Application` to de-elevate (complex logic in `ProcessLauncher.LaunchAsDesktopUser`)
- **Arguments:** Empty string handling matters - COM treats `""` differently from `null` for optional parameters

### Logging
- Debug logs written to `%TEMP%\sWinShortcuts_AltMouse_Debug.log` when enabled
- Toggle via Settings dialog (stored in INI as `App.EnableDebugLogging`)
- **String interpolation happens even if disabled** - potential performance issue in hot paths

## Testing Hook Changes

When modifying `InputHookService`:

1. Test with multiple rapid input events (ensure no stuck keys)
2. Test profile switching while keys are held (ensure state is released)
3. Test with Alt pressed during various operations
4. Check for memory leaks (timers, callbacks)
5. Verify `SendInput` loops don't occur (INPUT_IGNORE marker)

## File Structure Notes

- `Factories/`: Factory methods for creating profile instances
- `ViewModels/`: MVVM view models; `ViewModelBase.cs` is common base
- `Services/`: Business logic and Windows API integration
- `Interop/`: P/Invoke declarations (kept separate for maintainability)
- `Utilities/`: Helpers (KeySerializer, ProcessLauncher)
- `Converters/` and `Behaviors/`: WPF UI glue code
- `Models/`: Domain models and settings classes
