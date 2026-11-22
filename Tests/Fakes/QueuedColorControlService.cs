using System.Collections.Concurrent;
using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class QueuedColorControlService : IColorControlService
{
    public ConcurrentQueue<int> Brightnesses { get; } = new();
    public ConcurrentQueue<(string DisplayId, int Brightness)> DisplayBrightnesses { get; } = new();

    public ColorApplyOutcome Apply(DisplayInfo display, DisplayColorProfile profile)
    {
        Brightnesses.Enqueue(profile.Brightness);
        DisplayBrightnesses.Enqueue((display.Id, profile.Brightness));
        return ColorApplyOutcome.Applied;
    }
}
