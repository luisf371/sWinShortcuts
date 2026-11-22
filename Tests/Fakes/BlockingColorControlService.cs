using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class BlockingColorControlService : IColorControlService, IDisposable
{
    public ManualResetEventSlim ApplyEntered { get; } = new(false);

    public ManualResetEventSlim ReleaseApply { get; } = new(false);

    public ColorApplyOutcome Apply(DisplayInfo display, DisplayColorProfile profile)
    {
        ApplyEntered.Set();
        if (!ReleaseApply.Wait(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("Test did not release the blocked color apply.");
        }

        return ColorApplyOutcome.Applied;
    }

    public void Dispose()
    {
        ApplyEntered.Dispose();
        ReleaseApply.Dispose();
    }
}
