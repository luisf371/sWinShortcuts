using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class FakeInputHookService : IInputHookService
{
    public event EventHandler<Profile?>? ActiveProfileChanged;

    public event EventHandler? ColorVariantToggleRequested;

    public bool HookWatchdogEnabled { get; set; } = true;

    public bool AdvancedModeEnabled { get; set; }

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

    public int ReleaseForegroundAutoRunCount { get; private set; }

    public void ReleaseForegroundAutoRun()
    {
        ReleaseForegroundAutoRunCount++;
    }

    public (IntPtr Hwnd, uint Pid, string? Exe)? LastForegroundIdentity { get; private set; }

    public void SetForegroundIdentity(IntPtr windowHandle, uint processId, string? normalizedExecutable)
    {
        LastForegroundIdentity = (windowHandle, processId, normalizedExecutable);
    }

    public void SetWindowsProfile(Profile profile)
    {
        WindowsProfile = profile;
    }

    public Key? LastColorToggleKey { get; private set; }

    public void SetColorToggleKey(Key? key)
    {
        LastColorToggleKey = key;
    }

    /// <summary>Test helper: simulate the user pressing the assigned global color-toggle key.</summary>
    public void RaiseColorVariantToggle()
    {
        ColorVariantToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
    }
}
