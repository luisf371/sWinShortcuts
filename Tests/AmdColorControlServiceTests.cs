using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class AmdColorControlServiceTests
{
    [Theory]
    [InlineData(0, 0, 200, 100, 1, 100)]
    [InlineData(50, 0, 200, 100, 1, 100)]
    [InlineData(75, 0, 200, 100, 1, 150)]
    [InlineData(76, 0, 200, 100, 10, 150)]
    [InlineData(51, 0, 205, 105, 20, 105)]
    [InlineData(100, 0, 200, 100, 1, 200)]
    [InlineData(120, 0, 200, 100, 1, 200)]
    public void TryMapPercentToAdlValue_ValidRange_MapsFromDefaultToMaximum(
        int percent,
        int min,
        int max,
        int @default,
        int step,
        int expected)
    {
        var mapped = AmdColorControlService.TryMapPercentToAdlValue(
            percent,
            new AmdSaturationRange(@default, min, max, step),
            out var value);

        Assert.True(mapped);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(100, 100, 100, 1)]
    [InlineData(99, 100, 200, 1)]
    [InlineData(201, 100, 200, 1)]
    [InlineData(100, 0, 200, 0)]
    [InlineData(100, 0, 200, 201)]
    public void TryMapPercentToAdlValue_UntrustworthyRange_FailsClosed(
        int @default,
        int min,
        int max,
        int step)
    {
        Assert.False(AmdColorControlService.TryMapPercentToAdlValue(
            75,
            new AmdSaturationRange(@default, min, max, step),
            out _));
    }

    [Fact]
    public void ApplyDigitalVibrance_ExactDisplayMatch_SetsMappedValueAndFlushesAdapter()
    {
        var api = new FakeAmdAdlApi
        {
            Displays =
            [
                new AmdDisplayTarget(@"\\.\DISPLAY1", 1, 0),
                new AmdDisplayTarget(@"\\.\DISPLAY2", 2, 1)
            ],
            Range = new AmdSaturationRange(100, 0, 200, 1)
        };
        using var service = CreateService(api);

        var outcome = service.ApplyDigitalVibrance(CreateDisplay(@"\\.\DISPLAY2"), CreateProfile(75));

        Assert.Equal(ColorApplyOutcome.Applied, outcome);
        Assert.Equal((api.Displays[1], 150), api.SetCalls.Single());
        Assert.Equal(2, Assert.Single(api.FlushCalls));
    }

    [Fact]
    public void ApplyDigitalVibrance_UnmappedAmongSeveralDisplays_SkipsWithoutBroadcasting()
    {
        var api = new FakeAmdAdlApi
        {
            Displays =
            [
                new AmdDisplayTarget(@"\\.\DISPLAY1", 1, 0),
                new AmdDisplayTarget(@"\\.\DISPLAY2", 1, 1)
            ]
        };
        using var service = CreateService(api);

        var outcome = service.ApplyDigitalVibrance(CreateDisplay(@"\\.\DISPLAY3"), CreateProfile(80));

        Assert.Equal(ColorApplyOutcome.Skipped, outcome);
        Assert.Empty(api.SetCalls);
    }

    [Fact]
    public void ApplyDigitalVibrance_UnmappedSingleDisplay_UsesUnambiguousFallback()
    {
        var onlyTarget = new AmdDisplayTarget(@"\\.\DISPLAY7", 4, 2);
        var api = new FakeAmdAdlApi { Displays = [onlyTarget] };
        using var service = CreateService(api);

        var outcome = service.ApplyDigitalVibrance(CreateDisplay(@"\\.\DISPLAY1"), CreateProfile(50));

        Assert.Equal(ColorApplyOutcome.Applied, outcome);
        Assert.Equal((onlyTarget, api.Range.Default), Assert.Single(api.SetCalls));
    }

    [Fact]
    public void ApplyDigitalVibrance_UnknownGpuWithUnmappedSingleAmdDisplay_Skips()
    {
        var api = new FakeAmdAdlApi
        {
            Displays = [new AmdDisplayTarget(@"\\.\DISPLAY7", 4, 2)]
        };
        using var service = CreateService(api);

        var outcome = service.ApplyDigitalVibrance(
            CreateDisplay(@"\\.\DISPLAY1", GpuVendor.Unknown),
            CreateProfile(75));

        Assert.Equal(ColorApplyOutcome.Skipped, outcome);
        Assert.Empty(api.SetCalls);
    }

    [Fact]
    public void ApplyDigitalVibrance_AdlUnavailable_SkipsAndProbesOnlyOnce()
    {
        var api = new FakeAmdAdlApi { InitializeResult = false };
        using var service = CreateService(api);

        Assert.Equal(ColorApplyOutcome.Skipped,
            service.ApplyDigitalVibrance(CreateDisplay(@"\\.\DISPLAY1"), CreateProfile(75)));
        Assert.Equal(ColorApplyOutcome.Skipped,
            service.ApplyDigitalVibrance(CreateDisplay(@"\\.\DISPLAY1"), CreateProfile(75)));

        Assert.Equal(1, api.InitializeCalls);
        Assert.Equal(0, api.EnumerateCalls);
    }

    [Fact]
    public void ApplyDigitalVibrance_UnsupportedSaturation_Skips()
    {
        var api = new FakeAmdAdlApi
        {
            Displays = [new AmdDisplayTarget(@"\\.\DISPLAY1", 1, 0)],
            TryGetRangeResult = false
        };
        using var service = CreateService(api);

        var outcome = service.ApplyDigitalVibrance(CreateDisplay(@"\\.\DISPLAY1"), CreateProfile(75));

        Assert.Equal(ColorApplyOutcome.Skipped, outcome);
        Assert.Empty(api.SetCalls);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ApplyDigitalVibrance_SetOrFlushFailure_ReturnsFailed(bool setResult, bool flushResult)
    {
        var api = new FakeAmdAdlApi
        {
            Displays = [new AmdDisplayTarget(@"\\.\DISPLAY1", 1, 0)],
            SetResult = setResult,
            FlushResult = flushResult
        };
        using var service = CreateService(api);

        var outcome = service.ApplyDigitalVibrance(CreateDisplay(@"\\.\DISPLAY1"), CreateProfile(75));

        Assert.Equal(ColorApplyOutcome.Failed, outcome);
        Assert.Equal(setResult ? 1 : 0, api.FlushCalls.Count);
    }

    [Fact]
    public void RefreshTopology_AvailableApi_RefreshesAndClearsTargetCache()
    {
        var api = new FakeAmdAdlApi
        {
            Displays = [new AmdDisplayTarget(@"\\.\DISPLAY1", 1, 0)]
        };
        using var service = CreateService(api);

        Assert.Equal(ColorApplyOutcome.Applied,
            service.ApplyDigitalVibrance(CreateDisplay(@"\\.\DISPLAY1"), CreateProfile(75)));

        service.RefreshTopology();

        Assert.Equal(ColorApplyOutcome.Applied,
            service.ApplyDigitalVibrance(CreateDisplay(@"\\.\DISPLAY1"), CreateProfile(75)));
        Assert.Equal(1, api.RefreshCalls);
        Assert.Equal(1, api.InitializeCalls);
        Assert.Equal(2, api.EnumerateCalls);
    }

    [Fact]
    public void RefreshTopology_RefreshFailure_ReinitializesOnNextApply()
    {
        var api = new FakeAmdAdlApi
        {
            Displays = [new AmdDisplayTarget(@"\\.\DISPLAY1", 1, 0)],
            RefreshResult = false
        };
        using var service = CreateService(api);

        Assert.Equal(ColorApplyOutcome.Applied,
            service.ApplyDigitalVibrance(CreateDisplay(@"\\.\DISPLAY1"), CreateProfile(75)));

        service.RefreshTopology();

        Assert.Equal(ColorApplyOutcome.Applied,
            service.ApplyDigitalVibrance(CreateDisplay(@"\\.\DISPLAY1"), CreateProfile(75)));
        Assert.Equal(1, api.RefreshCalls);
        Assert.Equal(2, api.InitializeCalls);
    }

    [Fact]
    public void RefreshTopology_PreviouslyUnavailableApi_ReprobesOnNextApply()
    {
        var api = new FakeAmdAdlApi
        {
            InitializeResult = false,
            Displays = [new AmdDisplayTarget(@"\\.\DISPLAY1", 1, 0)]
        };
        using var service = CreateService(api);

        Assert.Equal(ColorApplyOutcome.Skipped,
            service.ApplyDigitalVibrance(CreateDisplay(@"\\.\DISPLAY1"), CreateProfile(75)));

        api.InitializeResult = true;
        service.RefreshTopology();

        Assert.Equal(ColorApplyOutcome.Applied,
            service.ApplyDigitalVibrance(CreateDisplay(@"\\.\DISPLAY1"), CreateProfile(75)));
        Assert.Equal(0, api.RefreshCalls);
        Assert.Equal(2, api.InitializeCalls);
    }

    private static AmdColorControlService CreateService(FakeAmdAdlApi api)
    {
        return new AmdColorControlService(new NullLoggerService { IsEnabled = true }, api);
    }

    private static DisplayInfo CreateDisplay(string deviceName, GpuVendor gpuVendor = GpuVendor.Amd)
    {
        return new DisplayInfo
        {
            Id = "monitor-1",
            Name = "Monitor",
            DeviceName = deviceName,
            AdapterName = "AMD Radeon",
            GpuVendor = gpuVendor
        };
    }

    private static DisplayColorProfile CreateProfile(int digitalVibrance)
    {
        return new DisplayColorProfile
        {
            DisplayId = "monitor-1",
            IsEnabled = true,
            DigitalVibrance = digitalVibrance
        };
    }

    private sealed class FakeAmdAdlApi : IAmdAdlApi
    {
        public bool InitializeResult { get; set; } = true;
        public IReadOnlyList<AmdDisplayTarget> Displays { get; init; } = [];
        public AmdSaturationRange Range { get; init; } = new(100, 0, 200, 1);
        public bool TryGetRangeResult { get; init; } = true;
        public bool SetResult { get; init; } = true;
        public bool FlushResult { get; init; } = true;
        public bool RefreshResult { get; init; } = true;
        public int InitializeCalls { get; private set; }
        public int RefreshCalls { get; private set; }
        public int EnumerateCalls { get; private set; }
        public List<(AmdDisplayTarget Target, int Value)> SetCalls { get; } = [];
        public List<int> FlushCalls { get; } = [];

        public bool TryInitialize()
        {
            InitializeCalls++;
            return InitializeResult;
        }

        public bool TryRefresh()
        {
            RefreshCalls++;
            return RefreshResult;
        }

        public IReadOnlyList<AmdDisplayTarget> GetDisplays()
        {
            EnumerateCalls++;
            return Displays;
        }

        public bool TryGetSaturationRange(AmdDisplayTarget target, out AmdSaturationRange range)
        {
            range = Range;
            return TryGetRangeResult;
        }

        public bool TrySetSaturation(AmdDisplayTarget target, int value)
        {
            SetCalls.Add((target, value));
            return SetResult;
        }

        public bool TryFlush(int adapterIndex)
        {
            FlushCalls.Add(adapterIndex);
            return FlushResult;
        }

        public void Dispose()
        {
        }
    }
}
