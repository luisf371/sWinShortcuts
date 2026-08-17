# PROJECT KNOWLEDGE BASE

**Generated:** 2026-01-19 (structure counts, code map, and notes refreshed 2026-08-14)
**Commit:** e3bdec7 (refresh at 1e2436f)
**Branch:** fixupv2 (refresh on tekky/bug-hunt-a5364214)

## OVERVIEW

Windows keyboard/mouse remapping app (.NET 10 WPF) using low-level hooks (`WH_KEYBOARD_LL`, `WH_MOUSE_LL`). Profiles activate per-executable for Alt+Mouse gestures, right-click chords, Caps Lock modes, Windows Launcher.

## STRUCTURE

```
sWinShortcuts/
├── Services/         # Business logic, hooks, Windows API (24 files)
├── ViewModels/       # MVVM with CommunityToolkit.Mvvm (11 files)
├── Models/           # Domain models, settings classes (17 files)
├── Utilities/        # Helpers: KeySerializer, ProcessLauncher, IniDocument
├── Configuration/    # IniProfileStore - INI-based persistence
├── Interop/          # NativeMethods.cs - ALL P/Invoke centralized
├── Converters/       # WPF value converters
├── Behaviors/        # WPF behaviors (ComboBox, MouseWheel)
├── Factories/        # ProfileFactory
├── Views/            # XAML dialogs (AddProfile, Settings)
├── Resources/        # Brushes.xaml, Styles.xaml
└── Tests/            # xUnit tests (separate .csproj, nested in main)
```

## WHERE TO LOOK

| Task | Location | Notes |
|------|----------|-------|
| Add per-profile feature | `Models/` → `Profile.cs` → `ProfileFactory` → `IniProfileStore` → `ProfileViewModel` → `InputHookService` | 7-step pattern |
| Input hook logic | `Services/InputHookService.cs` | 5664 lines, lock-free hot path |
| Profile persistence | `Configuration/IniProfileStore.cs` | INI format, handle migrations |
| P/Invoke declarations | `Interop/NativeMethods.cs` | Keep centralized |
| Service registration | `App.xaml.cs` | DI via Microsoft.Extensions.Hosting |
| Add test | `Tests/` + use `Tests/Fakes/` | Manual fakes, no mocking libs |

## CODE MAP

### Critical Components

| File | Lines | Role | Caution |
|------|-------|------|---------|
| `InputHookService.cs` | 5664 | Keyboard/mouse callbacks | Lock-free, zero GC in callbacks; hosts the one-shot Rapid Fire click timer |
| `ProfileViewModel.cs` | 816 | Profile editor VM | Auto-saves on property change |
| `IniProfileStore.cs` | 790 | Profile serialization | Backward compat migrations |
| `NvidiaColorControlService.cs` | 568 | Display color control | Graceful NVAPI fallback |
| `MainViewModel.cs` | 861 | Profile list management | - |
| `MainWindow.xaml.cs` | 537 | UI code-behind | - |

### Service Interfaces

| Interface | Implementation | Purpose |
|-----------|----------------|---------|
| `IProfileManager` | `ProfileManager` | CRUD, executable matching |
| `IInputHookService` | `InputHookService` | Global hooks, input synthesis |
| `IProfileStore` | `IniProfileStore` | INI persistence |
| `IForegroundWatcher` | `ForegroundWatcher` | Window focus detection |
| `IColorControlService` | `NvidiaColorControlService` | Display gamma/vibrance |

## CONVENTIONS

### Naming
- Private fields: `_camelCase`
- Constants: `SCREAMING_SNAKE` (e.g., `TIMER_IDLE`, `KEY_PRESS_DURATION_MIN_MS`)
- Async methods: suffix `Async`

### Patterns
- Primary constructors for DI: `public sealed class ProfileManager(IProfileStore store)`
- File-scoped namespaces: `namespace sWinShortcuts.Services;`
- Collection expressions: `[]` not `new List<T>()`
- `ConfigureAwait(false)` in all service code
- Allman braces, 4-space indent

### Testing
- Test naming: `MethodName_Scenario_ExpectedResult`
- Manual fakes in `Tests/Fakes/` (no Moq/NSubstitute)
- Integration tests implement `IDisposable` for cleanup

## ANTI-PATTERNS (THIS PROJECT)

