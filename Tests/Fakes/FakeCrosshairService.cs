using System.Collections.Concurrent;
using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class FakeCrosshairService : ICrosshairService
{
    public sealed record AppliedConfig(Profile? Profile, IntPtr ForegroundHwnd);

    public ConcurrentQueue<AppliedConfig> Applications { get; } = new();

    public ConcurrentQueue<bool> RightButtonStates { get; } = new();

    public void ApplyProfile(Profile? profile, IntPtr foregroundHwnd)
    {
        Applications.Enqueue(new AppliedConfig(profile, foregroundHwnd));
    }

    public void SetRightButtonHeld(bool isDown)
    {
        RightButtonStates.Enqueue(isDown);
    }
}
