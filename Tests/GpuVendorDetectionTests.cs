using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class GpuVendorDetectionTests
{
    [Theory]
    [InlineData(0x10DEu, GpuVendor.Nvidia)]
    [InlineData(0x1002u, GpuVendor.Amd)]
    [InlineData(0x8086u, GpuVendor.Intel)]
    [InlineData(0x1022u, GpuVendor.Unknown)]
    [InlineData(0u, GpuVendor.Unknown)]
    public void ParseGpuVendor_DxgiVendorId_ReturnsExpectedVendor(uint vendorId, GpuVendor expected)
    {
        Assert.Equal(expected, DisplayService.ParseGpuVendor(vendorId));
    }

    [Theory]
    [InlineData(@"PCI\VEN_1002&DEV_744C", true)]
    [InlineData(@"PCI\VEN_1022&DEV_164E", false)]
    [InlineData(@"PCI\VEN_10DE&DEV_2684", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAmdAdapter_PnpVendorEvidence_ReturnsExpectedResult(string? pnpString, bool expected)
    {
        Assert.Equal(expected, AmdAdlApi.IsAmdAdapter(pnpString));
    }

    [Fact]
    public void EnumerateDxgiAdapters_WindowsDxgiBoundary_ReturnsValidMappings()
    {
        var adapters = DisplayService.EnumerateDxgiAdapters();

        Assert.All(adapters, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Key));
            Assert.True(Enum.IsDefined(entry.Value.Vendor));
        });
    }

    [Theory]
    [InlineData(GpuVendor.Nvidia, "NVIDIA GeForce RTX 4080", "Detected GPU: NVIDIA GeForce RTX 4080")]
    [InlineData(GpuVendor.Amd, "AMD Radeon RX 7900 XTX", "Detected GPU: AMD Radeon RX 7900 XTX")]
    [InlineData(GpuVendor.Intel, "Intel(R) UHD Graphics", "Detected GPU: Intel(R) UHD Graphics — vibrance unsupported")]
    [InlineData(GpuVendor.Unknown, "Microsoft Basic Display Adapter", "Detected GPU: Microsoft Basic Display Adapter — vibrance unavailable")]
    [InlineData(GpuVendor.Unknown, "", "Detected GPU: unknown (virtual display) — vibrance unavailable")]
    public void DetectedGpuLabel_DisplayCapability_IsExplicit(
        GpuVendor vendor,
        string adapterName,
        string expected)
    {
        using var viewModel = CreateViewModel(new DisplayInfo
        {
            Id = "monitor-1",
            Name = "Monitor",
            DeviceName = @"\\.\DISPLAY1",
            AdapterName = adapterName,
            GpuVendor = vendor
        });

        Assert.Equal(expected, viewModel.DetectedGpuLabel);
    }

    private static DisplayColorSettingsViewModel CreateViewModel(DisplayInfo display)
    {
        var settings = new ColorSettings();
        var profile = settings.GetOrCreateProfile(display.Id);
        return new DisplayColorSettingsViewModel(
            display,
            profile,
            settings,
            new RecordingColorControlService(),
            () => true);
    }
}
