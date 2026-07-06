using sWinShortcuts.Services;
using sWinShortcuts.Utilities;
using Xunit;

namespace Tests;

public class ExecutableNameTests
{
    [Theory]
    [InlineData("notepad.exe", "notepad")]
    [InlineData("NOTEPAD.EXE", "notepad")]
    [InlineData(@"C:\Windows\System32\notepad.exe", "notepad")]
    [InlineData("paint.net.exe", "paint.net")]   // S1: only the trailing .exe is stripped
    [InlineData("paint.net", "paint.net")]        // S1: bare process name (from ForegroundWatcher) survives
    [InlineData("  Foo.Bar.exe  ", "foo.bar")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_StripsOnlyTrailingExe(string? input, string expected)
    {
        Assert.Equal(expected, ExecutableName.Normalize(input));
    }
}

public class WindowRestoreTests
{
    // Single primary monitor at origin (0,0), 1920x1080.
    [Fact]
    public void OnScreen_PositivePosition_Intersects()
    {
        Assert.True(WindowRestore.IntersectsVirtualDesktop(100, 100, 800, 600, 0, 0, 1920, 1080));
    }

    [Fact]
    public void NegativeOrigin_SecondaryMonitorLeftOfPrimary_Intersects()
    {
        // Secondary monitor to the left: virtual desktop spans [-1920, 1920] x [0, 1080].
        Assert.True(WindowRestore.IntersectsVirtualDesktop(-1800, 50, 800, 600, -1920, 0, 1920, 1080));
    }

    [Fact]
    public void EntirelyOffScreen_DoesNotIntersect()
    {
        Assert.False(WindowRestore.IntersectsVirtualDesktop(5000, 5000, 800, 600, 0, 0, 1920, 1080));
    }
}

public class StartupServiceArgumentTests
{
    [Fact]
    public void BuildCreateArguments_WrapsExeInEscapedInnerQuotes()
    {
        var args = StartupService.BuildCreateArguments("MyTask", @"C:\Program Files\App\app.exe");

        // S2: the /TR action must carry escaped inner quotes so a space-containing path survives.
        Assert.Contains("/TR \"\\\"C:\\Program Files\\App\\app.exe\\\"\"", args);
        Assert.Contains("/TN \"MyTask\"", args);
        Assert.Contains("/RL HIGHEST", args);
    }
}

public class KeySerializerDigitTests
{
    [Fact]
    public void Deserialize_BareDigit_MapsToDigitRowKey_NotNumericEnum()
    {
        // P6: "5" must become D5, not the numeric enum member 5 (Key.Clear).
        Assert.Equal(System.Windows.Input.Key.D5, KeySerializer.Deserialize("5"));
        Assert.Equal(System.Windows.Input.Key.D0, KeySerializer.Deserialize("0"));
    }

    [Fact]
    public void Deserialize_NamedKey_StillWorks()
    {
        Assert.Equal(System.Windows.Input.Key.A, KeySerializer.Deserialize("A"));
        Assert.Equal(System.Windows.Input.Key.F5, KeySerializer.Deserialize("F5"));
    }
}
