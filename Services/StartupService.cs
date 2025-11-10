using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace sWinShortcuts.Services;

public sealed class StartupService : IStartupService
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string RunValueName = "sWinShortcuts";
    private const string TaskName = "sWinShortcuts_AutoStart";

    public StartupState GetState()
    {
        var task = IsScheduledTaskEnabled();
        var run = IsRunKeyEnabled();
        return new StartupState(StartWithWindows: run || task, StartAsAdmin: task);
    }

    public bool Apply(bool startWithWindows, bool startAsAdmin, out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            if (!startWithWindows)
            {
                // Disable both methods
                TryDisableScheduledTask(out _);
                DisableRunKey();
                return true;
            }

            if (startAsAdmin)
            {
                // Use scheduled task set to Highest privileges
                if (!TryEnableScheduledTask(out var err))
                {
                    errorMessage = string.IsNullOrWhiteSpace(err)
                        ? "Failed to create scheduled task for admin startup."
                        : err;
                    return false;
                }

                // Ensure Run key is removed to avoid duplicate launches
                DisableRunKey();
                return true;
            }
            else
            {
                // Use Run key
                EnableRunKey();
                // Ensure scheduled task is removed
                TryDisableScheduledTask(out _);
                return true;
            }
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static string GetExecutablePath()
    {
        // Prefer Environment.ProcessPath when available
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path))
            return path!;
        return Process.GetCurrentProcess().MainModule?.FileName
               ?? throw new InvalidOperationException("Unable to determine executable path.");
    }

    private static bool IsRunKeyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(RunValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private static void EnableRunKey()
    {
        var exe = GetExecutablePath();
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(RunValueName, '"' + exe + '"');
    }

    private static void DisableRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch
        {
            // ignore
        }
    }

    private static bool IsScheduledTaskEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{TaskName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryEnableScheduledTask(out string? error)
    {
        error = null;
        try
        {
            // Make sure any old task is replaced
            TryDisableScheduledTask(out _);

            var exe = GetExecutablePath();
            var quotedExe = '"' + exe + '"';

            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Create /F /RL HIGHEST /SC ONLOGON /TN \"{TaskName}\" /TR {quotedExe}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            var stdErr = proc.StandardError.ReadToEnd();
            var stdOut = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(8000);

            if (proc.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryDisableScheduledTask(out string? error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Delete /F /TN \"{TaskName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi)!;
            // If the task doesn't exist, schtasks returns non-zero; treat as success
            proc.WaitForExit(5000);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}

