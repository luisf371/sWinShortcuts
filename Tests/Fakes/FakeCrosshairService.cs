using System.Collections.Concurrent;
using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class FakeCrosshairService : ICrosshairService
{
    public sealed record AppliedConfig(Profile? Profile, IntPtr ForegroundHwnd, long ForegroundGeneration);

    public ConcurrentQueue<AppliedConfig> Applications { get; } = new();

    public ConcurrentQueue<bool> RightButtonStates { get; } = new();

    public void Start() { }

    public void Stop() => ApplyProfile(null, IntPtr.Zero);

    public void ApplyProfile(Profile? profile, IntPtr foregroundHwnd, long foregroundGeneration = 0)
    {
        Applications.Enqueue(new AppliedConfig(profile, foregroundHwnd, foregroundGeneration));
    }

    public void SetRightButtonHeld(bool isDown)
    {
        RightButtonStates.Enqueue(isDown);
    }
}
