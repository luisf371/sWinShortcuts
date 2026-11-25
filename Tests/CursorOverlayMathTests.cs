using System.Drawing;
using sWinShortcuts.Views;
using Xunit;

namespace Tests;

public sealed class CursorOverlayMathTests
{
    private static readonly Rectangle Monitor = new(0, 0, 1920, 1080);
    private const int Gap = CursorCountdownWindow.CURSOR_GAP;

    [Fact]
    public void PlaceBeside_RoomBelowRight_PlacesBelowRightOfTheCursor()
    {
        Assert.Equal((500 + Gap, 400 + Gap), CursorCountdownWindow.PlaceBeside(500, 400, 80, 40, Monitor));
    }

    [Theory]
    [InlineData(1900, 400, 1900 - Gap - 80, 400 + Gap)]
    [InlineData(500, 1070, 500 + Gap, 1070 - Gap - 40)]
    [InlineData(1900, 1070, 1900 - Gap - 80, 1070 - Gap - 40)]
    public void PlaceBeside_NearAnEdge_FlipsToTheInside(int cursorX, int cursorY, int expectedX, int expectedY)
    {
        Assert.Equal((expectedX, expectedY), CursorCountdownWindow.PlaceBeside(cursorX, cursorY, 80, 40, Monitor));
    }

    [Theory]
    [InlineData(-1900, -1070, -1900 + Gap, -1070 + Gap)]
    [InlineData(-10, -10, -10 - Gap - 80, -10 - Gap - 40)]
    public void PlaceBeside_MonitorWithNegativeCoordinates_StaysOnThatMonitor(int cursorX, int cursorY, int expectedX, int expectedY)
    {
        var leftAbove = new Rectangle(-1920, -1080, 1920, 1080);

        Assert.Equal((expectedX, expectedY), CursorCountdownWindow.PlaceBeside(cursorX, cursorY, 80, 40, leftAbove));
    }

    [Theory]
    [InlineData(240, -35, false, "X +240  Y -35 px")]
    [InlineData(-240, 35, false, "X -240  Y +35 px")]
    [InlineData(-5, -7, false, "X -5  Y -7 px")]
    [InlineData(0, 12, false, "X 0  Y +12 px")]
    [InlineData(240, -35, true, "≈ +240, -35")]
    public void FormatDistance_EachDirection_ShowsSignedPixelsOrApproximateRaw(int x, int y, bool raw, string expected)
    {
        Assert.Equal(expected, OffsetRulerWindow.FormatDistance(x, y, raw));
    }

    [Theory]
    [InlineData(1.0, 2010, 615, 90, 15)]
    [InlineData(1.5, 2010, 615, 60, 10)]
    public void ToDip_LogicalPointOnAMonitor_IsRelativeToThatMonitorInDips(double scale, int x, int y, double expectedX, double expectedY)
    {
        var second = new Rectangle(1920, 600, 2560, 1440);

        var point = OffsetRulerWindow.ToDip(x, y, second, scale);

        Assert.Equal((expectedX, expectedY), (point.X, point.Y));
    }

    [Fact]
    public Task Close_BeforeAClick_EndsTheMeasurementWithNull() => MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
    {
        var result = new TaskCompletionSource<(int X, int Y)?>();
        var ruler = new OffsetRulerWindow(Monitor, new Point(500, 400), (500, 400), raw: false, result);

        ruler.Close();

        Assert.True(result.Task.IsCompletedSuccessfully);
        Assert.Null(await result.Task);
    });
}
