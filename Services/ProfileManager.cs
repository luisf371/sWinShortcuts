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

    public event EventHandler<Profile>? ProfileAdded;

    public event EventHandler<Profile>? ProfileRemoved;

    public IReadOnlyList<Profile> Profiles => new ReadOnlyCollection<Profile>(_profiles);

    public Profile WindowsProfile => _profiles.First(p => p.IsWindowsProfile);

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
        }
        finally
        {
            _gate.Release();
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

            if (_profiles.Remove(profile))
            {
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

    public Profile? FindByExecutable(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        var normalized = NormalizeExecutable(executableName);

        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return _profiles.FirstOrDefault(p =>
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

            await _store.SaveProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string NormalizeExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileNameWithoutExtension(executable);
        return fileName?.Trim().ToLowerInvariant() ?? string.Empty;
    }
}
