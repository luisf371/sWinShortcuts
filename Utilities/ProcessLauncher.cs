using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
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
                logger?.Log($"[Launcher] Failed to launch as desktop user '{path}': {ex.Message}");
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
            logger?.Log($"[Launcher] Failed to launch '{path}': {ex.Message}");
            throw;
        }
    }

    private static bool IsRunningAsAdmin() => Elevation.IsRunningAsAdmin();

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
                logger?.Log($"[Launcher] Fallback explorer launch of '{resolvedPath}' failed: {ex.Message}");
                // Fall through to COM method if this fails for some reason.
            }
        }

        WithDesktopShell(shell =>
        {
            dynamic desktopDispatch = shell;
            object? args = string.IsNullOrEmpty(arguments) ? null : arguments;
            var directory = Path.GetDirectoryName(resolvedPath);
            object? workingDirectory = string.IsNullOrEmpty(directory) ? null : directory;
            desktopDispatch.ShellExecute(resolvedPath, args, workingDirectory, "open", 1);
        });
    }

    internal static void WithDesktopShell(Action<object> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        object? shellWindows = null;
        object? desktop = null;
        object? browserObject = null;
        NativeMethods.IShellView? view = null;
        object? folderView = null;
        object? shell = null;
        try
        {
            var shellWindowsType = Type.GetTypeFromCLSID(NativeMethods.CLSID_ShellWindows)
                ?? throw new InvalidOperationException("Could not find ShellWindows type.");
            shellWindows = Activator.CreateInstance(shellWindowsType)
                ?? throw new InvalidOperationException("Could not create ShellWindows instance.");
            object location = NativeMethods.CSIDL_DESKTOP;
            object? root = null; // VT_EMPTY, as required by FindWindowSW.
            Marshal.ThrowExceptionForHR(((NativeMethods.IShellWindows)shellWindows).FindWindowSW(
                ref location, ref root, NativeMethods.SWC_DESKTOP, out _,
                NativeMethods.SWFO_NEEDDISPATCH, out desktop));
            if (desktop is null)
                throw new InvalidOperationException("Could not find the desktop Shell window.");

            var browserId = typeof(NativeMethods.IShellBrowser).GUID;
            Marshal.ThrowExceptionForHR(((NativeMethods.IServiceProvider)desktop).QueryService(
                NativeMethods.SID_STopLevelBrowser, browserId, out browserObject));
            if (browserObject is not NativeMethods.IShellBrowser browser)
                throw new InvalidOperationException("Could not obtain the desktop Shell browser.");
            Marshal.ThrowExceptionForHR(browser.QueryActiveShellView(out view));
            if (view is null)
                throw new InvalidOperationException("Could not obtain the desktop Shell view.");
            Marshal.ThrowExceptionForHR(view.GetItemObject(
                NativeMethods.SVGIO_BACKGROUND, NativeMethods.IID_IDispatch, out folderView));
            if (folderView is null)
                throw new InvalidOperationException("Could not obtain the desktop folder automation object.");
            shell = ((dynamic)folderView).Application;
            if (shell is null)
                throw new InvalidOperationException("Could not obtain the desktop Shell application.");
            action(shell);
        }
        finally
        {
            // Release each acquired reference once; interface casts above are aliases,
            // not additional owned references. Never final-release a potentially shared RCW.
            ReleaseComReference(shell);
            ReleaseComReference(folderView);
            ReleaseComReference(view);
            ReleaseComReference(browserObject);
            ReleaseComReference(desktop);
            ReleaseComReference(shellWindows);
        }
    }

    private static void ReleaseComReference(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
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
