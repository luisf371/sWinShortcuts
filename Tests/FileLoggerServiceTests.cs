using System;
using System.IO;
using System.Text;
using sWinShortcuts.Services;
using Xunit;

namespace Tests;

public sealed class FileLoggerServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledLogger_ConstructAndDispose_DoesNotRequireLogDirectory(bool obstructed)
    {
        var root = Path.Combine(Path.GetTempPath(), "sWinShortcutsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var logDirectory = Path.Combine(root, "logs");
        try
        {
            if (obstructed) File.WriteAllText(logDirectory, "obstruction");
            using (var logger = new FileLoggerService(logDirectory))
            {
                logger.Log("disabled entry");
            }

            Assert.False(Directory.Exists(logDirectory));
            if (obstructed) Assert.Equal("obstruction", File.ReadAllText(logDirectory));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void EnabledLogger_FirstWrite_CreatesMissingLogDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "sWinShortcutsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var logDirectory = Path.Combine(root, "logs");
        try
        {
            using (var logger = new FileLoggerService(logDirectory))
            {
                Assert.False(Directory.Exists(logDirectory));
                logger.IsEnabled = true;
                logger.Log("FIRST-WRITE-MARKER");
            }
            Assert.Contains("FIRST-WRITE-MARKER", File.ReadAllText(Path.Combine(logDirectory, "debug.log")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Logger_RootObstructionRemoved_LaterWriteRecovers()
    {
        var root = Path.Combine(Path.GetTempPath(), "sWinShortcutsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var logDirectory = Path.Combine(root, "logs");
        File.WriteAllText(logDirectory, "obstruction");
        try
        {
            using (var logger = new FileLoggerService(logDirectory))
            {
                File.Delete(logDirectory);
                logger.IsEnabled = true;
                logger.Log("RECOVERED-WRITE-MARKER");
            }
            Assert.Contains("RECOVERED-WRITE-MARKER", File.ReadAllText(Path.Combine(logDirectory, "debug.log")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DisabledLogger_Dispose_DoesNotTrimExistingLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "sWinShortcutsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "debug.log");
        try
        {
            File.WriteAllText(path, new string('x', 2 * 1024 * 1024 + 100));
            var originalLength = new FileInfo(path).Length;
            using (var logger = new FileLoggerService(root)) { }
            Assert.Equal(originalLength, new FileInfo(path).Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void TrimLogFile_LeavesRoomForSeveralSmallAppends()
    {
        var root = Path.Combine(Path.GetTempPath(), "sWinShortcutsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "debug.log");
        try
        {
            File.WriteAllLines(path, Enumerable.Repeat(new string('x', 30), 100));
            FileLoggerService.TrimLogFile(path, 2048);
            var retained = File.ReadAllText(path);

            for (var i = 0; i < 8; i++)
            {
                var entry = $"new-{i:D2}-012345678901234567890123" + Environment.NewLine;
                File.AppendAllText(path, entry);
                retained += entry;
                FileLoggerService.TrimLogFile(path, 2048);
                Assert.Equal(retained, File.ReadAllText(path));
                Assert.True(new FileInfo(path).Length <= 2048);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TrimLogFile_KeepsNewestEntriesWithinLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "sWinShortcutsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "debug.log");

        try
        {
            var content = new StringBuilder();
            for (var i = 0; i < 500; i++)
            {
                content.AppendLine($"old-{i:D4}-012345678901234567890123456789");
            }

            content.AppendLine("NEWEST-MARKER");
            File.WriteAllText(path, content.ToString(), Encoding.UTF8);

            FileLoggerService.TrimLogFile(path, 2048);

            var trimmed = File.ReadAllText(path);
            Assert.True(new FileInfo(path).Length <= 2048);
            Assert.Contains("NEWEST-MARKER", trimmed);
            Assert.DoesNotContain("old-0000", trimmed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
