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
  With **Keep color active** enabled, a game's colors can stay after you switch away.
- **Human-readable storage**: every profile is a plain `.ini` file you can edit by hand,
  back up, or share.

### Remapping & Input

- **Alt + Shortcut** — while Alt is held, a bound key or mouse button becomes two shortcuts:
  *tapping* it and *holding* it, each mappable to any key. One hold threshold (200 ms for
  new profiles and **Reset**) decides which one you meant: releasing before it sends Tap,
  reaching it sends Hold. Hold sends one tap of its key; it is not held until you release.
  One switch turns key, mouse button and wheel triggers on or off together, and one grid
  lists them all (mouse buttons and wheel directions first, then keys).
  - *FPS example: Alt + tap Mouse4 → throw grenade · Alt + hold `Q` → quick melee.*
- **Key remaps** — map any key to any other key. The original key is suppressed by default,
  so the remap fully replaces it (you can let the original through if you want both
  behaviors). Remaps can also be scoped to **only while right mouse is held** — your normal
  keyboard stays completely untouched until you aim.
  - *FPS example: while aiming, `E` → `4` to pull out equipment — the rest of the time `E`
    still does its normal interact.*
- **Mouse wheel shortcuts** — select **Wheel Up** or **Wheel Down** as a Key Mapping
  source to tap a key, optionally with **Right Click Only** checked. Alt + Shortcut also
  accepts Wheel Up/Down as a trigger; wheel rows use **Tap**, with **Hold** disabled.
- **Caps Lock repurposing** — **Normal** mirrors Caps Lock down/up; **2x Normal** sends
  one full key tap on press and another on release; **Disabled** suppresses the key.
  **Remap Key** substitutes your chosen output key for Caps Lock. While a remapped 2x press is
  held, the Windows key is ignored so an accidental press cannot pull focus away mid-hold.
- **Windows Launcher** — `Win + Numpad` shortcuts launch any program, file, or folder, with
  optional arguments and run-as-admin.
  - *Example: `Win+Numpad1` launches your main game with its launch options.*

### Gaming Assists

- **Macros** — create labeled, editable sequences of keyboard presses, waits, mouse
  buttons, scrolling, and coordinate clicks. Assign a keyboard shortcut to run a macro
  once in its application profile, or record physical actions and edit the resulting steps.

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
- **Up to three color presets** per app profile and in **Window [Default]**. Enable Secondary
  and Tertiary independently in the Display tab. The global color profile toggle key cycles
  through Primary, Secondary, and Tertiary, skipping unavailable presets.
- **Keep color active** is off by default in each app profile's Display tab. Turn it on to
  keep the game's current preset when you Alt+Tab to the desktop or an app without an enabled profile.
  - The hold ends when the game's process exits or the focused app matches a different enabled
    profile. A profile used only for shortcuts counts.
  - Each enabled display row applies to the whole monitor, including other apps on it. While
    the hold lasts, an enabled game row takes precedence over edits to **Window [Default]** on that monitor.
  - An enabled `explorer.exe` profile (the desktop and taskbar) ends every hold as soon as
    you reach the desktop.

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
2. **Configure** — enable the features you want for that app: Alt + Shortcut
   bindings, remaps, auto-run, rapid fire, color presets, etc. Changes save automatically.
3. **Switch** — focus the target application and the profile activates on its own. The
   tray icon and title reflect what's active.

> **Tip:** launch sWinShortcuts as Administrator if you want remaps to work inside
> programs that are themselves running elevated (Task Manager, some game launchers, admin
> consoles).

### Mouse Wheel Mappings

In a custom profile, enable **Key Mapping**, add a row, click **Assign** in its source and
scroll the wheel once over the field. Assign a keyboard target and check **Right Click Only**
if it should work only while the right mouse button is held. For an Alt gesture,
enable **Alt + Shortcut**, add a binding, click **Assign** in its trigger and scroll over
the field, and set **Tap** to the target key. **Hold** is disabled for wheel
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

### Macros

