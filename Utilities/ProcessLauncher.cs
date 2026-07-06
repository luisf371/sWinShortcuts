using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using sWinShortcuts.Services;
using sWinShortcuts.Interop;

namespace sWinShortcuts.Utilities;

public static class ProcessLauncher
{
    public static void Launch(string path, string arguments, bool runAsAdmin, ILoggerService? logger = null)
    {
        bool isElevated = IsRunningAsAdmin();

        // If we are elevated, but the user wants non-elevated (RunAsAdmin == false),
        // we must use the Shell Dispatch trick to de-elevate.
        if (isElevated && !runAsAdmin)
        {
            try
            {
                LaunchAsDesktopUser(path, arguments, logger);
                return;
            }
            catch (Exception ex)
            {
                logger?.Log($"Failed to launch as desktop user: {ex.Message}");
                // CRITICAL: Do NOT fall back to standard launch if de-elevation fails.
                // That would result in running as Admin against the user's wishes.
                throw new InvalidOperationException("Failed to launch application as limited user from admin context.", ex);
            }
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = runAsAdmin ? "runas" : string.Empty,
            WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty
        };

        try 
        {
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            logger?.Log($"Failed to launch process: {ex.Message}");
            throw;
        }
    }

    private static bool IsRunningAsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void LaunchAsDesktopUser(string path, string arguments, ILoggerService? logger)
    {
        string resolvedPath = ResolvePath(path);

        // Optimization: If no arguments are provided, we can use the simpler
        // "Explorer.exe <path>" trick which reliably runs as the desktop user.
        if (string.IsNullOrWhiteSpace(arguments))
        {
            try
            {
                // Use the absolute Windows-dir explorer.exe (not a bare name resolved via PATH) — this runs
                // from an elevated context, so a PATH-order hijack must not be possible.
                var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var explorerPath = string.IsNullOrEmpty(windowsDir)
                    ? "explorer.exe"
                    : System.IO.Path.Combine(windowsDir, "explorer.exe");

                // We wrap the path in quotes to handle spaces correctly.
                // Explorer will execute the default action for the file (usually running it).
                Process.Start(explorerPath, $"\"{resolvedPath}\"");
                return;
            }
            catch (Exception ex)
            {
                logger?.Log($"Fallback explorer launch failed: {ex.Message}");
                // Fall through to COM method if this fails for some reason.
            }
        }

        // CLSID for ShellWindows
        var shellWindowsType = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"));
        if (shellWindowsType == null) throw new InvalidOperationException("Could not find ShellWindows type.");

        dynamic? shellWindows = Activator.CreateInstance(shellWindowsType);
        if (shellWindows == null) throw new InvalidOperationException("Could not create ShellWindows instance.");

        // Get the desktop window handle
        IntPtr desktopHwnd = NativeMethods.GetShellWindow();
        if (desktopHwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not obtain Shell Window handle (GetShellWindow returned 0).");
        }
        
        dynamic? desktopDispatch = null;

        // Find the window that matches the desktop
        foreach (dynamic item in shellWindows)
        {
            try
            {
                // item is IWebBrowser2, has HWND property
                // HWND in COM is often a long, so cast to long first to be safe
                if ((IntPtr)(long)item.HWND == desktopHwnd)
                {
                    desktopDispatch = item.Document.Application;
                    break;
                }
            }
            catch
            {
                // Ignore errors accessing individual items
            }
        }

        if (desktopDispatch != null)
        {
            // ShellExecute signature:
            // void ShellExecute(string File, [optional] object vArgs, [optional] object vDir, [optional] object vOperation, [optional] object vShow);
            // vOperation: "open"
            // vShow: 1 (SW_SHOWNORMAL)
            
            object file = resolvedPath;
            // Pass null for optional arguments if they are empty.
            // This avoids passing empty strings ("") which some COM implementations might mishandle
            // or pass as an actual empty argument to the process (argv[1]="").
            object? vArgs = string.IsNullOrEmpty(arguments) ? null : arguments;
            object? vDir = string.IsNullOrEmpty(Path.GetDirectoryName(resolvedPath)) ? null : Path.GetDirectoryName(resolvedPath);
            object vOp = "open";
            object vShow = 1;

            desktopDispatch.ShellExecute(file, vArgs, vDir, vOp, vShow);
        }
        else
        {
             throw new InvalidOperationException("Could not find Desktop Shell view to perform de-elevation.");
        }
    }

    private static string ResolvePath(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return fileName;
        if (Path.IsPathRooted(fileName) && File.Exists(fileName)) return fileName;

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return fileName;

        var paths = pathEnv.Split(Path.PathSeparator);
        var extensions = new[] { ".exe", ".bat", ".cmd", ".com" };

        foreach (var path in paths)
        {
            var fullPathBase = Path.Combine(path, fileName);
            
            // Check exact match first (e.g. if user typed "cmd.exe")
            if (File.Exists(fullPathBase)) return fullPathBase;

            // Check extensions
            foreach (var ext in extensions)
            {
                var fullPath = fullPathBase + ext;
                if (File.Exists(fullPath)) return fullPath;
            }
        }

        return fileName;
    }
}
