using System.Collections.Concurrent;
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

    private int _isStarted;
    private int _deactivateCount;
    private int _releaseForegroundAutoRunCount;
    private readonly object _foregroundIdentityLock = new();
    private (IntPtr Hwnd, uint Pid, string? Exe, long Generation)? _lastForegroundIdentity;

    public bool IsStarted => Volatile.Read(ref _isStarted) != 0;

    public Profile? WindowsProfile { get; private set; }

    public ConcurrentQueue<Profile> ActivatedProfiles { get; } = new();

    public ConcurrentQueue<(Profile Profile, long Generation)> Activations { get; } = new();

    public int DeactivateCount => Volatile.Read(ref _deactivateCount);

    public ConcurrentQueue<long> DeactivationGenerations { get; } = new();

    public void Start()
    {
        Volatile.Write(ref _isStarted, 1);
    }

    public void Stop()
    {
        Volatile.Write(ref _isStarted, 0);
    }

    public void ActivateProfile(Profile profile, long foregroundGeneration)
    {
        ActivatedProfiles.Enqueue(profile);
        Activations.Enqueue((profile, foregroundGeneration));
        ActiveProfileChanged?.Invoke(this, profile);
    }

    public void DeactivateProfile(long foregroundGeneration)
    {
        Interlocked.Increment(ref _deactivateCount);
        DeactivationGenerations.Enqueue(foregroundGeneration);
        ActiveProfileChanged?.Invoke(this, null);
    }

    public ConcurrentQueue<(Profile Profile, ProfileChangeKind Kind)> ReconciledChanges { get; } = new();

    public void ReconcileProfileSettings(Profile profile, ProfileChangeKind changeKind)
    {
        ReconciledChanges.Enqueue((profile, changeKind));
    }

    public int ReleaseForegroundAutoRunCount =>
        Volatile.Read(ref _releaseForegroundAutoRunCount);

    public void ReleaseForegroundAutoRun()
    {
        Interlocked.Increment(ref _releaseForegroundAutoRunCount);
    }

    public (IntPtr Hwnd, uint Pid, string? Exe, long Generation)? LastForegroundIdentity
    {
        get
        {
            lock (_foregroundIdentityLock)
            {
                return _lastForegroundIdentity;
            }
        }
    }

    public Action<long>? ForegroundIdentitySet { get; set; }

    public void SetForegroundIdentity(
        IntPtr windowHandle,
        uint processId,
        string? normalizedExecutable,
        long foregroundGeneration)
    {
        lock (_foregroundIdentityLock)
        {
            _lastForegroundIdentity =
                (windowHandle, processId, normalizedExecutable, foregroundGeneration);
        }

        ForegroundIdentitySet?.Invoke(foregroundGeneration);
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
