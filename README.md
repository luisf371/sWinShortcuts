# sWinShortcuts

Low-level Windows keyboard and mouse remapper with per-application profiles and a WPF front-end.

## Overview
sWinShortcuts keeps a lightweight background service that watches the active foreground window and swaps keyboard or mouse behavior based on the profile that matches the focused process. It is aimed at power users who want macOS-style navigation on Windows, custom right-button chords, or quick application launchers without running a heavyweight automation suite.

## Features
- **Per-app profiles** – Bind different behaviors to each executable; optional global “Windows” profile acts as fallback.
- **Alt+Mouse remapping** – Map Alt+Click/Tap/Hold gestures to keyboard keys (e.g., Alt+Right drag → `Ctrl+Wheel`).
- **Right-button overrides** – Convert right-button + key chords into other key presses or suppress the original key.
- **Caps Lock modes** – Toggle, disable, remap to another key, or enable “momentary Shift”.
- **Windows launcher grid** – Use the numpad (or other keys) as global launchers with optional admin elevation.
- **Tray integration** – Runs minimized with notify-icon status and quick access to the main window.
- **Profile persistence** – Stores settings as INI files under `%APPDATA%\sWinShortcuts\Profiles`.

## How It Works
- `ProfileActivationService` wires together the long-running services: it starts the foreground watcher, installs the low-level hooks, and keeps the tray icon in sync.
- `ForegroundWatcher` listens for `EVENT_SYSTEM_FOREGROUND`; when a window changes it resolves the owning process and asks `ProfileManager` for a matching profile.
- `ProfileManager` loads/saves profile objects using the `IniProfileStore`, enforcing uniqueness on profile names and executables.
- `InputHookService` installs global `WH_KEYBOARD_LL` and `WH_MOUSE_LL` hooks. It interprets events, applies Alt+mouse bindings, right-button overrides, Caps Lock modes, and synthesizes input via `SendInput`.
- The WPF UI (MVVM via `CommunityToolkit.Mvvm`) presents profile editors (`MainViewModel`, `ProfileViewModel`) that manipulate model objects and auto-save on change.

## Getting Started
### Prerequisites
- Windows 10/11 with desktop experience (hooks rely on `user32.dll`).
- .NET 8 SDK (version specified by `sWinShortcuts.csproj`).
- Build tools that support WPF (`UseWPF` is enabled).

### Build
```powershell
pwsh -NoLogo -Command "dotnet build sWinShortcuts/sWinShortcuts.csproj"
```

### Run
```powershell
pwsh -NoLogo -Command "dotnet run --project sWinShortcuts/sWinShortcuts.csproj"
```

The app starts minimized to the tray on launch. Double-click the tray icon (or choose **Open**) to show the main window.

## Usage Highlights
- **Windows Profile:** Acts as the global defaults (e.g., numpad launchers, Caps Lock mode); cannot be deleted.
- **Custom Profiles:** Assign an executable path (full path or name) and toggle features per profile.
- **Alt Mouse Bindings:** Configure tap vs. hold behavior per mouse button and assign target keys.
- **Right Mouse Overrides:** Specify source keys paired with the right button; optionally suppress the original key press.
- **Caps Lock:** Choose between normal toggle, disabling, momentary Shift, or remapping to another key.
- **Launchers:** Map keys (default numpad set) to file paths with optional command-line arguments and elevation.

## Data & Configuration
- Profiles live as INI files in `%APPDATA%\sWinShortcuts\Profiles`.
- The global “Windows” profile is stored as `%APPDATA%\sWinShortcuts\Win.ini`.
- The alt-mouse hook writes diagnostics to `%TEMP%\sWinShortcuts_AltMouse_Debug.log` when events occur.

## Performance & Footprint
- The app installs a single keyboard and mouse hook, and a foreground window event hook. In steady state, CPU usage remains near idle because work only happens on actual input events or window changes.
- Memory footprint is similar to other WPF tray apps (~tens of MB) due to the .NET 8 runtime and WPF stack.
- Disk I/O is minimal; configuration persists on change and hook debugging appends small log entries.
- Resource spikes can occur if custom bindings trigger bursts of synthesized input, but the logic throttles key sends with short sleeps to avoid flooding.
- Compared to full automation suites (AutoHotkey, PowerToys), sWinShortcuts is lightweight: no scripting engine, no polling loops, and limited background threads (mostly hosted services and dispatcher).

## Architecture Notes
- Built as a single WPF project targeting `net8.0-windows`, using `Microsoft.Extensions.Hosting` to compose services.
- Services are registered in `App.xaml.cs` (dependency injection) and kept alive by the generic host.
- MVVM layers (`ViewModels`, `Models`, `Services`) cleanly separate UI concerns from hook logic.
- Native interop is isolated in `Interop/NativeMethods.cs`, keeping P/Invoke calls centralized.

## Limitations & Considerations
- Requires foreground detection permission; some protected processes may not expose their module path.
- Hooks run at the process level; running other global hook tools may lead to conflicts.
- Running elevated is optional, but launching elevated apps from the tray may prompt UAC.
- The diagnostics log can grow over time; consider pruning `%TEMP%\sWinShortcuts_AltMouse_Debug.log` if needed.

## Contributing
1. Fork the repo and create a branch.
2. Install the .NET 8 SDK and ensure WPF workloads are available.
3. Run `dotnet build` before submitting PRs; add tests or repro steps for hook-related changes.
4. Keep hooks efficient—avoid long-running work inside low-level callbacks.

## License
_Add your preferred license here (none specified in the current repository)._
