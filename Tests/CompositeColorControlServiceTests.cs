using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
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

    [Theory]
    [InlineData(true, ColorApplyOutcome.Applied)]
    [InlineData(false, ColorApplyOutcome.Applied)]
    [InlineData(true, ColorApplyOutcome.Failed)]
    [InlineData(false, ColorApplyOutcome.Failed)]
    public void Apply_SkippedRestoreAfterWrite_RetriesOnlyOutstandingComponent(
        bool gammaSkipped, ColorApplyOutcome initialOutcome)
    {
        var gamma = gammaSkipped ? initialOutcome : ColorApplyOutcome.Applied;
        var vibrance = gammaSkipped ? ColorApplyOutcome.Applied : initialOutcome;
        var service = new CompositeColorControlService(
            (_, _) => gamma, (_, _) => vibrance, (_, _) => vibrance);
        var display = CreateDisplay(GpuVendor.Amd);
        var profile = CreateProfile();
        Assert.Equal(initialOutcome, service.Apply(display, profile));

        profile.IsEnabled = false;
        gamma = gammaSkipped ? ColorApplyOutcome.Skipped : ColorApplyOutcome.Applied;
        vibrance = gammaSkipped ? ColorApplyOutcome.Applied : ColorApplyOutcome.Skipped;
        Assert.Equal(ColorApplyOutcome.Failed, service.Apply(display, profile));
        Assert.Equal(ColorApplyOutcome.Failed,
            service.Apply(CreateDisplay(GpuVendor.Amd, "MONITOR-1"), profile));
        Assert.Equal(ColorApplyOutcome.Applied,
            service.Apply(CreateDisplay(GpuVendor.Amd, "monitor-2"), profile));

        // The first component's successful restore must survive the other component's retry.
        gamma = gammaSkipped ? ColorApplyOutcome.Applied : ColorApplyOutcome.Skipped;
        vibrance = gammaSkipped ? ColorApplyOutcome.Skipped : ColorApplyOutcome.Applied;
        Assert.Equal(ColorApplyOutcome.Applied, service.Apply(display, profile));
        gamma = vibrance = ColorApplyOutcome.Skipped;
        Assert.Equal(ColorApplyOutcome.Skipped, service.Apply(display, profile));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_AlwaysUnsupportedComponent_DoesNotRequireRestore(bool gammaUnsupported)
    {
        var service = new CompositeColorControlService(
            (_, _) => gammaUnsupported ? ColorApplyOutcome.Skipped : ColorApplyOutcome.Applied,
            (_, _) => gammaUnsupported ? ColorApplyOutcome.Applied : ColorApplyOutcome.Skipped,
            (_, _) => gammaUnsupported ? ColorApplyOutcome.Applied : ColorApplyOutcome.Skipped);
        var display = CreateDisplay(GpuVendor.Amd);
        var profile = CreateProfile();

        Assert.Equal(ColorApplyOutcome.Applied, service.Apply(display, profile));
        profile.IsEnabled = false;
        Assert.Equal(ColorApplyOutcome.Applied, service.Apply(display, profile));
        Assert.Equal(ColorApplyOutcome.Applied, service.Apply(display, profile));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Apply_BackendThrows_RetainsEveryAttemptedComponent(bool gammaThrows)
    {
        var throwOnApply = true;
        var gamma = ColorApplyOutcome.Applied;
        var vibrance = ColorApplyOutcome.Applied;
        var service = new CompositeColorControlService(
            (_, _) => throwOnApply && gammaThrows ? throw new InvalidOperationException() : gamma,
            (_, _) => ColorApplyOutcome.Skipped,
            (_, _) => throwOnApply ? throw new InvalidOperationException() : vibrance);
        var display = CreateDisplay(GpuVendor.Amd);
        var profile = CreateProfile();
        Assert.Throws<InvalidOperationException>(() => service.Apply(display, profile));

        throwOnApply = false;
        profile.IsEnabled = false;
        gamma = vibrance = ColorApplyOutcome.Skipped;
        Assert.Equal(ColorApplyOutcome.Failed, service.Apply(display, profile));

        gamma = ColorApplyOutcome.Applied;
        Assert.Equal(gammaThrows ? ColorApplyOutcome.Applied : ColorApplyOutcome.Failed,
            service.Apply(display, profile));
        gamma = ColorApplyOutcome.Skipped;
        vibrance = ColorApplyOutcome.Applied;
        Assert.Equal(ColorApplyOutcome.Applied, service.Apply(display, profile));
    }

    [Fact]
    public void Apply_AmdEnumerationTemporarilyUnavailable_RetriesPersistedSaturationRestore()
    {
        var api = new TransientAmdAdlApi();
        using var amd = new AmdColorControlService(new NullLoggerService(), api);
        var service = new CompositeColorControlService(
            (_, _) => ColorApplyOutcome.Applied,
            (_, _) => ColorApplyOutcome.Skipped,
            amd.ApplyDigitalVibrance);
        var display = CreateDisplay(GpuVendor.Amd);
        var profile = CreateProfile();
        Assert.Equal(ColorApplyOutcome.Applied, service.Apply(display, profile));
        Assert.Equal(150, api.Saturation);
        Assert.Equal(1, api.FlushCalls);

        amd.RefreshTopology();
        api.EnumerationAvailable = false;
        profile.IsEnabled = false;
        profile.DigitalVibrance = DisplayColorProfile.DefaultDigitalVibrance;
        Assert.Equal(ColorApplyOutcome.Failed, service.Apply(display, profile));
        Assert.Equal(150, api.Saturation);

        api.EnumerationAvailable = true;
        Assert.Equal(ColorApplyOutcome.Applied, service.Apply(display, profile));
        Assert.Equal(100, api.Saturation);
        Assert.Equal(2, api.FlushCalls);
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

    private static DisplayInfo CreateDisplay(GpuVendor vendor, string id = "monitor-1")
    {
        return new DisplayInfo
        {
            Id = id,
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

    private sealed class TransientAmdAdlApi : IAmdAdlApi
    {
        public bool EnumerationAvailable { get; set; } = true;
        public int Saturation { get; private set; } = 100;
        public int FlushCalls { get; private set; }

        public bool TryInitialize() => true;
        public bool TryRefresh() => true;
        public IReadOnlyList<AmdDisplayTarget> GetDisplays() => EnumerationAvailable
            ? [new AmdDisplayTarget(@"\\.\DISPLAY1", 1, 0)] : [];

        public bool TryGetSaturationRange(AmdDisplayTarget target, out AmdSaturationRange range)
        {
            range = new AmdSaturationRange(100, 0, 200, 1);
            return true;
        }

        public bool TrySetSaturation(AmdDisplayTarget target, int value)
        {
            Saturation = value;
            return true;
        }

        public bool TryFlush(int adapterIndex)
        {
            FlushCalls++;
            return true;
        }

        public void Dispose() { }
    }
}
