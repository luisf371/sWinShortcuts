# Changelog

All notable changes, new features, improvements, and bug fixes for **sWinShortcuts** are documented here. Changes are grouped chronologically by release date and build milestone in plain English.

---

## August 29, 2026 (Build 84)

### Added
- **AMD Radeon Digital Vibrance Support**:
  - Added Digital Vibrance (saturation) control for AMD Radeon GPUs using the AMD Display Library (ADL2, requires AMD Radeon Software Adrenalin 25.3.1 or newer).
  - Added automatic GPU vendor and adapter detection per display using DirectX Graphics Infrastructure (DXGI).
  - The detected graphics card (NVIDIA, AMD, Intel, or virtual display) is now shown inline next to each monitor in the Display color settings tab.
  - Added multi-monitor safeguards that prevent applying saturation settings to the wrong display if monitor device names are ambiguous.

### Changed & Improved
- **Modular Display Color Control Architecture**:
  - Extracted universal Windows GDI brightness, contrast, and gamma ramp handling into a dedicated, vendor-neutral service.
  - Added a composite color controller that routes digital vibrance to NVIDIA NVAPI or AMD ADL2 based on the active screen's detected GPU while preserving standard Windows gamma ramps.
  - Added automatic recovery and cache invalidation when display arrangements, monitor resolutions, or graphics drivers change.

---

## August 29, 2026 (Build 79)

### Changed & Improved
- **Configuration & Persistence Modernization**:
  - Removed obsolete migration and fallback routines for legacy file formats (such as older `Color.ini` files, deprecated multi-section Alt+Mouse entries, and 4-field display color profiles) to keep configuration loading fast and lightweight.
  - Standardized all settings reading and writing to strictly use the modern INI structure (`[App]` application settings and unified profile sections).
  - Unified tap and hold shortcut serialization between Alt + Mouse and Alt + Keyboard features.
  - Streamlined custom profile editing permission checks in the main window.

### Fixed
- **Shortcut Removal Cleanup**:
  - Updated the "Remove All" buttons across shortcut tables (Combined Mappings, Alt + Mouse, and Alt + Keyboard) to properly conduct all cleanup steps for every deleted entry, ensuring events are unhooked, available trigger keys are refreshed, and profile changes auto-save reliably.

---

## August 26, 2026 (Build 74 – Build 77)

### Added
- **Automatic Update Checker**:
  - Added an in-app update check that queries GitHub for new releases.
  - Displays a clean notification banner at the top of the main window when a new version is available, with options to open the release page or dismiss the banner.
  - Added a "Check for Updates" toggle in the Settings dialog (can be enabled or disabled at any time).
  - Ensured release links safely open in your default browser without elevation conflicts.

### Changed & Improved
- **Input System Modularization**:
  - Reorganized the internal input engine into dedicated, focused components (Anti-AFK, Auto-Run, Gestures, Rapid Fire, Key Remapping) for enhanced long-term reliability and responsiveness.
  - Added direct file saving fallbacks when running in environments where file replacement operations are restricted.

---

## August 23, 2026 (Build 63 – Build 65)

### Added
- **Alt + Keyboard Shortcuts Mode**:
  - Added the ability to hold `Alt` and either *tap* or *hold* mapped keyboard keys to trigger different actions (similar to Alt + Mouse buttons).
  - Configurable tap/hold threshold sliders with automatic key-repeat suppression and clean cancellation.
  - Full management table under the profile's **Keys** tab.

### Fixed
- **Hold Breath Early Cancel Overhaul**:
  - Made "Early Cancel" a true master toggle: unchecking it completely disables early panic-canceling.
  - Selective input blocking: only the exact keystroke that cancels hold-breath is intercepted; repeated presses during the same aim pass directly to the game.
  - Fixed an issue where holding down a key before aiming could cause keyboard auto-repeats to trigger an accidental cancel or leave the key stuck down in-game.
  - Smoother profile switching and rebind transitions to prevent dropped or swallowed keys.

---

## August 19 – 20, 2026 (Build 53 – Build 60)

### Added
- **Crosshair Magnification & Size Control**:
  - Added a **Crosshair Size** slider allowing you to scale crosshairs from -50% to +50% of their original size with a live percentage display.
  - Added a **Reset** button next to the slider to quickly return to the default scale.
- **Build Info in Settings**:
  - The Settings window footer now displays the CI build number or local development build timestamp for easy version verification.

### Changed & Improved
- **UI Layout & Navigation**:
  - Promoted the **Windows Launcher** to a dedicated top-level tab on the built-in Windows profile.
  - Added "Reset to Default" buttons across all slider settings (Alt+Mouse, Rapid Fire, Hold Breath, and Anti-AFK).
  - Standardized the hotkey settings layout in the Settings window with full-width captions and cleaner alignment.
  - Adjusted the default Alt+Mouse hold threshold to 150 ms for optimal gaming response.

---

## August 17, 2026 (Build 43 – Build 45)

