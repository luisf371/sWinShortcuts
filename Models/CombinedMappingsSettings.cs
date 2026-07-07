using System;
using System.Collections.Generic;

namespace sWinShortcuts.Models;

public sealed class CombinedMappingsSettings
{
    private volatile List<CombinedMappingEntry> _mappings = new();

    public bool IsEnabled { get; set; }

    // Settable so the UI publishes edits by swapping in a fully built list (copy-on-write): the hook
    // thread and the pool-thread INI serializer enumerate whatever reference they grabbed as a stable
    // snapshot, so an edit can never race their foreach. Loading may still mutate the fresh list in
    // place (pre-publication, single-threaded).
    public List<CombinedMappingEntry> Mappings
    {
        get => _mappings;
        set => _mappings = value ?? throw new ArgumentNullException(nameof(value));
    }
}
