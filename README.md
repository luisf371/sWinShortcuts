<!-- ![Project Banner](path/to/image.png) -->

![License](https://img.shields.io/badge/license-Unspecified-lightgrey)
![Language](https://img.shields.io/badge/language-C%23-blue)
![Version](https://img.shields.io/badge/version-1.0.0-green)

## Intro

sWinShortcuts is a high-performance Windows utility designed to enhance productivity through low-level keyboard and mouse remapping. It provides a robust framework for creating application-specific profiles that automate complex gestures and system-level adjustments with minimal latency.

## Key Features

- **Application-Aware Profiles**: Automatically activates custom configurations based on the currently focused executable.
- **Low-Level Input Hooks**: Utilizes `WH_KEYBOARD_LL` and `WH_MOUSE_LL` for precise, system-wide input interception and remapping.
- **Display Color Management**: Per-profile control of brightness, contrast, gamma, and NVIDIA Digital Vibrance.
- **Advanced Alt+Mouse Gestures**: Enables complex action mapping to mouse buttons when combined with the Alt modifier.
- **Process De-elevation**: Unique capability to launch desktop-level applications from an elevated service context.
- **Anti-Cheat Humanization**: Incorporates randomized timing jitter and RNG warmup to simulate natural human input patterns.

## Quick Start

### Prerequisites
- .NET 8.0 SDK
- Windows 10/11

### Installation and Execution
1. Clone the repository.
2. Build the project using the .NET CLI:
   ```powershell
   dotnet build sWinShortcuts.csproj
   ```
3. Run the application:
   ```powershell
   dotnet run --project sWinShortcuts.csproj
   ```
   *The application will start minimized to the system tray.*

### Running Tests
To execute the test suite:
```powershell
dotnet test Tests/Tests.csproj
```

## Overview

sWinShortcuts is built on .NET 8 using the WPF framework for its user interface and WinForms for system tray integration. The architecture follows the MVVM pattern, leveraging `CommunityToolkit.Mvvm` for state management and `Microsoft.Extensions.Hosting` for dependency injection. 

Technical highlights include:
- **Hot-Path Optimization**: The `InputHookService` is designed for zero-allocation execution to ensure stability during high-frequency input events.
- **Native Interop**: Centralized P/Invoke management through `NativeMethods.cs` for direct interaction with Windows APIs (User32, Gdi32) and NVAPI.
- **Persistence Layer**: Custom INI-based storage system for profiles, ensuring human-readable configuration files located in `%APPDATA%\sWinShortcuts\`.

## FAQ

**Q: Why does sWinShortcuts require Administrator privileges?**  
**A:** The application must run with high integrity to capture and remap input across all windows, including those already running as Administrator, and to interact with low-level system hooks.

**Q: Is it safe to use in competitive games?**  
**A:** While sWinShortcuts employs humanization techniques like randomized jitter, any global hook application is technically visible to anti-cheat systems. Use it at your own discretion.

**Q: How does the "Launch as Desktop User" feature work?**  
**A:** This feature utilizes a COM Shell Dispatch technique to request the Explorer process (the desktop user) to launch an application, preventing the child process from inheriting elevated privileges.

**Q: Can I edit profiles manually without the UI?**  
**A:** Yes. Profiles are stored as standard `.ini` files in `%APPDATA%\sWinShortcuts\Profiles\`. You can modify these files directly and restart the application to apply changes.

**Q: What happens if my hardware does not support NVIDIA Digital Vibrance?**  
**A:** The application includes a graceful fallback system. It will continue to apply standard Windows Gamma Ramps while silently skipping NVIDIA-specific calls on unsupported hardware.

## Build & Publish Reference

Here is a reference of the available build and publish commands for different use cases:

### Standard Commands
* **Build Debug (Default)**: Builds the project with debug symbols and no optimizations.
  ```powershell
  dotnet build sWinShortcuts.csproj -c Debug
  ```
* **Build Release**: Builds the project with optimizations, ready for production.
  ```powershell
  dotnet build sWinShortcuts.csproj -c Release
  ```
* **Clean Artifacts**: Cleans previous build outputs (clears `bin/` and `obj/` directories).
  ```powershell
  dotnet clean sWinShortcuts.csproj
  ```

### Publishing Single Executables
Publishing outputs compiled packages ready for distribution.

* **Self-Contained Single EXE**: Compiles all binaries and the .NET 8 runtime into a single executable. No .NET runtime installation is required on the user's machine.
  ```powershell
  dotnet publish sWinShortcuts.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true
  ```
* **Framework-Dependent Single EXE**: Compiles into a single executable but does not bundle the runtime, yielding a significantly smaller file size. Requires the .NET 8 runtime to be installed on the target machine.
  ```powershell
  dotnet publish sWinShortcuts.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
  ```

## License

Unspecified

## AI Disclosure

This project utilizes AI assistance for code generation, documentation, and architectural optimization to ensure high-quality standards and efficient development cycles.
