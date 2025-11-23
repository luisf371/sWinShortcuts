using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Windows.Forms;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public sealed class DisplayService : IDisplayService
{
    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        var friendlyNames = TryGetFriendlyNames();

        var displays = Screen.AllScreens
            .Select((screen, index) => new DisplayInfo
            {
                Id = screen.DeviceName,
                DeviceName = screen.DeviceName,
                Name = BuildFriendlyName(screen, index, friendlyNames),
                IsPrimary = screen.Primary
            })
            .ToList();

        return displays;
    }

    private static string BuildFriendlyName(Screen screen, int index, IReadOnlyList<string> friendlyNames)
    {
        if (index < friendlyNames.Count && !string.IsNullOrWhiteSpace(friendlyNames[index]))
        {
            return friendlyNames[index];
        }

        var label = $"Display {index + 1}";
        if (screen.Primary)
        {
            label += " (Primary)";
        }

        return $"{label} - {screen.DeviceName}";
    }

    private static IReadOnlyList<string> TryGetFriendlyNames()
    {
        var list = new List<string>();

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT UserFriendlyName FROM WmiMonitorID");
            using var results = searcher.Get();
            foreach (ManagementObject mo in results)
            {
                if (mo["UserFriendlyName"] is Array raw && raw is ushort[] arr)
                {
                    var name = new string(arr.Select(c => (char)c).ToArray()).TrimEnd('\0');
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        list.Add(name);
                        continue;
                    }
                }

                list.Add(string.Empty);
            }
        }
        catch
        {
            // Ignore WMI failures and fall back to default naming.
        }

        return list;
    }
}