### Added
- **Tabbed Profile Editor & Window Pinning**:
  - Reorganized profile settings into clean tabs: **Keys**, **Advanced**, **Display**, and **System/Launcher**.
  - Added an **Always on Top** pin button in the title bar to keep sWinShortcuts floating over games while configuring options.
- **Sticky Rapid Fire & Status Dot Overlay**:
  - Rapid Fire arming is now a sticky session state: arm it once with your global hotkey, and it stays armed across focus switches, activating whenever its assigned game is in focus.
  - Added a lightweight on-screen **Status Dot Overlay**:
    - **Green Dot**: Ready and active in the focused game.
    - **Gray Dot**: Armed, but the active window is not the target game.
    - **Hidden**: Disarmed / turned off.
  - Pressing the Rapid Fire hotkey in a non-eligible application or desktop cleanly disarms it.
- **Color Preset Toast Notifications**:
  - Added a 2-second, click-through on-screen toast notification confirming when you switch display color presets with the global hotkey.

### Fixed
- **Crosshair Profile Snapshots**:
  - Fixed an issue where crosshair overlay settings were lost when creating profile snapshots during background saves.

---

## August 14 – 15, 2026 (Build 34 – Build 40)

### Added
- **Custom Game Crosshair Overlay**:
  - Added an on-screen crosshair overlay feature per game profile.
  - Click-through, centered on the game monitor, semi-transparent, and hidden from Alt-Tab.
  - Automatically hides while holding the Right Mouse Button so it doesn't obstruct in-game sniper scopes or iron sights.
- **Crash Reporting**:
  - Added automatic crash logging to `crash.log` in AppData to capture unexpected crashes without requiring debug logging to be active.

### Fixed
- **Application Exit & Cleanup**:
  - Fixed an issue where the crosshair overlay window could keep background processes running after closing the app from the system tray.
  - Resolved settings startup issues and cleaned up unused converters.

---

## August 10 – 12, 2026 (Build 27 – Build 32)

### Added
- **Profile-Based Rapid Fire (Auto-Clicker)**:
  - Added rapid fire left-clicking for semi-automatic weapons with configurable click intervals (25 ms – 250 ms).
  - Added subtle randomized timing jitter to simulate natural human input and prevent anti-cheat detection.
  - Added an app-wide toggle hotkey in Settings.
  - Added click hold duration and release retry logic to ensure every click registers reliably in-game without stuck mouse buttons.
- **Single-Key Auto-Run Triggers**:
  - Auto-Run can now be toggled using a single key press without requiring modifier keys (such as `Ctrl` or `Alt`).
  - Improved layout and alignment for Sprint and Early Cancel options.

---

## August 8 – 9, 2026 (Build 26)

### Added
- **Redesigned Caps Lock Modes**:
  - Replaced legacy modes with four clear options:
    - **Normal**: Pass through or replace Caps Lock with another key.
    - **2x Normal**: Sends paired double-tap pulses on key press and release.
    - **Disabled**: Disables Caps Lock completely to prevent accidental toggles during gaming.
    - Independent key remapping target setting.

### Changed & Improved
- **Upgrade to .NET 10 LTS**:
  - Upgraded the application and test suites to .NET 10 LTS for improved performance, security, and long-term Windows desktop runtime support.
  - Modernized unit testing framework to xUnit v3.

---

## July 22 – 26, 2026 (Build 17 – Build 22)

### Changed & Improved
- **Multi-Monitor Display Color Stability**:
  - Display color controls now resolve physical monitor hardware IDs (EDID) instead of temporary Windows display numbers, keeping color configurations intact across reboots or monitor cable swaps.
  - Added per-display fallback: unconfigured displays in a game profile automatically inherit global display color settings.
- **Startup Settings & Windows Integration**:
  - Enhanced "Start with Windows" options with administrator elevation awareness via Windows Task Scheduler.
  - Added a "Start Minimized to System Tray" setting.

### Fixed
- **Anti-AFK Logging & Test Coverage**:
  - Improved diagnostics for background window focus detection and anti-AFK activity.

---

## July 10 – 16, 2026 (Build 14)

### Added
- **Auto-Run & Anti-AFK Features**:
  - **Auto-Run**: Toggle continuous forward movement with configurable sprint key integration and smooth manual takeover (pressing/releasing forward seamlessly resumes auto-run).
  - **Anti-AFK**: Sends subtle input at configurable intervals (1–15 min) only after real keyboard inactivity, pausing automatically when you are playing.
  - **Advanced Mode Gate**: Added an Advanced Mode toggle in Settings to keep complex features hidden until needed.
- **Dual Display Color Presets (Primary & Secondary)**:
  - Added support for dual color presets per monitor with an app-level hotkey to toggle between normal and high-visibility modes on the fly.
- **Hold Breath Early Panic Trigger**:
  - Added a configurable panic button (key or mouse button) to instantly break out of hold-breath zoom mode.

### Fixed
- **Core Stability & Input Hardening**:
  - Fixed Caps Lock repeat desync and stuck keys during fast typing.
  - Dedicated background thread for window focus tracking to eliminate input lag.
  - Safe, atomic profile auto-saving to prevent data loss or file corruption.
  - Decoupled hold-breath simulation from the input hook thread to eliminate right-click micro-stutter.
  - Implemented an input hook watchdog that automatically recovers input hooks if Windows drops them.

