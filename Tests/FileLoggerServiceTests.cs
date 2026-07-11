using System;
using System.IO;
using System.Text;
using sWinShortcuts.Services;
using Xunit;

namespace Tests;

public sealed class FileLoggerServiceTests
{
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
