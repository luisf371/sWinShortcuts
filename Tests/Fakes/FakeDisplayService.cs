using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class FakeDisplayService : IDisplayService
{
    public IReadOnlyList<DisplayInfo> Displays { get; set; } = [];

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        return Displays;
    }
}
