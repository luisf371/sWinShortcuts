using System.Windows.Input;

namespace sWinShortcuts.Models;

public sealed class CombinedMappingEntry
{
    public InputTrigger Source { get; set; }
    public Key TargetKey { get; set; }
    public bool SuppressOriginalKey { get; set; } = true;
    public bool RightClickOnly { get; set; } = false;
}
