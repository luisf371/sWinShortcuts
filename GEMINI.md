# sWinShortcuts

A low-level Windows keyboard and mouse remapper with per-application profiles and a WPF front-end. It features a lightweight background service that swaps input behaviors based on the active foreground window.

## Project Overview

*   **Type:** C# WPF Application (.NET 8.0)
*   **Architecture:** MVVM (Model-View-ViewModel) with Dependency Injection via `Microsoft.Extensions.Hosting`.
*   **Core Library:** `CommunityToolkit.Mvvm` for MVVM patterns.
*   **Persistence:** INI files stored in `%APPDATA%\sWinShortcuts\Profiles`.

## Key Components

*   **Services:**
    *   `ProfileActivationService`: Hosted service that orchestrates background logic (hooks, watchers).
    *   `ForegroundWatcher`: Monitors the active window to switch profiles.
    *   `InputHookService`: Installs low-level keyboard/mouse hooks (`WH_KEYBOARD_LL`, `WH_MOUSE_LL`) to intercept and modify input.
    *   `ProfileManager`: Manages profile lifecycle (CRUD) and ensures default profiles exist.
*   **Data Models:**
    *   `Profile`: Represents a set of mappings for a specific application (or global Windows defaults).
    *   `WindowsLauncherSettings`: Configuration for the grid-based launcher (Numpad keys).
*   **UI:**
    *   `MainWindow`: The primary configuration interface.
    *   `SystemTrayService`: Manages the notification area icon and context menu.

## Build & Run

**Prerequisites:**
*   .NET 8 SDK
*   Windows 10/11 (Desktop Experience)

**Build:**
```powershell
dotnet build sWinShortcuts/sWinShortcuts.csproj
```

**Run:**
```powershell
dotnet run --project sWinShortcuts/sWinShortcuts.csproj
```

**Publish (Standalone Single File):**
```powershell
dotnet publish sWinShortcuts.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true
```

## Development Conventions

*   **Dependency Injection:** Services are registered in `App.xaml.cs` using `Microsoft.Extensions.DependencyInjection`.
*   **MVVM:** ViewModels inherit from `ObservableObject` (via `ViewModelBase` or directly).
*   **Async/Await:** Heavy I/O or long-running tasks should be asynchronous. `ProfileManager` uses `SemaphoreSlim` for thread-safe async file operations.
*   **Interop:** Native P/Invoke calls are isolated in `Interop/NativeMethods.cs`.
*   **Coding Style:** Follow standard C# conventions. Use file-scoped namespaces.

***NEVER COMMIT***
Do not Commit without being specifically asked by the user.