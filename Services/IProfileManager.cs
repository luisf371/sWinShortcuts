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

    event EventHandler<Profile>? ProfileAdded;

    event EventHandler<Profile>? ProfileRemoved;

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<Profile> AddProfileAsync(string name, string executable, CancellationToken cancellationToken = default);

    Task RemoveProfileAsync(Profile profile, CancellationToken cancellationToken = default);

    Profile? FindByExecutable(string executableName);

    Task SaveProfileAsync(Profile profile, CancellationToken cancellationToken = default);
}
