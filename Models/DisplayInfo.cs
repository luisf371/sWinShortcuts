namespace sWinShortcuts.Models;

public sealed class DisplayInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string DeviceName { get; init; }

    public bool IsPrimary { get; init; }
}
