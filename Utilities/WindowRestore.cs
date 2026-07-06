namespace sWinShortcuts.Utilities;

public static class WindowRestore
{
    /// <summary>
    /// True when the saved window rectangle meaningfully intersects the (origin-aware) virtual desktop.
    /// Unlike a naive non-negative check, this accepts windows on a monitor placed left of / above the
    /// primary (negative coordinates) and only rejects rectangles that are entirely off every screen.
    /// </summary>
    public static bool IntersectsVirtualDesktop(
        double left, double top, double width, double height,
        double vsLeft, double vsTop, double vsRight, double vsBottom)
    {
        return left < vsRight && (left + width) > vsLeft && top < vsBottom && (top + height) > vsTop;
    }
}
