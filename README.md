<div align="center">

# sWinShortcuts

**Per-application keyboard & mouse remapping for Windows**

[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D6)](https://github.com/luisf371/sWinShortcuts)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![UI](https://img.shields.io/badge/UI-WPF-68217A)](https://learn.microsoft.com/dotnet/desktop/wpf/)
[![License](https://img.shields.io/badge/license-TBD-lightgrey)](#license)

</div>

sWinShortcuts is a tray-based Windows utility that remaps keys and mouse buttons using
low-level input hooks (`WH_KEYBOARD_LL` / `WH_MOUSE_LL`). You define a **profile** per
executable, and the right shortcuts and display settings activate automatically the
moment that program comes to the foreground.

## Table of Contents

- [Features](#features)
- [Getting Started](#getting-started)
- [Usage](#usage)
- [Data & Configuration Files](#data--configuration-files)
- [Project Layout](#project-layout)
- [Troubleshooting](#troubleshooting)
- [FAQ](#faq)
- [Contributing](#contributing)
- [License](#license)

## Features

### Profiles

- **Application-aware**: profiles activate automatically based on the focused executable —
  no manual switching.
- **Windows profile**: a built-in, undeletable global profile whose settings apply everywhere
  a game profile doesn't override them.
- **Human-readable storage**: every profile is a plain `.ini` file you can edit by hand,
  back up, or share.

### Remapping & Input

- **Alt + Mouse shortcuts** — while Alt is held, every mouse button becomes two shortcuts:
  *tapping* it and *holding* it, each mappable to any key (50 ms threshold by default
  decides which one you meant).
  - *FPS example: Alt + tap Mouse4 → throw grenade · Alt + hold Mouse5 → hold melee.*
- **Key remaps** — map any key to any other key. The original key is suppressed by default,
  so the remap fully replaces it (you can let the original through if you want both
  behaviors). Remaps can also be scoped to **only while right mouse is held** — your normal
  keyboard stays completely untouched until you aim.
  - *FPS example: while aiming, `E` → `4` to pull out equipment — the rest of the time `E`
    still does its normal interact.*
- **Caps Lock repurposing** — disable Caps Lock entirely, or remap it to fire on hold or on
  double-tap.
  - *FPS example: double-tap CapsLock → go prone · hold CapsLock → melee.*
- **Windows Launcher** — `Win + Numpad` shortcuts launch any program, file, or folder, with
  optional arguments and run-as-admin.
  - *Example: `Win+Numpad1` launches your main game with its launch options.*

### Gaming Assists

- **Auto Run** — toggle continuous forward movement with a hotkey (default `Ctrl+R`), with
  optional sprint in hold or press mode. By default it sends to the focused window; it can
  also target the game while it's in the background (experimental — support depends on the
  game).
- **Rapid Fire** — an auto-clicker with a 25–250 ms interval and up to 20 ms of random
  jitter. Enabled per profile and armed/disarmed globally with a hotkey.
- **Hold Breath** — automatically presses a key (default `Left Shift`) while the right
  mouse button is held, with configurable delay, Hold/Toggle modes, and a panic trigger to
  cancel instantly.
- **Anti-AFK** — presses a key at a set interval (1–15 min), but *only* after real keyboard
  inactivity: any keypress resets the timer, so it never fires while you're actually
  playing.
- **Crosshair overlay** — puts a custom crosshair in the center of the screen: use the
  bundled one or your own image. Can hide automatically while you aim.

### Display Color

- **Per-monitor brightness, contrast, and gamma** applied through GDI gamma ramps — no
  driver needed.
- **NVIDIA Digital Vibrance** per monitor (silently skipped on other GPUs).
- **Primary + Secondary presets** per profile (e.g. normal vs. vibrant looks), flipped live
  with a global app-level hotkey.

## Getting Started

### Requirements

| | |
|---|---|
| OS | Windows 10 or 11 (x64) |
| Install from a release | [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Build from source | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Optional | NVIDIA GPU for Digital Vibrance · Administrator rights for remapping input inside elevated windows |

### Install from a Release

1. Install the [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Grab `sWinShortcuts.exe` from the latest build on the
   [Releases page](https://github.com/luisf371/sWinShortcuts/releases).
3. Run it. The app lives in the system tray — double-click the tray icon to open the main
   window.

### Build from Source

```bash
git clone https://github.com/luisf371/sWinShortcuts.git
cd sWinShortcuts
dotnet publish sWinShortcuts.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true
```

That produces a single self-contained EXE — no .NET install needed on the machine you run
it on — at:

```
bin\Release\net10.0-windows\win-x64\publish\sWinShortcuts.exe
```

For quick development runs without publishing:

```bash
dotnet run --project sWinShortcuts.csproj
```

### Running the Tests

```bash
dotnet test Tests/Tests.csproj
```

Run a single test class or method:

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ProfileManagerTests"
```

## Usage

1. **Create a profile** — click *Add*, give it a name, and pick (or browse to) the target
   executable. Every setting below becomes a per-profile option.
2. **Configure** — enable the features you want for that app: Alt+Mouse bindings, remaps,
   auto-run, rapid fire, color presets, etc. Changes save automatically.
3. **Switch** — focus the target application and the profile activates on its own. The
   tray icon and title reflect what's active.

> **Tip:** launch sWinShortcuts as Administrator if you want remaps to work inside
> programs that are themselves running elevated (Task Manager, some game launchers, admin
> consoles).

### Default Quick Reference

| Feature | Default trigger |
|---|---|
| Auto Run toggle | `Ctrl + R` |
| Sprint | `Left Shift` (hold mode) |
| Hold Breath key | `Left Shift` |
| Rapid Fire | per-profile enable + global arm hotkey (set in Settings) |
| Windows Launcher | `Win +` Numpad key |
| Color preset toggle | global hotkey (set in Settings) |

### Global Settings

Open **Settings** from the tray menu or main window to configure:

- **Color toggle key** and **Rapid Fire arm key** — app-level hotkeys shared across all
  profiles (they are intentionally *not* per-profile settings).
- **Enable Debug Logging** — writes verbose input-hook tracing to `debug.log`.
- **Start minimized** and **Start with Windows** (with optional *run as administrator*).

### Special Profiles

| Profile | Role |
|---|---|
| **Windows** | Built-in global fallback. Its settings apply in every app that doesn't have its own profile. Cannot be deleted. |
| **Color Settings** | Built-in profile holding display color defaults. Cannot be deleted. |
| Your profiles | Matched against the foreground executable (name or full path). |

## Data & Configuration Files

Everything lives under `%APPDATA%\sWinShortcuts\`:

| File | Contents |
|---|---|
| `sWinShortcuts.ini` | App-level settings (`[App]` toggle keys, start-minimized) and window state |
| `Profiles\<Name>.ini` | One file per profile — all feature settings |
| `Win.ini` | The built-in Windows (global) profile |
| `Color.ini` | The built-in color profile |
| `debug.log` | Verbose debug output (when enabled in Settings) |
| `crash.log` | Crash reports |

Profiles are plain INI — edit them directly, then restart the app to apply.

## Project Layout

```
sWinShortcuts/
├── Services/         # Business logic: hooks, profile activation, tray, color, logging
├── ViewModels/       # MVVM view models (CommunityToolkit.Mvvm)
├── Models/           # Domain models and per-feature settings classes
├── Views/            # XAML dialogs (Add Profile, Settings, Crosshair overlay)
├── Configuration/    # IProfileStore + IniProfileStore (INI persistence)
├── Interop/          # NativeMethods.cs — all P/Invoke declarations
├── Utilities/        # KeySerializer, ProcessLauncher, IniDocument, startup helpers
├── Behaviors/        # WPF attached behaviors
├── Converters/       # WPF value converters
├── Factories/        # ProfileFactory
├── Resources/        # Shared brushes and styles
├── Tests/            # xUnit test project (manual fakes in Tests/Fakes, no mocking libs)
└── Icons/            # App icon, default crosshair
```

## Troubleshooting

- **Remaps don't fire in a specific app** — that app is probably running elevated. Restart
  sWinShortcuts as Administrator.
- **"Another instance is already running"** — sWinShortcuts allows only one instance per
  session; check the tray (and hidden tray icons) for the existing one.
- **Something crashed or behaved oddly** — look in `%APPDATA%\sWinShortcuts\crash.log`.
- **Digital Vibrance slider does nothing** — it's NVIDIA-only. On other GPUs the app still
  applies brightness/contrast/gamma and skips vibrance.
- **Some protected processes can't be matched by path** — the app falls back to matching by
  process name.

## FAQ

**Does it need Administrator rights?**
Not strictly, but running elevated is recommended: it's required to capture and remap input
directed at other elevated windows, and for the elevated autostart option. Everything else
works unelevated.

**Is it safe to use in competitive games?**
Any global-hook utility is visible to anti-cheat systems. sWinShortcuts adds humanization
(randomized jitter, variable press durations) to its injected input, but that is no
guarantee — use your own judgment and follow each game's rules.

**Can I edit profiles without the UI?**
Yes — they're standard INI files in `%APPDATA%\sWinShortcuts\Profiles\`. Edit, save,
restart the app.

**When I launch something from Windows Launcher, does it run as admin?**
Only if the item says so. Items without run-as-admin always launch as your normal desktop
user, even when sWinShortcuts itself is running elevated.

**What if I don't have an NVIDIA GPU?**
Digital Vibrance is skipped; brightness/contrast/gamma still work on any GPU.

## Contributing

Issues and pull requests are welcome. A few project conventions to know:

- Tests are xUnit, named `MethodName_Scenario_ExpectedResult`, with hand-written fakes in
  `Tests/Fakes/` (no mocking libraries).
- Never add allocations, locks, or long-running work inside the input hook callbacks.
- All P/Invoke goes in `Interop/NativeMethods.cs`; all INI I/O goes through
  `Utilities/IniExtensions.cs` with invariant culture.
- See `AGENTS.md` for the full engineering knowledge base (code map, patterns, anti-patterns).

## License

To be determined — no license has been chosen yet, so the code is all-rights-reserved by
default. If you want to use it, open an issue and let's talk.
