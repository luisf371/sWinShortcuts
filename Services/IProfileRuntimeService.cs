using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public interface IProfileRuntimeService
{
    void NotifyProfileChanged(Profile profile, ProfileChangeKind changeKind);
}
