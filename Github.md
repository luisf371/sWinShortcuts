Yes—you can wire up a GitHub Actions workflow that builds both artifacts on every push or tag. High-level approach:

Host runner: windows-latest (WPF requires Windows).
Install .NET 8 with actions/setup-dotnet.
Restore/build once (caches NuGet).
Publish the framework-dependent build (dotnet publish sWinShortcuts.csproj -c Release).
Publish the self-contained single-file build (dotnet publish … -r win-x64 --self-contained true -p:PublishSingleFile=true … with the extra self-extract switches you tested).
Upload both output folders as artifacts or bundle them into releases, depending on trigger.
Example .github/workflows/build.yml outline:


```git
name: Build sWinShortcuts

on:
  push:
    branches: [ main ]
  pull_request:
  workflow_dispatch:
  release:
    types: [created]

jobs:
  build:
    runs-on: windows-latest

    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x

      - name: Restore
        run: dotnet restore sWinShortcuts/sWinShortcuts.csproj

      - name: Publish framework-dependent
        run: dotnet publish sWinShortcuts/sWinShortcuts.csproj -c Release

      - name: Publish self-contained single-file
        run: >
          dotnet publish sWinShortcuts/sWinShortcuts.csproj -c Release
          -r win-x64 --self-contained true
          -p:PublishSingleFile=true
          -p:IncludeNativeLibrariesForSelfExtract=true
          -p:IncludeAllContentForSelfExtract=true

      - name: Upload artifacts
        uses: actions/upload-artifact@v4
        with:
          name: framework-dependent
          path: sWinShortcuts/bin/Release/net8.0-windows/publish

      - name: Upload self-contained
        uses: actions/upload-artifact@v4
        with:
          name: self-contained
          path: sWinShortcuts/bin/Release/net8.0-windows/win-x64/publish
```

You can swap upload-artifact with actions/upload-release-asset in a release job if you prefer the zip directly on GitHub releases. Consider adding dotnet test before publishing if you introduce tests.