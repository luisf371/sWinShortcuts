using System;

namespace sWinShortcuts.Configuration;

// F-008: thrown when a save targets a profile whose on-disk source was unreadable at load, so its
// in-memory state is factory defaults. The store refuses to overwrite the preserved source; the
// dirty-tracking layer catches this to KEEP the edit (in memory) without reporting a false successful
// save (which would clear the dirty flag and silently lose the change).
public sealed class PersistenceSuspendedException(string profileName)
    : Exception($"'{profileName}' could not be read from disk and is preserved read-only; changes are not being saved until it can be read again.")
{
    public string ProfileName { get; } = profileName;
}
