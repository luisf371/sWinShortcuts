using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using sWinShortcuts.Models;

namespace sWinShortcuts.Configuration;

public interface IProfileStore
{
    Task<IReadOnlyList<Profile>> LoadProfilesAsync(CancellationToken cancellationToken);

    Task SaveProfileAsync(Profile profile, CancellationToken cancellationToken);

    Task DeleteProfileAsync(Profile profile, CancellationToken cancellationToken);
}
