# PROJECT KNOWLEDGE BASE

**Generated:** 2026-01-19
**Commit:** e3bdec7
**Branch:** fixupv2

## OVERVIEW

Windows keyboard/mouse remapping app (.NET 8 WPF) using low-level hooks (`WH_KEYBOARD_LL`, `WH_MOUSE_LL`). Profiles activate per-executable for Alt+Mouse gestures, right-click chords, Caps Lock modes, Windows Launcher.

## STRUCTURE

```
sWinShortcuts/
├── Services/         # Business logic, hooks, Windows API (19 files)
├── ViewModels/       # MVVM with CommunityToolkit.Mvvm (11 files)
├── Models/           # Domain models, settings classes (11 files)
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
| Input hook logic | `Services/InputHookService.cs` | 1199 lines, lock-free hot path |
| Profile persistence | `Configuration/IniProfileStore.cs` | INI format, handle migrations |
| P/Invoke declarations | `Interop/NativeMethods.cs` | Keep centralized |
| Service registration | `App.xaml.cs` | DI via Microsoft.Extensions.Hosting |
| Add test | `Tests/` + use `Tests/Fakes/` | Manual fakes, no mocking libs |

## CODE MAP

### Critical Components

| File | Lines | Role | Caution |
|------|-------|------|---------|
| `InputHookService.cs` | 1199 | Keyboard/mouse callbacks | Lock-free, zero GC in callbacks |
| `ProfileViewModel.cs` | 529 | Profile editor VM | Auto-saves on property change |
| `IniProfileStore.cs` | 508 | Profile serialization | Backward compat migrations |
| `NvidiaColorControlService.cs` | 453 | Display color control | Graceful NVAPI fallback |
| `MainViewModel.cs` | 408 | Profile list management | - |
| `MainWindow.xaml.cs` | 399 | UI code-behind | - |

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
// (ProcessLauncher.cs:29)

// Deprecated but kept for compat:
// SelectedDisplayId in IniProfileStore (lines 411, 435)
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
- Debug log: `%TEMP%\sWinShortcuts_AltMouse_Debug.log`
