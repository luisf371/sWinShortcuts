using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace sWinShortcuts.Services;

public sealed class StartupService : IStartupService
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string RunValueName = "sWinShortcuts";
    private const string TaskName = "sWinShortcuts_AutoStart";

    internal delegate bool SchtasksRunner(string arguments, int timeoutMs,
        out int exitCode, out string stdout, out string stderr);

    private readonly SchtasksRunner _runSchtasks;
    private readonly Func<bool> _readRunKey;
    private readonly Action<bool> _writeRunKey;

    public StartupService() : this(RunSchtasks, IsRunKeyEnabled, enabled =>
    {
        if (enabled) EnableRunKey();
        else DisableRunKey();
    }) { }

    internal StartupService(SchtasksRunner runSchtasks, Func<bool> readRunKey, Action<bool> writeRunKey)
    {
        _runSchtasks = runSchtasks;
        _readRunKey = readRunKey;
        _writeRunKey = writeRunKey;
    }

    public StartupState GetState()
    {
        if (!TryGetScheduledTaskState(out var task, out var error))
            throw new InvalidOperationException(error);
        var run = _readRunKey();
        return new StartupState(StartWithWindows: run || task, StartAsAdmin: task);
    }

    public bool Apply(bool startWithWindows, bool startAsAdmin, out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            if (!startWithWindows)
            {
                // Disable both methods. Surface a scheduled-task delete failure (e.g. an unelevated
                // attempt to remove a HIGHEST task returns Access Denied) instead of silently leaving
                // the elevated autostart running.
                var okTask = TryDisableScheduledTask(out var taskErr);
                if (!okTask)
                {
                    errorMessage = string.IsNullOrWhiteSpace(taskErr)
                        ? "Failed to remove the elevated startup task. Administrator rights are required to change it."
                        : taskErr;
                    return false;
                }

                _writeRunKey(false);
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
                _writeRunKey(false);
                return true;
            }
            else
            {
                // Remove any leftover elevated task BEFORE enabling the Run key: with both
                // mechanisms active the app launches twice, one still elevated against the user's
                // new choice. Failing here leaves the previous state untouched and surfaces it.
                if (!TryDisableScheduledTask(out var taskErr))
                {
                    errorMessage = string.IsNullOrWhiteSpace(taskErr)
                        ? "Failed to remove the elevated startup task. Administrator rights are required to change it."
                        : taskErr;
                    return false;
                }

                _writeRunKey(true);
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
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(RunValueName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static void EnableRunKey()
    {
        var exe = GetExecutablePath();
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(RunValueName, '"' + exe + '"');
    }

    private static void DisableRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    private bool TryGetScheduledTaskState(out bool present, out string? error)
    {
        present = false;
        error = null;
        try
        {
            var completed = _runSchtasks($"/Query /TN \"{TaskName}\" /HRESULT", 3000,
                out var exitCode, out _, out var stderr);
            if (!completed)
            {
                error = "Timed out reading the startup task.";
                return false;
            }
            if (exitCode == 0)
            {
                present = true;
                return true;
            }
            if (exitCode == unchecked((int)0x80070002))
                return true;
            error = string.IsNullOrWhiteSpace(stderr)
                ? $"Could not read the startup task (HRESULT 0x{exitCode:X8})."
                : stderr;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool TryEnableScheduledTask(out string? error)
    {
        error = null;
        try
        {
            // Make sure any old task is replaced
            if (!TryDisableScheduledTask(out error))
                return false;

            var exe = GetExecutablePath();

            if (!_runSchtasks(BuildCreateArguments(TaskName, exe), 8000, out var exitCode, out var stdOut, out var stdErr))
            {
                error = "Timed out creating the startup task.";
                return false;
            }

            if (exitCode != 0)
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

    private bool TryDisableScheduledTask(out string? error)
    {
        error = null;
        try
        {
            // Absent → idempotent success. Only if it exists do we care whether the delete truly worked.
            if (!TryGetScheduledTaskState(out var present, out error))
                return false;
            if (!present)
            {
                return true;
            }

            if (!_runSchtasks($"/Delete /F /TN \"{TaskName}\"", 5000, out var exitCode, out var stdOut, out var stdErr))
            {
                error = "Timed out removing the startup task.";
                return false;
            }

            if (exitCode != 0)
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

    // Runs schtasks reading stdout/stderr CONCURRENTLY (avoids the redirected-pipe deadlock where the
    // child blocks filling one stream while we drain the other). Returns false on timeout after killing.
    private static bool RunSchtasks(string arguments, int timeoutMs, out int exitCode, out string stdOut, out string stdErr)
    {
        exitCode = -1;
        stdOut = string.Empty;
        stdErr = string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName = GetSchtasksPath(),
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return false;
        }

        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            try { proc.WaitForExit(2000); } catch { /* best effort */ }
            // Do NOT block on the read tasks here — if Kill failed / streams never closed, GetResult would
            // hang forever. Take only whatever already completed.
            stdOut = CompletedOrEmpty(outTask);
            stdErr = CompletedOrEmpty(errTask);
            return false;
        }

        // Process exited: streams are at EOF, so these complete promptly.
        stdOut = SafeResult(outTask);
        stdErr = SafeResult(errTask);
        exitCode = proc.ExitCode;
        return true;
    }

    private static string CompletedOrEmpty(Task<string> task)
        => task.IsCompletedSuccessfully ? task.Result : string.Empty;

    private static string SafeResult(Task<string> task)
    {
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch
        {
            return string.Empty;
        }
    }

    // 14.6: invoke schtasks by absolute path to avoid a PATH-order hijack of this elevated launch.
    private static string GetSchtasksPath()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidate = Path.Combine(system, "schtasks.exe");
        return File.Exists(candidate) ? candidate : "schtasks.exe";
    }

    // S2: the /TR action needs escaped inner quotes ("\"path\"") so CommandLineToArgvW preserves a
    // space-containing install path (e.g. "C:\Program Files\..."); a single quote layer is stripped and
    // the action resolves to "C:\Program".
    internal static string BuildCreateArguments(string taskName, string exe)
    {
        return $"/Create /F /RL HIGHEST /SC ONLOGON /TN \"{taskName}\" /TR \"\\\"{exe}\\\"\"";
    }
}

