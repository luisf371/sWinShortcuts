using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;

namespace sWinShortcuts.Services;

public sealed class StartupService : IStartupService
{
    private const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string RunValueName = "sWinShortcuts";
    private const string LegacyTaskName = "sWinShortcuts_AutoStart";
    private static readonly XNamespace TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal delegate bool SchtasksRunner(string arguments, int timeoutMs,
        out int exitCode, out string stdout, out string stderr);

    private readonly SchtasksRunner _runSchtasks;
    private readonly Func<bool> _readRunKey;
    private readonly Action<bool> _writeRunKey;
    private readonly string _userSid;

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
        using var identity = WindowsIdentity.GetCurrent();
        _userSid = identity.User?.Value
            ?? throw new InvalidOperationException("Unable to determine the current user's SID.");
    }

    public StartupState GetState()
    {
        if (!TrySelectScheduledTask(out _, out var task, out var error))
            throw new InvalidOperationException(error);
        var run = _readRunKey();
        return new StartupState(StartWithWindows: run || task, StartAsAdmin: task);
    }

    public bool Apply(bool startWithWindows, bool startAsAdmin, out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            // Read both mechanisms before changing either so a failed transition can be compensated.
            if (!TrySelectScheduledTask(out var taskName, out var previousTask, out errorMessage))
                return false;
            var previousRun = _readRunKey();

            try
            {
                var useTask = startWithWindows && startAsAdmin;
                var taskSucceeded = useTask
                    ? TryEnableScheduledTask(taskName, previousTask, out errorMessage)
                    : TryDisableScheduledTask(taskName, previousTask, out errorMessage);
                if (!taskSucceeded)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorMessage)
                        ? "Failed to change the elevated startup task. Administrator rights may be required."
                        : errorMessage);

                // Remove the elevated task before normal startup is enabled; never silently leave
                // elevated startup selected after the user requested a normal launch.
                _writeRunKey(startWithWindows && !startAsAdmin);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = TryRestoreState(taskName, previousTask, previousRun, out var restoreError)
                    ? $"{ex.Message} Previous startup methods were restored."
                    : $"{ex.Message} Startup restoration failed: {restoreError} {DescribeCurrentState(taskName)}";
                return false;
            }
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private bool TryRestoreState(string taskName, bool previousTask, bool previousRun, out string? error)
    {
        error = null;
        try
        {
            // A timed-out operation may already have changed the OS; query before compensating.
            if (!TryGetScheduledTaskState(taskName, false, out var currentTask, out error))
                return false;
            if (currentTask != previousTask)
            {
                var restored = previousTask
                    ? TryEnableScheduledTask(taskName, currentTask, out error)
                    : TryDisableScheduledTask(taskName, currentTask, out error);
                if (!restored)
                    return false;
            }
            if (_readRunKey() != previousRun)
                _writeRunKey(previousRun);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private string DescribeCurrentState(string taskName)
    {
        try
        {
            if (!TryGetScheduledTaskState(taskName, false, out var task, out var error))
                return $"Current startup state is unknown: {error}";
            var run = _readRunKey();
            return $"Current startup state: elevated task {(task ? "enabled" : "disabled")}, normal startup {(run ? "enabled" : "disabled")}.";
        }
        catch (Exception ex)
        {
            return $"Current startup state is unknown: {ex.Message}";
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

    private bool TrySelectScheduledTask(out string taskName, out bool present, out string? error)
    {
        taskName = $"{LegacyTaskName}_{_userSid}";
        if (!TryGetScheduledTaskState(taskName, false, out present, out error) || present)
            return error is null;

        // Existing installations keep their task name only when its principal belongs to this user.
        if (!TryGetScheduledTaskState(LegacyTaskName, true, out present, out error))
            return false;
        if (present)
            taskName = LegacyTaskName;
        return true;
    }

    private bool TryGetScheduledTaskState(string taskName, bool ignoreOtherUsers, out bool present, out string? error)
    {
        present = false;
        error = null;
        try
        {
            var completed = _runSchtasks($"/Query /TN \"{taskName}\" /XML /HRESULT", 3000,
                out var exitCode, out var xml, out var stderr);
            if (!completed)
            {
                error = "Timed out reading the startup task.";
                return false;
            }
            if (exitCode == 0)
            {
                var principal = XDocument.Parse(xml).Root?.Element(TaskNamespace + "Principals")?
                    .Elements(TaskNamespace + "Principal").SingleOrDefault();
                var userId = principal?.Element(TaskNamespace + "GroupId") is null
                    ? principal?.Element(TaskNamespace + "UserId")?.Value : null;
                present = string.Equals(ResolveUserSid(userId), _userSid, StringComparison.OrdinalIgnoreCase);
                if (present || ignoreOtherUsers)
                    return true;
                error = "The startup task's principal could not be verified as the current user. No changes were made.";
                return false;
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

    private static string? ResolveUserSid(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return null;
        try
        {
            return userId.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(userId).Value
                : ((SecurityIdentifier)new NTAccount(userId).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch (IdentityNotMappedException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private bool TryEnableScheduledTask(string taskName, bool present, out string? error)
    {
        error = null;
        string? xmlPath = null;
        try
        {
            // Replace an owned task without a delete-first interval; a new task must not overwrite
            // a task another process registered after our initial query.
            xmlPath = Path.GetTempFileName();
            BuildTaskDefinition(_userSid, GetExecutablePath()).Save(xmlPath);

            if (!_runSchtasks(BuildCreateArguments(taskName, xmlPath, present), 8000, out var exitCode, out var stdOut, out var stdErr))
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
        finally
        {
            if (xmlPath is not null)
            {
                try { File.Delete(xmlPath); } catch { /* best effort */ }
            }
        }
    }

    private bool TryDisableScheduledTask(string taskName, bool present, out string? error)
    {
        error = null;
        try
        {
            // Absent → idempotent success. Only if it exists do we care whether the delete truly worked.
            if (!present)
            {
                return true;
            }

            if (!_runSchtasks($"/Delete /F /TN \"{taskName}\"", 5000, out var exitCode, out var stdOut, out var stdErr))
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

    internal static XDocument BuildTaskDefinition(string userSid, string exe)
    {
        var ns = TaskNamespace;
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            new XElement(ns + "Triggers", new XElement(ns + "LogonTrigger", new XElement(ns + "UserId", userSid))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"),
                new XElement(ns + "UserId", userSid),
                new XElement(ns + "LogonType", "InteractiveToken"),
                new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings",
                new XElement(ns + "DisallowStartIfOnBatteries", false),
                new XElement(ns + "StopIfGoingOnBatteries", false),
                new XElement(ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"),
                new XElement(ns + "Exec", new XElement(ns + "Command", exe)))));
    }

    internal static string BuildCreateArguments(string taskName, string xmlPath, bool replace = false)
        => $"/Create{(replace ? " /F" : "")} /TN \"{taskName}\" /XML \"{xmlPath}\"";
}

