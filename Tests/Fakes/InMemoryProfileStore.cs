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

    public Task<IReadOnlyList<Profile>> LoadProfilesAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<Profile>>(_profiles.ToList());
    }

    public Task SaveProfileAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        
        var existing = _profiles.FirstOrDefault(p => p.Name == profile.Name);
        if (existing is not null && !ReferenceEquals(existing, profile))
        {
            _profiles.Remove(existing);
        }
        
        if (!_profiles.Contains(profile))
        {
            _profiles.Add(profile);
        }
        
        return Task.CompletedTask;
    }

    public Task DeleteProfileAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profiles.Remove(profile);
        _deletedNames.Add(profile.Name);
        return Task.CompletedTask;
    }

    public bool WasDeleted(string name) => _deletedNames.Contains(name);
}