### CRITICAL: InputHookService Hot Path
```csharp
// NEVER in KeyboardCallback/MouseCallback:
- Allocations (no new objects, no LINQ, no string interpolation)
- Locks (use volatile + Interlocked only)
- Long-running operations

// ALWAYS:
- Check _isRunning first for early exit
- Use pre-allocated timers
- Guard LogDebug with IsDebugEnabled check
```

### Forbidden
```csharp
// CRITICAL: Do NOT fall back to standard launch if de-elevation fails
// (ProcessLauncher.Launch: the catch after LaunchAsDesktopUser throws by design)

// Deprecated but kept for compat:
// SelectedDisplayId in IniProfileStore (legacy [Color] SelectedDisplay read/write)
```

### Type Safety
- Never `as any` or suppress errors
- Nullability enabled project-wide

## UNIQUE STYLES

### Profile Switch State Management
`ReleaseAllState()` called on profile switch:
- Cancels all pending timers
- Releases pressed keys (sends key-up)
- Resets mouse button states
- Clears override dictionaries

### Anti-Cheat Humanization
InputHookService includes timing jitter:
- Thread-local `Random` with hybrid seeding
- RNG warmup before key injection
- Variable delays on key press duration

### Special Profiles
- **Windows Profile** (`ProfileConstants.WindowsProfileName`): Global fallback, undeletable
- **Color Profile** (`ProfileConstants.ColorProfileName`): Global color settings only

## COMMANDS

```powershell
# Build
dotnet build sWinShortcuts.csproj

# Run (starts minimized to tray)
dotnet run --project sWinShortcuts.csproj

# Test all
dotnet test Tests/Tests.csproj

# Test single class
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ProfileManagerTests"

# Test single method
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AddProfileAsync_DuplicateName"
```

## NOTES

### Project Structure Quirks
- Tests nested inside main project (excluded via .csproj `<Compile Remove="Tests\**\*.cs" />`)
- Manual `AssemblyInfo.cs` (auto-generation disabled)
- Uses both WPF and WinForms (`UseWindowsForms=true` for NotifyIcon)

### Performance Considerations
- Hooks fire on EVERY input event - keep callbacks fast
- String interpolation in logs allocates - guard with `if (IsDebugEnabled)`
- Pre-allocate `System.Threading.Timer` per mouse button

### Known Limitations
- Some protected processes don't expose executable path (falls back to process name)
- NVAPI digital vibrance only works on NVIDIA GPUs
- De-elevation uses COM Shell.Application (complex path in ProcessLauncher)

### Data Locations
- Profiles: `%APPDATA%\sWinShortcuts\Profiles\{Name}.ini`
- Windows profile: `%APPDATA%\sWinShortcuts\Win.ini`
- Color profile: `%APPDATA%\sWinShortcuts\Color.ini`
- Debug log: `%APPDATA%\sWinShortcuts\debug.log`
- Crash report: `%APPDATA%\sWinShortcuts\crash.log` (always on, independent of debug logging; capped at 512 KiB)

### App-Level Toggle Keys & Rapid Fire
- `ColorToggleKey` and `RapidFireToggleKey` are app-level toggle keys persisted in `sWinShortcuts.ini` `[App]`, assigned via Settings, and shown read-only in profile panes — they are NOT per-profile settings
- Rapid Fire arm is a sticky SINGLE-OWNER session state: it survives profile switches, same-profile republishes, watchdog hook reinstalls, and `ReleaseForegroundState`, and clicks only while its owner is the settled active profile (`ProfileInputGenerationIsCurrent` + owner == active). Toggling in another RF-capable app RE-TARGETS the owner; toggling in a settled non-eligible context (desktop, or a profile without Rapid Fire) DISARMS the live arm — the primary-key escape hatch for an owner whose game was quit, at the cost that an incidental press in an app binding the same key also disarms (pick a non-conflicting key); a toggle during a foreground-generation mismatch still fails closed
- Full disarm happens ONLY on: toggle-off, toggle-key reassignment, owner RapidFire-config/Identity edits, owner removal or master-off (both `ReconcileProfileSettings` paths), Advanced Mode off, session switch, and Stop/Start
- `RapidFireArmChanged` (may-change event; handlers re-query `GetRapidFireArmStatus` and dedup) feeds the status dot overlay (`RapidFireStatusService`): green = Ready, gray = ArmedNotReady, hidden = Off
- All INI persist/parse goes through `Utilities/IniExtensions.cs` with `CultureInfo.InvariantCulture` — never use culture-sensitive formatting in new keys
