using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public interface IProfileManager
{
    IReadOnlyList<Profile> Profiles { get; }

    Profile WindowsProfile { get; }
    
    Profile ColorProfile { get; }

    event EventHandler<Profile>? ProfileAdded;

    event EventHandler<Profile>? ProfileRemoved;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<Profile> AddProfileAsync(string name, string executable, CancellationToken cancellationToken = default);

    Task RemoveProfileAsync(Profile profile, CancellationToken cancellationToken = default);

    Task RenameProfileAsync(Profile profile, string newName, CancellationToken cancellationToken = default);

    Profile? FindByExecutable(string executableName);

    Task SaveProfileAsync(Profile profile, CancellationToken cancellationToken = default);

    Task SaveProfileSnapshotAsync(
        Profile managedProfile,
        Profile persistenceSnapshot,
        CancellationToken cancellationToken = default);
}
