using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using sWinShortcuts.Configuration;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public sealed class ProfileManager(IProfileStore store) : IProfileManager
{
    private readonly IProfileStore _store = store;
    private readonly List<Profile> _profiles = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Immutable snapshot swapped under _gate after every mutation. Read APIs (called from the background
    // activation worker AND the UI) read this without locking, so a foreground change can never race a
    // profile add/remove/rename over the live List.
    private volatile IReadOnlyList<Profile> _snapshot = Array.Empty<Profile>();

    public event EventHandler<Profile>? ProfileAdded;

    public event EventHandler<Profile>? ProfileRemoved;

    public IReadOnlyList<Profile> Profiles => _snapshot;

    public Profile WindowsProfile => _snapshot.First(p => p.IsWindowsProfile);

    public Profile ColorProfile => _snapshot.First(p => p.IsColorProfile);

    // Must be called while holding _gate.
    private void RebuildSnapshot() => _snapshot = _profiles.ToArray();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_profiles.Count > 0)
            {
                return;
            }

            var loaded = await _store.LoadProfilesAsync(cancellationToken).ConfigureAwait(false);
            _profiles.Clear();
            _profiles.AddRange(loaded);

            if (!_profiles.Any(p => p.IsWindowsProfile))
            {
                var windowsProfile = ProfileFactory.CreateWindowsProfile();
                _profiles.Insert(0, windowsProfile);
            }

            if (!_profiles.Any(p => p.IsColorProfile))
            {
                var colorProfile = ProfileFactory.CreateColorProfile();
                var insertIndex = _profiles.Any() && _profiles[0].IsWindowsProfile ? 1 : 0;
                _profiles.Insert(insertIndex, colorProfile);
            }

            DeduplicateProfileNames();
            RebuildSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Two on-disk files can both declare Name=Foo (names come from INI content, not the filename),
    // which would otherwise make every save on either throw a duplicate-name error. Keep each profile
    // on its own file (identity = SourcePath) and only rename the colliding display name.
    private void DeduplicateProfileNames()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ProfileConstants.WindowsProfileName,
            ProfileConstants.ColorProfileName
        };

        // Deterministic order (by file path) so suffixes stay stable across restarts.
        var custom = _profiles
            .Where(p => !p.IsWindowsProfile && !p.IsColorProfile)
            .OrderBy(p => p.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var profile in custom)
        {
            var baseName = string.IsNullOrWhiteSpace(profile.Name) ? "Profile" : profile.Name;
            var candidate = baseName;
            for (var n = 2; used.Contains(candidate); n++)
            {
                candidate = $"{baseName} ({n})";
            }

            if (!string.Equals(candidate, profile.Name, StringComparison.Ordinal))
            {
                profile.Name = candidate;
            }

            used.Add(candidate);
        }
    }

    public async Task<Profile> AddProfileAsync(string name, string executable, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        var profile = ProfileFactory.CreateCustomProfile(name.Trim(), executable.Trim());

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_profiles.Any(p => string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Profile named '{name}' already exists.");
            }

            if (!string.IsNullOrWhiteSpace(profile.NormalizedExecutable) &&
                _profiles.Any(p => string.Equals(p.NormalizedExecutable, profile.NormalizedExecutable, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"Profile for executable '{executable}' already exists.");
            }

            await _store.SaveProfileAsync(profile, cancellationToken).ConfigureAwait(false);
            _profiles.Add(profile);
            RebuildSnapshot();
        }
        finally
        {
            _gate.Release();
        }

        ProfileAdded?.Invoke(this, profile);
        return profile;
    }

    public async Task RemoveProfileAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (profile.IsWindowsProfile)
            {
                throw new InvalidOperationException("The Windows profile cannot be removed.");
            }
            
            if (profile.IsColorProfile)
            {
                throw new InvalidOperationException("The Color Settings profile cannot be removed.");
            }

            if (_profiles.Remove(profile))
            {
                RebuildSnapshot();
                await _store.DeleteProfileAsync(profile, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return;
            }
        }
        finally
        {
            _gate.Release();
        }

        ProfileRemoved?.Invoke(this, profile);
    }

    public async Task RenameProfileAsync(Profile profile, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        var trimmed = newName.Trim();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (profile.IsWindowsProfile || profile.IsColorProfile)
            {
                throw new InvalidOperationException("Built-in profiles cannot be renamed.");
            }

            if (!_profiles.Contains(profile))
            {
                throw new InvalidOperationException("Profile is not managed by this manager.");
            }

            if (string.Equals(trimmed, ProfileConstants.WindowsProfileName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, ProfileConstants.ColorProfileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"'{trimmed}' is a reserved profile name.");
            }

            if (_profiles.Any(p => !ReferenceEquals(p, profile) &&
                                   string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A profile named '{trimmed}' already exists.");
            }

            // Keep the SAME Profile instance (preserves selection + autosave keying) and write back to
            // its existing SourcePath, so the rename can never clobber another profile's file. Roll the
            // name back if the save fails so a reported failure doesn't leave a half-renamed model.
            var oldName = profile.Name;
            profile.Name = trimmed;
            try
            {
                await _store.SaveProfileAsync(profile, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                profile.Name = oldName;
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public Profile? FindByExecutable(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        var normalized = NormalizeExecutable(executableName);

        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        // Read the immutable snapshot (not the live _profiles) so this can't race an add/remove.
        var snapshot = _snapshot;
        return snapshot.FirstOrDefault(p =>
            string.Equals(p.NormalizedExecutable, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public async Task SaveProfileAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_profiles.Contains(profile))
            {
                throw new InvalidOperationException("Profile is not managed by this manager.");
            }

            // Check for duplicate profile name (excluding the current profile)
            if (_profiles.Any(p => !ReferenceEquals(p, profile) &&
                                   string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A profile named '{profile.Name}' already exists.");
            }

            await _store.SaveProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string NormalizeExecutable(string executable) => Utilities.ExecutableName.Normalize(executable);
}
