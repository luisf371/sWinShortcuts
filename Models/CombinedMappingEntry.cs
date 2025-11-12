using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class CombinedMappingEntry
{
    public Key SourceKey { get; set; }
    public Key TargetKey { get; set; }
    public bool SuppressOriginalKey { get; set; } = true;
    public bool RightClickOnly { get; set; } = false;
}