Open an application profile's **Macros** tab, immediately after **Advanced**. Enable
macros for the profile, choose **New**, give the macro a label, and assign its key or mouse
button with optional Ctrl/Alt/Shift/Win modifiers. New macros start enabled; clear
**On** beside the macro list to keep one from responding. Playback requires
**Advanced Mode** and runs only while that profile's application is in the foreground.
Shortcut conflicts and incomplete sequences are shown in the editor and cannot run.

Two header rows sit above the sequence. The first picks the macro: the macro list, its own
**On** switch and status (**Ready**, **Off**, a short reason, or **Fix step N** when one step
needs attention), **New…**, and **⋯** for **Rename…**, **Duplicate**, **Export to clipboard**,
**Import from clipboard**, **Clear all steps…**, and **Delete macro…**. The second sets when it runs: the Ctrl/Alt/Shift/Win modifiers, the key or
mouse button, and the **Playback** button, whose label shows what is set (for example **Once**
or **Loop · on press**). Its menu holds **Play once per press** or **Loop until pressed
again**, **Start when released** or **Start when pressed**, and **Stop when the mouse moves**.
**Delete macro…** asks first when the macro has steps. Duplicating a macro adds the next
available number to its name, such as **Ammo 1** and **Ammo 2**, and the copy starts off
without a shortcut.

**Export to clipboard** copies the selected macro as a code that starts with `SWSM:` and ends with
`:74656B6B79:`. **Import from clipboard** accepts one complete code at a time. Line breaks between the
markers are allowed. An incomplete or invalid code shows an error. The code leaves out the shortcut
and the **On** state, so an imported macro starts off without a shortcut. It keeps
its name, or gets the next free number when another macro already uses that name. A code made
on a different screen layout keeps its screen positions and shows a warning with how many fall
outside your screens. Codes from a newer version of the app ask you to update first.

The sequence stays beside an inspector for the selected step. New steps and recordings go
at the end unless you select a step, which inserts after it; the **New steps go** hint in
the toolbar shows where. **Add step** lets you choose an action directly: a new **Wait**
starts at 100 ms. A new **Move** starts in **Position** mode. Both **Move** and **Mouse click**
start at the macro's nearest earlier position. **Clear all steps…** (under **⋯**) removes every
step after asking, for example to record a macro again; its name, shortcut, and options stay.
With the step list focused, use **Delete** to remove a step, **Ctrl+D** to duplicate it,
and **Alt+Up/Down** to reorder it. **Fix step N** jumps to the row named in a validation error;
that row shows a warning mark, and the step editor shows the full message.
Change a key by clicking **Assign** beside it and pressing the new key.

The list shows **Presses** by default; switch to **All steps** above the list to see every
event. With **Presses**, adjacent matching Down, Wait, and Up events appear as one
press with their original step numbers and hold time. Intervening actions keep the events
separate. Edit a grouped press's key/button and hold directly, or use **Show all steps**
to edit the original events. Moving, duplicating, or deleting a collapsed row operates on
all three. This changes the view only: saved steps,
playback, and the 1,000-step limit still use the original events. A Wait that holds a press
shows its time as a hold even when presses are expanded.

**Wait times…** turns the sequence toolbar into a one-row bar, as **Record** does for its
banner, so the step list keeps its size. Choose **Press holds** (how long each key or button
stays down: the Wait inside a press, plus Key press and Mouse click holds), **Between steps**
(Wait steps that pause between actions), or **All**. Each choice shows how many rows it
changes, and the list highlights those rows' times while the bar is open. Enter a duration
from **0 to 3,600,000 ms** and choose **Apply** (or press Enter in the duration field). It opens on **Press holds** with a suggested 50 ms, enough for most
games to register a press, so the pauses that set the macro's rhythm keep their recorded
timing. A recorded click's hold counts as a press hold even though the click re-anchors the
cursor before releasing; waits around a key's typematic repeats or other input count as
between steps. The bar's close button or Escape discards the input; switching macros, leaving the
editor, or starting a recording closes it automatically.

