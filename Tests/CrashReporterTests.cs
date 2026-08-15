using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using sWinShortcuts.Services;
using Xunit;

namespace Tests;

// CrashReporter is the always-on crash/near-crash facility, structurally independent of the debug
// toggle. xUnit parallelizes across classes but NOT facts within a class, and the test seam is a
// shared static root override — so every fact sets the override itself and clears it in a finally
// (mirrors FileLoggerServiceTests' temp-dir discipline). Any future test that Start()s the real
// InputHookService must also route CrashReporter here first: a null override writes to the real
// %APPDATA% root, and the shared static override is not parallelization-safe across classes.
public sealed class CrashReporterTests
{
    private const long MaxCrashLogBytes = 512 * 1024;
    private static readonly string Separator = new('=', 80);

    [Fact]
    public void Write_CreatesCrashLogWithEnvelopeAndException()
    {
        var root = CreateOverrideRoot();
        try
        {
            CrashReporter.Write("Test.Source", new InvalidOperationException("boom-message"), "detail-line");
            CrashReporter.Write("Test.Source.Fatal", new InvalidOperationException("fatal-boom"), fatal: true);

            var path = Path.Combine(root, "crash.log");
            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);

            Assert.Contains("Source:    Test.Source", content);
            Assert.Contains("InvalidOperationException", content);
            Assert.Contains("boom-message", content);
            Assert.Contains("Severity:  NEAR-CRASH / diagnostic", content);
            Assert.Contains("Severity:  FATAL", content);
            Assert.Contains("Detail:    detail-line", content);
            // Round-trip ("O") timestamp with a numeric UTC offset, e.g. 2026-08-15T12:34:56.7+02:00.
            Assert.True(Regex.IsMatch(content,
                @"\[REPORT\] \d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?([+-]\d{2}:\d{2}|Z)"),
                "report line must carry an ISO 8601 timestamp");
        }
        finally
        {
            CrashReporter.SetRootDirectoryOverrideForTests(null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Write_NullException_RecordsContextualEvent()
    {
        var root = CreateOverrideRoot();
        try
        {
            CrashReporter.Write("Test.Contextual", null, "some-detail");

            var content = File.ReadAllText(Path.Combine(root, "crash.log"));
            Assert.Contains("(none — contextual event)", content);
            Assert.Contains("Detail:    some-detail", content);
        }
        finally
        {
            CrashReporter.SetRootDirectoryOverrideForTests(null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Write_RepeatedReports_TrimsToCap()
    {
        var root = CreateOverrideRoot();
        try
        {
            // ~16 KiB of detail per write: crossing the 512 KiB cap takes ~33 writes, and every
            // write past it exercises the per-write trim without needing a thousand small appends.
            var payload = new string('x', 16 * 1024);
            for (var i = 0; i < 48; i++)
            {
                CrashReporter.Write("Test.Trim", null, $"OLDEST-ENTRY-{i:D4} {payload}");
            }
            CrashReporter.Write("Test.Trim", null, "NEWEST-MARKER");

            var path = Path.Combine(root, "crash.log");
            Assert.True(new FileInfo(path).Length <= MaxCrashLogBytes);
            var content = File.ReadAllText(path);
            Assert.Contains("NEWEST-MARKER", content);
            Assert.DoesNotContain("OLDEST-ENTRY-0000", content);
        }
        finally
        {
            CrashReporter.SetRootDirectoryOverrideForTests(null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Write_ConcurrentReports_AllEntriesIntact()
    {
        var root = CreateOverrideRoot();
        try
        {
            Parallel.For(0, 32, i => CrashReporter.Write("Test.Concurrent", null, $"MARKER-{i:D2}"));

            var content = File.ReadAllText(Path.Combine(root, "crash.log"));
            // Consecutive entries are separated only by the newline between one entry's trailing
            // separator and the next's leading one, so a RemoveEmptyEntries split keeps those
            // whitespace-only gap tokens (32 bodies + 31 gaps + 1 trailing = 64). Count the
            // [REPORT] header lines instead: that is the entry count regardless of separator
            // placement.
            var entries = content.Split(Separator, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(32, entries.Count(e => e.Contains("[REPORT]")));
            for (var i = 0; i < 32; i++)
            {
                var marker = $"MARKER-{i:D2}";
                Assert.Contains(marker, content);
                // Exactly one intact envelope carries each marker: the WriteSync lock made every
                // entry atomic rather than interleaved.
                Assert.Equal(1, entries.Count(e => e.Contains(marker)));
            }
        }
        finally
        {
            CrashReporter.SetRootDirectoryOverrideForTests(null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WriteExitMarker_WritesSingleCompactLine()
    {
        var root = CreateOverrideRoot();
        try
        {
            CrashReporter.WriteExitMarker(clean: true);
            CrashReporter.WriteExitMarker(clean: false);

            var lines = File.ReadAllLines(Path.Combine(root, "crash.log"));
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("[EXIT] ", lines[0]);
            Assert.StartsWith("[EXIT] ", lines[1]);
            Assert.Contains(" clean | uptime ", lines[0]);
            Assert.DoesNotContain("unclean", lines[0]);
            Assert.Contains(" unclean | uptime ", lines[1]);
            Assert.Contains("PID ", lines[0]);
            Assert.Contains("PID ", lines[1]);
        }
        finally
        {
            CrashReporter.SetRootDirectoryOverrideForTests(null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Write_IsIndependentOfDebugLoggingToggle()
    {
        var root = CreateOverrideRoot();
        try
        {
            // Constructing FileLoggerService is deliberately tolerated: its ctor creates the real
            // %APPDATA%\sWinShortcuts root and starts a background writer, but with IsEnabled=false
            // it never writes debug.log. The structural claim under test is that the crash path
            // never consults the debug toggle, so only the overridden crash.log root is asserted on.
            using (var logger = new FileLoggerService())
            {
                logger.IsEnabled = false;
                CrashReporter.Write("Test.Independence", null, "written-while-debug-disabled");
            }

            var content = File.ReadAllText(Path.Combine(root, "crash.log"));
            Assert.Contains("written-while-debug-disabled", content);
            Assert.False(File.Exists(Path.Combine(root, "debug.log")));
        }
        finally
        {
            CrashReporter.SetRootDirectoryOverrideForTests(null);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateOverrideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "sWinShortcutsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        CrashReporter.SetRootDirectoryOverrideForTests(root);
        return root;
    }
}
