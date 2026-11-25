# Repository Guidelines

## Project structure and module organization

sWinShortcuts is a Windows .NET 10 WPF desktop app for profile-based keyboard and mouse shortcuts. The app project and main window live at the repository root. Use the directory that owns the behavior you are changing.

- `Views/` and `Controls/` contain WPF UI. `ViewModels/` uses CommunityToolkit.Mvvm. Shared XAML styles and image assets live in `Resources/` and `Icons/`.
- `Models/` defines profiles and settings. `Services/` manages runtime behavior, with input state machines in `Services/Input/`.
- `Configuration/` handles profile persistence. `Tests/` contains the separate test project and its fakes.

## Build, test, and development commands

Run these commands from the repository root on Windows with the .NET SDK specified in `global.json`.

- `dotnet build sWinShortcuts.csproj` compiles the desktop app.
- `dotnet run --project sWinShortcuts.csproj` starts the app in the system tray.
- `dotnet test Tests/Tests.csproj` runs the full test suite.
- `.\BUILD_WINDOWS.bat` publishes a single-file Windows x64 executable that requires the .NET 10 Desktop Runtime.

## Coding style and naming conventions

Use four spaces, Allman braces, and file-scoped namespaces in C#. Use PascalCase for types and methods, `_camelCase` for private fields, and the `Async` suffix for asynchronous methods. Follow the surrounding XAML layout and the analyzer settings in `Tests/.editorconfig`.

Keep P/Invoke declarations in `Interop/NativeMethods.cs`. Use `Utilities/IniExtensions.cs` for culture-invariant INI values. Keep allocations, locks, and long-running work out of input hook callbacks.

When a macro step kind or macro INI key changes, follow the share-code engine bump rule beside `EngineMajor` in `Utilities/MacroShareCode.cs`.

## Testing guidelines

Use xUnit v3 and name tests `MethodName_Scenario_ExpectedResult`. Reuse hand-written fakes in `Tests/Fakes/`. Add focused regression tests for behavior changes. CI runs the full suite on Windows x64 without a numeric coverage gate.

Run one class with `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ProfileManagerTests"`.

## Commit and pull request guidelines

Use Conventional Commit subjects such as `fix(input): stop auto-run refocus stutter`. Add a scope when it identifies the affected area. Describe the user-facing change and verification in the pull request. Link a related issue when one exists. Update `CHANGELOG.md` and the pending version for notable user-facing changes. Commit, push, or open or merge pull requests only when explicitly requested.
