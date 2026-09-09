using System;
using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class FakeDisplayService : IDisplayService
{
    public IReadOnlyList<DisplayInfo> Displays { get; set; } = [];

    public event EventHandler? DisplaysChanged;

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        return Displays;
    }

    public Task<IReadOnlyList<DisplayInfo>> GetDisplaysAsync() => Task.FromResult(Displays);

    public void RaiseDisplaysChanged() => DisplaysChanged?.Invoke(this, EventArgs.Empty);
}
