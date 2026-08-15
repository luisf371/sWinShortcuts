using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.Services;

// Always-on crash/near-crash report facility, structurally independent of the debug-logging toggle:
// it never references ILoggerService/FileLoggerService.IsEnabled, so independence is not a setting.
// This is deliberately a synchronous static appender rather than a DI service or a background queue —
// it must work before the host exists, on any thread, during shutdown after host disposal, and while
// the process is dying (a queued writer can lose the one entry that explains the death). Entries are
// plain text with fixed begin/end separators so they stay machine-greppable and
// concurrency-verifiable; growth is bounded by reusing FileLoggerService's tested trim, with a
// tighter cap than debug.log's 2 MiB because crash reports are rare and every one is diagnostic.
internal static class CrashReporter
{
    private const string CRASH_LOG_FILE_NAME = "crash.log";
    private const long MAX_CRASH_LOG_BYTES = 512 * 1024;
    private const string REPORT_SEPARATOR = "================================================================================";

    private static readonly object WriteSync = new();

    // Test seam: non-null overrides AppSettings.GetRootDirectory(); null restores production behavior.
    private static string? _rootDirectoryOverride;

    public static void Write(string source, Exception? exception = null, string? detail = null, bool fatal = false)
    {
        try
        {
            var root = _rootDirectoryOverride ?? AppSettings.GetRootDirectory();
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, CRASH_LOG_FILE_NAME);

            var builder = new StringBuilder(512);
            builder.Append(REPORT_SEPARATOR).AppendLine();
            builder.Append($"[REPORT] {DateTimeOffset.Now:O}").AppendLine();
            builder.Append($"Source:    {source}").AppendLine();
            builder.Append($"Severity:  {(fatal ? "FATAL" : "NEAR-CRASH / diagnostic")}").AppendLine();
            builder.Append($"Process:   {ProcessName} (PID {Environment.ProcessId})").AppendLine();
            builder.Append($"Runtime:   .NET {Environment.Version} | OS: {Environment.OSVersion}").AppendLine();
            builder.Append($"Uptime:    {(TryGetUptime(out var uptime) ? uptime : "unknown")}").AppendLine();
            builder.Append($"Thread:    {Environment.CurrentManagedThreadId}").AppendLine();
            if (detail is not null)
            {
                builder.Append($"Detail:    {detail}").AppendLine();
            }
            builder.Append("Exception:").AppendLine();
            builder.AppendLine(exception?.ToString() ?? "(none — contextual event)");
            builder.Append(REPORT_SEPARATOR).AppendLine();

            lock (WriteSync)
            {
                File.AppendAllText(path, builder.ToString());
                FileLoggerService.TrimLogFile(path, MAX_CRASH_LOG_BYTES);
            }
        }
        catch
        {
            // A failing crash logger has nowhere left to report; never throw out of here.
        }
    }

    /// <summary>
    /// Compact one-line termination marker so "app vanished with no report" is distinguishable from
    /// a clean exit. Written once per termination from App.OnExit (which also fires on Windows logoff).
    /// </summary>
    public static void WriteExitMarker(bool clean)
    {
        try
        {
            var root = _rootDirectoryOverride ?? AppSettings.GetRootDirectory();
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, CRASH_LOG_FILE_NAME);

            var builder = new StringBuilder(128);
            builder.Append($"[EXIT] {DateTimeOffset.Now:O} {(clean ? "clean" : "unclean")}")
                .Append($" | uptime {(TryGetUptime(out var uptime) ? uptime : "unknown")}")
                .AppendLine($" | PID {Environment.ProcessId}");

            lock (WriteSync)
            {
                File.AppendAllText(path, builder.ToString());
                FileLoggerService.TrimLogFile(path, MAX_CRASH_LOG_BYTES);
            }
        }
        catch
        {
            // Never throw out of the crash logger.
        }
    }

    internal static void SetRootDirectoryOverrideForTests(string? rootDirectory)
    {
        _rootDirectoryOverride = rootDirectory;
    }

    // Environment.ProcessPath keeps the real exe name under single-file publish.
    private static string ProcessName
        => Path.GetFileName(Environment.ProcessPath) ?? "sWinShortcuts.exe";

    // Its own guard rather than one outer catch: StartTime access can throw, and that failure must
    // not cost the whole report entry. Invariant culture so the field never depends on locale.
    private static bool TryGetUptime(out string uptime)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            uptime = (DateTime.Now - process.StartTime).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            uptime = "unknown";
            return false;
        }
    }
}
