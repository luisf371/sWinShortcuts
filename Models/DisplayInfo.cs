namespace sWinShortcuts.Models;

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel
}

public sealed class DisplayInfo
{
    /// <summary>Stable per-monitor identity, used to persist color settings.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Current Windows display target (for example <c>\\.\DISPLAY1</c>), used only to apply settings.</summary>
    public required string DeviceName { get; init; }

    public string AdapterName { get; init; } = string.Empty;

    public GpuVendor GpuVendor { get; init; }

    public bool IsPrimary { get; init; }
}