---

## July 6 – 8, 2026

### Fixed & Hardened
- **Input Hook & Key Release Reliability**:
  - Fixed stuck modifier keys when releasing Right Mouse Button during hold-breath by synchronizing state transitions and key releases.
  - Decoupled hold-breath key injection from the low-level hook thread to prevent right-click aim stutter.
  - Added an automatic hook-loss watchdog with raw input detection to seamlessly recover hooks if Windows silently unhooks them.
  - Synchronized Alt and Right Mouse Button physical state checks after switching profiles so held keys remain responsive.
- **Profile Persistence & Auto-Save Hardening**:
  - Prevented hold-breath delay setting from resetting to a 5 ms minimum on launch when set to 0 ms.
  - Swapped profile autosave serialization to atomic snapshots to eliminate cross-thread data races between the UI, saving tasks, and input hooks.
  - Added debounced window drag/resize settings saves to avoid continuous disk writes while moving the window.
  - Ensured switching away from elevated autostart cleanly cleans up scheduled tasks before enabling standard registry autostart.

---

## April 26, 2026

### Added
- **Independent Per-Display Color Control**:
  - Full multi-monitor support allowing independent digital vibrance, brightness, contrast, and gamma settings for each connected screen.
  - Added automatic monitor hardware identification (EDID parsing) to display real monitor brand and model names (e.g. "Dell U2720Q") rather than generic display numbers.
  - Dedicated per-display color configuration UI with instant preview sliders.
- **Color Plan Deduplication & Sequential Processing**:
  - Background color updates now process sequentially through dedicated channels to eliminate screen flickering or redundant color profile applications during rapid window switching.
  - Added monitor handle caching with automatic invalidation when display settings or screen arrangements change.
- **Application Icons & Diagnostic Logging**:
  - Integrated official application icons and added application-level crash diagnostics.

### Fixed
- **Thread Safety for Key Remapping**:
  - Hardened key override mapping lists against multi-threaded race conditions during rapid key combinations and releases.

---

## December 28 – 29, 2025

### Changed & Improved
- **Zero-Allocation Hook Optimization**:
  - Refactored right-click override release and input hooks to eliminate memory allocations in hot paths, ensuring zero garbage collection pauses during gaming.
- **Monitor Caching & Friendly Names**:
  - Refactored display enumeration to cache monitors and use native Windows display device APIs for clean, human-readable display names.
- **Window Switching & Process Performance**:
  - Resolved window-switch latency, optimized running process enumeration, and stabilized key selection bindings in the profile manager.

---

## November 2025

### Added
- **Digital Vibrance & Color Control Engine**:
  - Introduced NVIDIA Digital Vibrance, Gamma, Brightness, and Contrast controls with live preview sliders.
  - Created a dedicated global color profile (`Color.ini`) for system-wide vibrance preferences.
  - Added a global debug logging toggle in the Settings dialog.
- **Combined Key Remapping & Right Mouse Overrides**:
  - Added configurable key-to-key remapping.
  - Merged standard key mapping and right-click override functionality into a unified, responsive interface.
  - Added per-key suppression checkboxes so remaps can completely replace the original key or allow both through.
  - Added startup configuration option ("Start with Windows") to Settings dialog.

### Fixed & Improved
- **Input & Window Behavior**:
  - Fixed right-click hold race conditions when combining Alt + mouse actions.
  - Added non-elevated application launching fallback (de-elevation) when running sWinShortcuts as Administrator.
  - Added smooth scrolling protection over datagrid headers and dropdown menus.

---

## October 2025 (Initial Release & Core Foundation)

### Added
- **Per-Application Profile Engine**:
  - Automatic profile activation matching the active foreground game or application executable.
  - Built-in "Windows" fallback profile for desktop productivity.
  - Plain-text INI profile persistence (`%APPDATA%\sWinShortcuts\`) for easy editing, backups, and portability.
- **Alt + Mouse Actions**:
  - Map `Alt` + mouse buttons (Left, Right, Middle, Mouse 4, Mouse 5) with distinct tap and hold thresholds.
  - Dynamic DataGrid editor with duplicate binding prevention and intuitive button pickers.
- **Right-Click Hold Breath**:
  - Automatically holds a steady-aim key (e.g. `Left Shift`) when holding Right Mouse Button after a configurable delay.
- **Windows Launcher**:
  - Assign `Win + Key` shortcuts (e.g., `Win + Numpad`) to launch games, programs, or scripts with optional administrative privileges.
- **Modern Dark UI & System Tray Integration**:
  - Dark-themed interface with custom sliders, tooltips, and running process picker.
  - Minimizes to system tray on close or minimize; double-click tray icon to restore.
  - Settings dialog for startup, logging, and application preferences.
- **High-Performance Input Hook Engine**:
  - Low-level keyboard and mouse hooks (`WH_KEYBOARD_LL`, `WH_MOUSE_LL`) with lock-free, zero-allocation hot paths.
