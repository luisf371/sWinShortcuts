@echo off
rem Builds sWinShortcuts as a single framework-dependent exe (needs the .NET 10 Desktop Runtime, x64).
rem Extra arguments go to dotnet publish, e.g.  BUILD_WINDOWS.bat -p:DebugType=None
setlocal
cd /d "%~dp0"
set "EXE=bin\Release\net10.0-windows\win-x64\publish\sWinShortcuts.exe"

rem A running copy locks the exe, and publish would retry for about 10 s before failing.
if exist "%EXE%" 2>nul (>>"%EXE%" (call )) || goto :locked

dotnet publish sWinShortcuts.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true %*
if errorlevel 1 goto :failed

echo.
echo Build succeeded: "%CD%\%EXE%"
set "RC=0"
goto :done

:locked
echo "%EXE%" is in use. Exit sWinShortcuts from its tray icon, then run this again.
set "RC=1"
goto :done

:failed
echo.
echo Build FAILED. See the errors above.
set "RC=1"

:done
rem Keep the window open when double-clicked: only Explorer's command line ends in "<script> ".
set "CMDLINE=%cmdcmdline:"=%"
if /i "%CMDLINE:~-5%"=="%~x0 " pause
exit /b %RC%
