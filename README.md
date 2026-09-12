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
- **Window [Default] profile**: the single built-in, undeletable global profile. Its input
  and color settings apply everywhere a game profile doesn't override them.
- **Human-readable storage**: every profile is a plain `.ini` file you can edit by hand,
  back up, or share.

### Remapping & Input

- **Alt + Mouse shortcuts** — while Alt is held, every mouse button becomes two shortcuts:
  *tapping* it and *holding* it, each mappable to any key (150 ms threshold by default
  decides which one you meant).
  - *FPS example: Alt + tap Mouse4 → throw grenade · Alt + hold Mouse5 → hold melee.*
- **Alt + Keyboard shortcuts** — the same tap/hold idea driven by keys instead of mouse
  buttons: while Alt is held, *tapping* a bound key and *holding* it each fire their own
  mapped key (same 150 ms threshold). Handy when a game reads mouse input in a way that
  sidesteps Alt+Mouse suppression.
  - *FPS example: Alt + tap `Q` → throw grenade · Alt + hold `E` → hold melee.*
- **Key remaps** — map any key to any other key. The original key is suppressed by default,
  so the remap fully replaces it (you can let the original through if you want both
  behaviors). Remaps can also be scoped to **only while right mouse is held** — your normal
  keyboard stays completely untouched until you aim.
  - *FPS example: while aiming, `E` → `4` to pull out equipment — the rest of the time `E`
    still does its normal interact.*
- **Mouse wheel shortcuts** — select **Wheel Up** or **Wheel Down** as a Key Mapping
  source to tap a key, optionally with **Right Click Only** checked. Alt + Mouse also
  includes Wheel Up/Down in its shortcut dropdown; wheel rows use **Tap**, with **Hold** disabled.
- **Caps Lock repurposing** — **Normal** mirrors Caps Lock down/up; **2x Normal** sends
  one full key tap on press and another on release; **Disabled** suppresses the key.
  **Remap Key** substitutes your chosen output key for Caps Lock.
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
  mouse button is held, with configurable delay, Hold/Toggle modes, and an optional Early
  Cancel trigger to cancel instantly (only the successful cancel press is blocked; later
  presses pass through until the next aim — releasing and pressing right mouse re-arms it).
- **Anti-AFK** — sends a short WASD ripple at a set interval (1–15 min). **Foreground**
  and **Background** modes require keyboard inactivity; any keypress resets that idle
  timer. **Forced** mode sends to the game window without waiting for keyboard inactivity,
  while respecting the configured interval.
- **Crosshair overlay** — puts a custom crosshair in the center of the screen: use the
  bundled one or your own image. Can hide automatically while you aim. Each profile has
  **Offset X/Y** sliders (−500 to +500 physical pixels; positive X moves right, positive Y
  moves down). Assign **Crosshair offset toggle key** in **Settings → Hotkeys** to switch
  between centered and the saved offset. Each profile starts centered for a new app session
  and remembers its selected mode when you switch away and return. Ordinary edits keep
  the mode; disabling the active crosshair resets it to centered.

### Display Color

- **Per-monitor brightness, contrast, and gamma** applied through GDI gamma ramps — no
  driver needed.
- **Digital Vibrance** per monitor on supported NVIDIA and AMD GPUs. AMD support requires
  [Radeon Software Adrenalin 25.3.1 or later](https://gpuopen-librariesandsdks.github.io/adl/).
  Unsupported GPUs or unavailable driver APIs retain brightness/contrast/gamma controls.
- **Primary + Secondary presets** per profile (e.g. normal vs. vibrant looks), flipped live
  with a global app-level hotkey.

## Getting Started

### Requirements

| | |
|---|---|
| OS | Windows 10 or 11 (x64) |
| Install from a release | [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Build from source | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Optional | Supported NVIDIA driver or AMD Radeon Software Adrenalin 25.3.1+ for Digital Vibrance · Administrator rights for remapping input inside elevated windows |

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
2. **Configure** — enable the features you want for that app: Alt+Mouse / Alt+Keyboard
   bindings, remaps, auto-run, rapid fire, color presets, etc. Changes save automatically.
3. **Switch** — focus the target application and the profile activates on its own. The
   tray icon and title reflect what's active.

> **Tip:** launch sWinShortcuts as Administrator if you want remaps to work inside
> programs that are themselves running elevated (Task Manager, some game launchers, admin
> consoles).

### Mouse Wheel Mappings

In a custom profile, enable **Key Mapping**, add a row, and choose **Wheel Up** or
**Wheel Down** as its source. Choose a keyboard target and check **Right Click Only**
if it should work only while the right mouse button is held. For an Alt gesture,
enable **Alt + Mouse**, add a binding, choose **Wheel Up** or **Wheel Down** in its
shortcut dropdown, and set **Tap** to the target key. **Hold** is disabled for wheel
rows. An assigned Alt gesture takes priority while Alt is held; otherwise an eligible
Key Mapping can run.

Each wheel increment taps the target once. Small scroll movements accumulate until
they reach one increment; changing direction or gesture context clears the partial
movement. Unbound scroll works normally. Key Mapping suppresses mapped scroll by
default; Advanced Mode lets you turn suppression off and keep scrolling as well.

Held modifiers stay held, so **Alt + Wheel Up → E may be received as Alt + E**.
Wheel shortcuts skip targets already detected as held and discard excess or stale
taps during fast scrolling. They support vertical scrolling and keyboard tap targets.
When using **Background Auto Run**, choose wheel targets different from its movement
and sprint keys: wheel output can interrupt those background holds.

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

Elevated startup is scoped to your Windows account and runs at your logon, including
on battery power, without a time limit. Existing startup tasks are adopted only when
their principal belongs to your account; other users' tasks are left alone. To apply
the battery and time-limit settings to an older task, run the app as administrator,
turn **Start with Windows** off and save, then turn it back on with **run as
administrator** and save again.

### Special Profiles

| Profile | Role |
|---|---|
| **Window [Default]** | The single built-in global fallback, including global display color settings. Its settings apply in every app that doesn't have its own profile. Cannot be deleted. |
| Your profiles | Matched against the foreground executable (name or full path). |

## Data & Configuration Files

Everything lives under `%APPDATA%\sWinShortcuts\`:

| File | Contents |
|---|---|
| `sWinShortcuts.ini` | App-level settings (`[App]` toggle keys, start-minimized) and window state |
| `Profiles\<Name>.ini` | One file per profile — all feature settings |
| `Win.ini` | The built-in `Window [Default]` global profile, including display color settings |
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
- **Digital Vibrance slider does nothing** — check for a supported NVIDIA driver or AMD
  Radeon Software Adrenalin 25.3.1+. Unsupported GPUs or unavailable driver APIs skip
  vibrance; brightness/contrast/gamma controls remain available.
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
Supported AMD GPUs also provide vibrance through Radeon Software Adrenalin 25.3.1+.
Other GPUs retain brightness/contrast/gamma controls; vibrance requires a supported
NVIDIA or AMD driver API.

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
