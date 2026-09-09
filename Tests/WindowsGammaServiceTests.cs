using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Xunit;

namespace Tests;

public sealed class WindowsGammaServiceTests
{
    [Theory]
    [InlineData(double.NaN, DisplayColorProfile.DefaultGamma)]
    [InlineData(double.PositiveInfinity, DisplayColorProfile.DefaultGamma)]
    [InlineData(double.NegativeInfinity, DisplayColorProfile.DefaultGamma)]
    [InlineData(0.1, 0.5)]
    [InlineData(4.0, 3.0)]
    public void BuildGammaRamp_InvalidGamma_NormalizesBeforeGeneratingRamp(double gamma, double expected)
    {
        var ramp = WindowsGammaService.BuildGammaRamp(new DisplayColorProfile { Gamma = gamma });
        var normalized = WindowsGammaService.BuildGammaRamp(new DisplayColorProfile { Gamma = expected });

        Assert.Equal(normalized.Red, ramp.Red);
        Assert.Equal(normalized.Green, ramp.Green);
        Assert.Equal(normalized.Blue, ramp.Blue);
    }

    [Fact]
    public void BuildGammaRamp_NeutralProfile_ProducesLinearRgbRamp()
    {
        var profile = new DisplayColorProfile
        {
            Brightness = 50,
            Contrast = 50,
            Gamma = 1.0
        };

        var ramp = WindowsGammaService.BuildGammaRamp(profile);

        Assert.Equal((ushort)0, ramp.Red[0]);
        Assert.Equal((ushort)32896, ramp.Red[128]);
        Assert.Equal(ushort.MaxValue, ramp.Red[255]);
        Assert.Equal(ramp.Red, ramp.Green);
        Assert.Equal(ramp.Red, ramp.Blue);
    }
}
