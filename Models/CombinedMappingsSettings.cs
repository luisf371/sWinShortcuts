using System.Collections.Generic;

namespace sWinShortcuts.Models;

public sealed class CombinedMappingsSettings
{
    public bool IsEnabled { get; set; }
    public List<CombinedMappingEntry> Mappings { get; } = new();
}

