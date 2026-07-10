using sWinShortcuts.Configuration;
using sWinShortcuts.Models;

namespace Tests.Fakes;

/// <summary>
/// In-memory implementation of IProfileStore for testing ProfileManager
/// without touching the filesystem.
/// </summary>
public sealed class InMemoryProfileStore : IProfileStore
{
    private readonly List<Profile> _profiles = [];
    private readonly HashSet<string> _deletedNames = [];

    public List<Profile> Profiles => _profiles;

    // Optional failure injection (F-014 save / F-015 delete durability tests). When set, the operation
    // throws BEFORE mutating internal state, mirroring a real locked/denied file.
    public Exception? SaveException { get; set; }
    public Exception? DeleteException { get; set; }

    // Optional deterministic save gate: when set, SaveProfileAsync awaits it before completing, so a test
    // can hold a save "in flight" and control exactly when it finishes (no flaky sleeps). SaveCount lets a
    // test assert how many persists actually ran (e.g. coalescing).
    public TaskCompletionSource? SaveGate { get; set; }
    public int SaveCount { get; private set; }

    // Signaled when SaveProfileAsync is entered (past the save-loop's top-of-loop guard, so the profile has
    // already left _dirty) — lets a test deterministically act while a save is genuinely in flight.
    public TaskCompletionSource? SaveEntered { get; set; }

    public Task<IReadOnlyList<Profile>> LoadProfilesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<Profile>>(_profiles.ToList());
    }

    public async Task SaveProfileAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        SaveEntered?.TrySetResult(); // signal the save is genuinely in flight (past the loop's top guard)

        // Gate BEFORE the exception so a test can hold a failing save in flight, then release it.
        var gate = SaveGate;
        if (gate is not null)
        {
            await gate.Task.ConfigureAwait(false);
        }

        if (SaveException is not null) throw SaveException;

        var existing = _profiles.FirstOrDefault(p => p.Name == profile.Name);
        if (existing is not null && !ReferenceEquals(existing, profile))
        {
            _profiles.Remove(existing);
        }

        if (!_profiles.Contains(profile))
        {
            _profiles.Add(profile);
        }

        SaveCount++;
    }

    public Task DeleteProfileAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (DeleteException is not null) throw DeleteException;
        _profiles.Remove(profile);
        _deletedNames.Add(profile.Name);
        return Task.CompletedTask;
    }

    public bool WasDeleted(string name) => _deletedNames.Contains(name);
}