Use the step editor to insert, duplicate, delete, and reorder key presses, explicit
key/button down and up events, waits, mouse clicks, cursor moves (to a position or by an offset), vertical or
horizontal scrolling, and **Freeze mouse**/**Unfreeze mouse** steps. A press or click with a hold of **0 ms** uses the existing automatic
hold timing; explicit waits and holds keep their entered durations. Explicit down/up
sequences must balance before playback is enabled.

**Record** captures physical keyboard/button/wheel actions and their timing once the
profile's application is in front: choose **Record**, then switch to the application (the
banner names it while it waits). The click or Alt+Tab that brings it forward is not
recorded, and an input held across that moment is skipped until released. **F12** or **Stop
recording** ends the take, which goes where **New steps go** shows and leaves its last row
selected, so the next take follows it; edit out unwanted actions afterward. Recording
pauses other automation, leaves the hooks installed, and
does not capture free cursor movement. Interrupted or capacity-limited takes retain
their captured prefix and add release rows for inputs still held. Each macro supports
1,000 steps, each profile supports 64 macros, and one recording lasts at most 10 minutes.

**Move** in **Position** mode moves to a fixed screen position. **Position** and **Mouse click**
use signed physical screen pixels, including monitors left of or above the primary display.
**Pick in 3 s** gives you three seconds to move the cursor and shows a countdown beside it; the
Windows cursor itself never changes. While it runs, the button becomes **Cancel**. Playback travels along a quick, slightly curved path with a shared
9,000-pixel/second speed cap and checks arrival before clicking. Travel adds time to
the saved waits. Clipped cursors, changed display geometry, and paths crossing monitor gaps stop playback with
an explanation; the app does not remove a game's cursor confinement.

One shortcut press runs the sequence once, after the launch chord is released. Further
macro shortcuts are ignored while a run is busy. Physical modifiers pause new output
until released; they can still affect keys already held in the target app. **F12** is a
temporary emergency cancellation key during playback or recording. Focus loss, profile
changes, and conflicting physical input also cancel playback and release its held inputs.
**Loop until pressed again** (the **Playback** menu, also called toggle loop) repeats a macro
until you press its shortcut again, even mid-step; there is no regular playback Stop button.

**Start when pressed** is a per-macro option, off by default. Choose it to start playback when
the shortcut goes down instead of when the chord is released; holding or releasing the
shortcut afterward neither restarts nor stops the run. Shift or Ctrl held as part of the
shortcut keeps reaching the application with the macro's keys, so a Shift+Q macro that
sends Tab arrives as Shift+Tab. A step that presses the same Shift or Ctrl key you are
holding is refused and stops the run. Alt and Win still pause output until released, because keys sent
under them trigger Windows shortcuts such as Alt+Tab or Win+D. Modifiers pressed after the
run starts pause output as usual.

**Stop when the mouse moves** is a per-macro option, off by default. Enable it to cancel
playback when you move the mouse, including during waits and held inputs. Injected
movement does not trigger this option. Coordinate actions still require arrival at the
requested pixel before continuing.

A **Freeze mouse** step blocks your physical mouse movement from that row on, so **Move**
and **Mouse click** steps are not pushed off their pixels; an **Unfreeze mouse** step lets the
mouse move again. Only physical movement is blocked: the macro's own cursor moves, your mouse
buttons, and the wheel work as usual. A freeze always ends with the run, whether it completes
or stops on **F12**, the toggle shortcut, focus loss, or a failure. In a toggle loop it
lasts across passes until an **Unfreeze mouse** step runs or the loop stops. While the mouse
is frozen, **Stop when the mouse moves** does not trigger; it applies again after **Unfreeze
mouse**. The freeze stops the Windows cursor, but games that read raw mouse input may still
see the motion.

**Move** in **Offset** mode uses a distance from where the cursor is when the step runs.
Positive X moves right. Positive Y moves down. Switch between **Position** and **Offset**
to edit each mode's own X and Y values. **Offset** has two choices:

- **Pixels** moves the on-screen cursor by an exact screen distance. It uses the same smooth
  movement and arrival check as **Position**. A destination off every screen stops the macro.
- **Raw** sends movement the way a real mouse does. Use it to turn the camera in games that
  read the mouse directly. It moves smoothly at the same speed as **Position**. The game's
  sensitivity controls the turn distance. Windows mouse speed controls the distance on the
  desktop. Windows marks the movement as software-generated, and some games ignore it.

**Measure**, beside **Pixels** and **Raw**, fills in the offset for you. You get three seconds to
point at where the move starts. Then a ruler shows the distance from there until you click where
the move should end. **Esc**, a right click, or 30 seconds cancels and keeps the old offset. With
**Raw**, the shown distance is approximate, because the game's sensitivity decides how far Raw
movement turns. The ruler takes focus while it is open, so a game may minimize or pause.

A **Save position** step stores the cursor's current screen position in one of eight slots, **#1** through **#8**.
Set a **Move** step to **Saved** and choose the same slot to return there. A Saved move must come after a
**Save position** step with the same number. It stops the macro if the saved spot is no longer on a screen.
Games that hide and recenter the cursor save the recentered spot. Put a **10 ms Wait** between a **Raw** move and a **Save position** step.

Every **Move** step has an **If a mouse button is held** choice. **Stop** is the default and
stops the macro at that step. **Wait** pauses until every mouse button is released.
**Move anyway** moves with the button held, which drags on the desktop. The macro's own shortcut button never counts as held, and keyboard keys never block
a move.

With **Enable Debug Logging** on, macro diagnostics include session states, step numbers
and action types, recording completion, and cancellation/failure reasons. A cursor-arrival
failure also logs the requested and observed pixels, elapsed settling time, and monitor bounds.

Auto-Run must finish before playback can start. Rapid Fire and Anti-AFK wait until the
macro finishes; the Rapid Fire arm is preserved. Macros use the same Windows input
transport as the existing features, so game acceptance and exact scheduling remain
application dependent. Unreadable or malformed macro sections suspend saves for that
profile to protect the original file; correct the file and restart to recover.

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

### Assigning Keys

Every key field is a read-only box with an **Assign** button (and **✕** to clear it where
"None" is allowed). The box is dimmed because you can't type in it. Click **Assign**: the box
shows **Capturing keys…**, then the keys you are holding, until you release the key or
combination. Modifiers always come first (**LCtrl+E**, never E+LCtrl), in the
order Ctrl, Alt, Shift, Win, whichever order you pressed them, and they keep the side you
pressed: **LCtrl+S** fires only with Left Ctrl, and **RCtrl+S** is a different shortcut.
Pressing only a modifier assigns that key itself, such as **LShift** for Sprint. Alt + Shortcut
triggers, macro shortcuts and Hold-breath Early Cancel also capture a mouse button pressed over
the box. Alt + Shortcut triggers and Key Mapping sources capture a wheel notch scrolled over it.
Press **Esc**, click **Cancel**, click outside the box, or switch windows to keep the old value.
**Esc** only cancels and can't be assigned, except in a macro step's **Key** box: there Esc is
assigned like any other key, so cancel with **Cancel** or a click outside the box. Click **✕** to clear a key.
Capture also ends after 10 seconds without input or 30 seconds overall. While a capture runs,
keystrokes go only to the box, so shortcuts such as Win+E, Alt+Tab and the app's own hotkeys
don't fire. If input hooks aren't running, the box captures through the window instead and
Windows shortcuts can still react.

**After upgrading, reconfirm your keybinds in Settings and in each profile.** Shortcuts saved
by older versions didn't record a side and now count as left-hand keys, so the default Auto-Run
**Ctrl+R** becomes **LCtrl+R**. If a shortcut looks wrong, click **Assign** and enter it again.

Elevated startup is scoped to your Windows account and runs at your logon, including
on battery power, without a time limit. Existing startup tasks are adopted only when
their principal belongs to your account; other users' tasks are left alone. To apply
the battery and time-limit settings to an older task, run the app as administrator,
turn **Start with Windows** off and save, then turn it back on with **run as
administrator** and save again.

### Special Profiles

| Profile | Role |
|---|---|
| **Window [Default]** | The single built-in global fallback, including global display color settings. Its settings apply in every app that doesn't have its own profile, except where **Keep color active** holds a game's colors after you switch away. Cannot be deleted. |
| Your profiles | Matched against the foreground executable (name or full path). |

## Data & Configuration Files

Everything lives under `%APPDATA%\sWinShortcuts\`:

| File | Contents |
|---|---|
| `sWinShortcuts.ini` | App-level settings (`[App]` toggle keys, start-minimized) and window state |
| `Profiles\<Name>.ini` | One file per profile — all feature settings |
| `Profiles\<Name>.ini.pre-alt-shortcut.bak` | One-time copy of a profile from before the Alt + Shortcut merge |
| `Win.ini` | The built-in `Window [Default]` global profile, including display color settings |
| `debug.log` | Verbose debug output (when enabled in Settings) |
| `crash.log` | Crash reports |

Profiles are plain INI — edit them directly, then restart the app to apply.

### Alt + Shortcut conversion

Alt + Shortcut settings are stored in `[AltShortcut]` (`Version`, `Enabled`, `HoldThreshold`)
and `[AltShortcutBindings]` (`Mouse:Left=F|G`, `Wheel:Up=E|`, `Key:Q=Z|X`; `Tap|Hold`, either
side empty). Profiles saved by older builds have separate `[AltMouse]` and `[AltKeyboard]`
sections; they are converted when loaded, and nothing changes on disk until the next save:

- **On/off:** a section counts only if it has a row with a target. If both had rows and only
  one was on, Alt + Shortcut is on and the rows of the section that was off are removed
  (they never ran).
- **Hold threshold:** the value that affected Hold rows is kept. If both sections had Hold
  rows with different values, the Alt + Mouse value is used for both. A missing value reads
  as the old 150 ms.
- Mouse rows for buttons that do not exist are dropped. Every removal or replaced threshold
  is written to `debug.log` when debug logging is on.

The first save after conversion copies the original file to `<Name>.ini.pre-alt-shortcut.bak`
once; that copy is never replaced. Older builds do not read the new sections: they show the
profile's Alt shortcuts as off and empty, and saving there drops them. To roll back, close the
app and rename the backup over the `.ini` file.

If `[AltShortcut]` or `[AltShortcutBindings]` contains an error, the profile loads with Alt +
Shortcut off and becomes read-only so the file is not overwritten; fix the file and restart.

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
- **Colors stay changed after sWinShortcuts exits or crashes** — the app leaves the current
  colors in place when it closes, and a crash does the same. On the next start it rewrites
  only monitors that have colors turned on; on the desktop, that means monitors checked in
  **Window [Default]**. To reset your desktop colors on every start, keep **Window [Default]**
  switched on, turn on **Color Profiles** in its Display tab, and check each monitor. The
  default slider values give normal colors. sWinShortcuts then controls those monitors'
  colors whenever no app profile applies, which replaces calibration loaded by other tools.
  A game's held colors also stay on screen after sWinShortcuts exits or crashes, and the
  same **Window [Default]** setup resets them on the next start.
- **Some protected processes can't be matched by path** — the app falls back to matching by
  process name.

## FAQ

**Does it need Administrator rights?**
Not strictly, but running elevated is recommended: it's required to capture and remap input
directed at other elevated windows, and for the elevated autostart option. Everything else
works unelevated.

**Is it safe to use in competitive games?**
Any global-hook utility is visible to anti-cheat systems, and every input sWinShortcuts
sends is marked by Windows as injected. Its randomized timing (jitter, variable press
durations) exists so input registers reliably — it is not ban protection. Use your own
judgment and follow each game's rules.

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
