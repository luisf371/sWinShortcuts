using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class FakeInputHookService : IInputHookService
{
    public event EventHandler<Profile?>? ActiveProfileChanged;

    public bool HookWatchdogEnabled { get; set; } = true;

    public bool IsStarted { get; private set; }

    public Profile? WindowsProfile { get; private set; }

    public List<Profile> ActivatedProfiles { get; } = [];

    public int DeactivateCount { get; private set; }

    public void Start()
    {
        IsStarted = true;
    }

    public void Stop()
    {
        IsStarted = false;
    }

    public void ActivateProfile(Profile profile)
    {
        ActivatedProfiles.Add(profile);
        ActiveProfileChanged?.Invoke(this, profile);
    }

    public void DeactivateProfile()
    {
        DeactivateCount++;
        ActiveProfileChanged?.Invoke(this, null);
    }

    public void SetWindowsProfile(Profile profile)
    {
        WindowsProfile = profile;
    }

    public void Dispose()
    {
    }
}
