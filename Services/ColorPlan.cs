using System;
using System.Collections.Immutable;
using System.Linq;

namespace sWinShortcuts.Services;

internal sealed record DisplayColorPlan(
    string DisplayId,
    bool IsEnabled,
    int Brightness,
    int Contrast,
    double Gamma,
    int DigitalVibrance);

internal sealed class ColorPlan : IEquatable<ColorPlan>
{
    public static readonly ColorPlan Empty = new(ImmutableArray<DisplayColorPlan>.Empty);

    public ColorPlan(ImmutableArray<DisplayColorPlan> displays)
    {
        Displays = displays;
    }

    public ImmutableArray<DisplayColorPlan> Displays { get; }

    public bool Equals(ColorPlan? other)
    {
        return other is not null && Displays.SequenceEqual(other.Displays);
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as ColorPlan);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var display in Displays)
        {
            hash.Add(display);
        }

        return hash.ToHashCode();
    }
}
