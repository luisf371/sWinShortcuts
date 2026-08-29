using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Xunit;

namespace Tests;

public sealed class WindowsGammaServiceTests
{
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
