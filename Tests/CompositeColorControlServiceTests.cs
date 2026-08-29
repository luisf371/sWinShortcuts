using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Xunit;

namespace Tests;

public sealed class CompositeColorControlServiceTests
{
    [Theory]
    [InlineData(GpuVendor.Nvidia, "gamma,nvidia")]
    [InlineData(GpuVendor.Amd, "gamma,amd")]
    [InlineData(GpuVendor.Intel, "gamma")]
    public void Apply_KnownVendor_RoutesOnlyToOwningBackend(GpuVendor vendor, string expectedCalls)
    {
        var calls = new List<string>();
        var service = CreateService(
            calls,
            ColorApplyOutcome.Skipped,
            ColorApplyOutcome.Applied,
            ColorApplyOutcome.Applied);

        var outcome = service.Apply(CreateDisplay(vendor), CreateProfile());

        Assert.Equal(expectedCalls.Split(','), calls);
        Assert.Equal(
            vendor == GpuVendor.Intel ? ColorApplyOutcome.Skipped : ColorApplyOutcome.Applied,
            outcome);
    }

    [Fact]
    public void Apply_UnknownVendor_FallsBackFromNvidiaSkipToAmd()
    {
        var calls = new List<string>();
        var service = CreateService(
            calls,
            ColorApplyOutcome.Skipped,
            ColorApplyOutcome.Skipped,
            ColorApplyOutcome.Applied);

        var outcome = service.Apply(CreateDisplay(GpuVendor.Unknown), CreateProfile());

        Assert.Equal(["gamma", "nvidia", "amd"], calls);
        Assert.Equal(ColorApplyOutcome.Applied, outcome);
    }

    [Theory]
    [InlineData(ColorApplyOutcome.Applied)]
    [InlineData(ColorApplyOutcome.Failed)]
    public void Apply_UnknownVendor_NvidiaClaimOrFailure_DoesNotProbeAmd(ColorApplyOutcome nvidiaOutcome)
    {
        var calls = new List<string>();
        var service = CreateService(
            calls,
            ColorApplyOutcome.Applied,
            nvidiaOutcome,
            ColorApplyOutcome.Applied);

        var outcome = service.Apply(CreateDisplay(GpuVendor.Unknown), CreateProfile());

        Assert.Equal(["gamma", "nvidia"], calls);
        Assert.Equal(
            nvidiaOutcome == ColorApplyOutcome.Failed ? ColorApplyOutcome.Failed : ColorApplyOutcome.Applied,
            outcome);
    }

    [Fact]
    public void Apply_GammaFailure_WinsOverSuccessfulVibrance()
    {
        var calls = new List<string>();
        var service = CreateService(
            calls,
            ColorApplyOutcome.Failed,
            ColorApplyOutcome.Applied,
            ColorApplyOutcome.Applied);

        var outcome = service.Apply(CreateDisplay(GpuVendor.Amd), CreateProfile());

        Assert.Equal(["gamma", "amd"], calls);
        Assert.Equal(ColorApplyOutcome.Failed, outcome);
    }

    private static CompositeColorControlService CreateService(
        List<string> calls,
        ColorApplyOutcome gamma,
        ColorApplyOutcome nvidia,
        ColorApplyOutcome amd)
    {
        return new CompositeColorControlService(
            (display, profile) => Record(calls, "gamma", gamma),
            (display, profile) => Record(calls, "nvidia", nvidia),
            (display, profile) => Record(calls, "amd", amd));
    }

    private static ColorApplyOutcome Record(
        List<string> calls,
        string name,
        ColorApplyOutcome outcome)
    {
        calls.Add(name);
        return outcome;
    }

    private static DisplayInfo CreateDisplay(GpuVendor vendor)
    {
        return new DisplayInfo
        {
            Id = "monitor-1",
            Name = "Monitor",
            DeviceName = @"\\.\DISPLAY1",
            GpuVendor = vendor
        };
    }

    private static DisplayColorProfile CreateProfile()
    {
        return new DisplayColorProfile
        {
            DisplayId = "monitor-1",
            IsEnabled = true,
            DigitalVibrance = 75
        };
    }
}
