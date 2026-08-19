using sWinShortcuts.Models;
using sWinShortcuts.Views;
using Xunit;

namespace Tests;

public sealed class CrosshairOverlayBoundsTests
{
    // 1920x1080 monitor at (0,0); the bundled asset is 58x58, plus a non-square asymmetry guard.
    private const int ScreenX = 0;
    private const int ScreenY = 0;
    private const int ScreenW = 1920;
    private const int ScreenH = 1080;

    public static TheoryData<int, int, int, int, int> SizeCases => new()
    {
        // imageWidth, imageHeight, sizeAdjustment, expectedW, expectedH
        { 58, 58, -50, 29, 29 },
        { 58, 58, 0, 58, 58 },
        { 58, 58, 50, 87, 87 },
        { 40, 64, -50, 20, 32 },
        { 40, 64, 0, 40, 64 },
        { 40, 64, 50, 60, 96 }
    };

    [Theory]
    [MemberData(nameof(SizeCases))]
    public void ComputeOverlayBounds_ScalesBothAxesUniformly(int imageW, int imageH, int adjustment, int expectedW, int expectedH)
    {
        var (w, h, _, _) = CrosshairWindow.ComputeOverlayBounds(
            imageW, imageH, adjustment, ScreenX, ScreenY, ScreenW, ScreenH);

        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);
    }

    [Theory]
    [MemberData(nameof(SizeCases))]
    public void ComputeOverlayBounds_CentersToNearestPixel(int imageW, int imageH, int adjustment, int expectedW, int expectedH)
    {
        var (w, h, left, top) = CrosshairWindow.ComputeOverlayBounds(
            imageW, imageH, adjustment, ScreenX, ScreenY, ScreenW, ScreenH);

        // Doubled-center error is at most one physical pixel per axis (HWND positions are
        // integers, so odd sizes on even-parity monitor dims cannot center exactly).
        Assert.True(Math.Abs((2 * left + w) - (2 * ScreenX + ScreenW)) <= 1,
            $"horizontal center off by more than 1 px: left={left}, w={w}");
        Assert.True(Math.Abs((2 * top + h) - (2 * ScreenY + ScreenH)) <= 1,
            $"vertical center off by more than 1 px: top={top}, h={h}");

        // Exact equality holds whenever the slack is even-parity.
        if ((ScreenW - expectedW) % 2 == 0)
        {
            Assert.Equal((ScreenW - expectedW) / 2, left);
        }
        if ((ScreenH - expectedH) % 2 == 0)
        {
            Assert.Equal((ScreenH - expectedH) / 2, top);
        }
    }

    [Fact]
    public void ComputeOverlayBounds_OddSizesOnEvenMonitor_AreOffByAtMostHalfPerPixelPerSide()
    {
        // 29x29 / 87x87 (58x58 at -50 / +50) on 1920x1080 exercise the odd-parity case.
        foreach (var adjustment in new[] { -50, 50 })
        {
            var (w, h, left, top) = CrosshairWindow.ComputeOverlayBounds(
                58, 58, adjustment, ScreenX, ScreenY, ScreenW, ScreenH);

            Assert.Equal(adjustment < 0 ? 29 : 87, w);
            Assert.Equal(w, h);
            Assert.InRange(left, (ScreenW - w) / 2 - 1, (ScreenW - w) / 2);
            Assert.InRange(top, (ScreenH - h) / 2 - 1, (ScreenH - h) / 2);
        }
    }

    [Theory]
    [InlineData(999, 50)]
    [InlineData(-999, -50)]
    public void ComputeOverlayBounds_ClampsOutOfRangeAdjustment(int adjustment, int clamped)
    {
        var actual = CrosshairWindow.ComputeOverlayBounds(
            58, 58, adjustment, ScreenX, ScreenY, ScreenW, ScreenH);
        var expected = CrosshairWindow.ComputeOverlayBounds(
            58, 58, clamped, ScreenX, ScreenY, ScreenW, ScreenH);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SizeAdjustment_DefaultIsZero()
    {
        Assert.Equal(0, CrosshairSettings.DefaultSizeAdjustment);
        Assert.Equal(0, new CrosshairSettings().SizeAdjustment);
    }
}
