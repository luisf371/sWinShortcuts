# Build standalone executible without *.dll files.
```powershell
dotnet publish sWinShortcuts.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true
```

```bash
dotnet publish sWinShortcuts.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
  ```

  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:IncludeAllContentForSelfExtract=true
--
--self-contained false > does not include .net runtime, 3mb vs 150mb's.

--

# Small Build - Requires .NET 8 Desktop Runtime (x64)

To produce a framework-dependent build, just run 
`dotnet publish -c Release`
from the project folder (done above). That drops all outputs under bin\Release\net8.0-windows\publish\. Contents will include:
sWinShortcuts.exe (small launcher)
sWinShortcuts.dll plus the other managed assemblies
.deps.json and .runtimeconfig.json
Ship that whole folder, but tell users they must install the .NET 8 Desktop Runtime (x64). Total size is a few megabytes since the runtime itself isn’t bundled.