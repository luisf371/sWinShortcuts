using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

public sealed class DisplayService : IDisplayService, IDisposable
{
    private readonly object _lock = new();
    private List<DisplayInfo>? _cachedDisplays;
    private bool _disposed;

    // Known manufacturer codes from EDID PNP database
    private static readonly Dictionary<string, string> ManufacturerCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ACI", "ASUS" },
        { "ACR", "Acer" },
        { "AOC", "AOC" },
        { "AUO", "AU Optronics" },
        { "BNQ", "BenQ" },
        { "CMN", "Chimei Innolux" },
        { "DEL", "Dell" },
        { "ENC", "Eizo" },
        { "EIZ", "Eizo" },
        { "GSM", "LG" },
        { "HPN", "HP" },
        { "HWP", "HP" },
        { "LEN", "Lenovo" },
        { "LGD", "LG Display" },
        { "NEC", "NEC" },
        { "PHL", "Philips" },
        { "SAM", "Samsung" },
        { "SDC", "Samsung" },
        { "SEC", "Samsung" },
        { "SNY", "Sony" },
        { "VSC", "ViewSonic" },
        { "MSI", "MSI" },
        { "GBT", "Gigabyte" },
        { "IVM", "Iiyama" },
    };

    public DisplayService()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public IReadOnlyList<DisplayInfo> GetDisplays()
    {
        lock (_lock)
        {
            if (_cachedDisplays is not null)
            {
                return _cachedDisplays;
            }

            _cachedDisplays = RefreshDisplays();
            return _cachedDisplays;
        }
    }

    private static List<DisplayInfo> RefreshDisplays()
    {
        var list = new List<DisplayInfo>();

        foreach (var screen in Screen.AllScreens)
        {
            var friendlyName = GetBestDisplayName(screen.DeviceName);
            
            // Fallback if empty or generic
            if (string.IsNullOrWhiteSpace(friendlyName) || 
                friendlyName.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase) ||
                friendlyName.Equals("Generic Monitor", StringComparison.OrdinalIgnoreCase))
            {
                friendlyName = screen.DeviceName;
            }
            
            // Add primary indicator for clarity
            if (screen.Primary)
            {
                friendlyName += " (Primary)";
            }

            list.Add(new DisplayInfo
            {
                Id = screen.DeviceName,
                DeviceName = screen.DeviceName,
                Name = friendlyName,
                IsPrimary = screen.Primary
            });
        }

        return list;
    }

    /// <summary>
    /// Tries multiple methods to get the best display name:
    /// 1. EDID from Windows registry (most accurate - e.g., "Dell U2720Q")
    /// 2. EnumDisplayDevices API (fallback)
    /// </summary>
    private static string GetBestDisplayName(string adapterDeviceName)
    {
        // First try EDID from registry (most reliable for actual model names)
        var edidName = GetDisplayNameFromEdid(adapterDeviceName);
        if (!string.IsNullOrWhiteSpace(edidName))
        {
            return edidName;
        }

        // Fallback to EnumDisplayDevices
        return GetFriendlyNameFromApi(adapterDeviceName);
    }

    /// <summary>
    /// Gets the display name by reading EDID data from the Windows registry.
    /// EDID contains the actual manufacturer and model information.
    /// </summary>
    private static string GetDisplayNameFromEdid(string adapterDeviceName)
    {
        try
        {
            // Get the monitor device path to find the right registry key
            var monitorDeviceId = GetMonitorDeviceId(adapterDeviceName);
            if (string.IsNullOrEmpty(monitorDeviceId))
            {
                return string.Empty;
            }

            // Look for EDID in the registry under HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY
            using var displayKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
            if (displayKey == null)
            {
                return string.Empty;
            }

            foreach (var monitorKeyName in displayKey.GetSubKeyNames())
            {
                using var monitorKey = displayKey.OpenSubKey(monitorKeyName);
                if (monitorKey == null) continue;

                foreach (var instanceKeyName in monitorKey.GetSubKeyNames())
                {
                    using var instanceKey = monitorKey.OpenSubKey(instanceKeyName);
                    if (instanceKey == null) continue;

                    // Check if this instance matches our monitor
                    var driver = instanceKey.GetValue("Driver") as string;
                    if (string.IsNullOrEmpty(driver)) continue;

                    // Try to match by checking if this monitor is active
                    using var paramsKey = instanceKey.OpenSubKey("Device Parameters");
                    if (paramsKey == null) continue;

                    var edid = paramsKey.GetValue("EDID") as byte[];
                    if (edid == null || edid.Length < 128) continue;

                    // Parse the EDID to get manufacturer and model
                    var parsedName = ParseEdid(edid);
                    if (!string.IsNullOrWhiteSpace(parsedName))
                    {
                        // We found a valid EDID. Now check if this is our monitor.
                        // The monitorKeyName typically contains the hardware ID which should match.
                        if (monitorDeviceId.Contains(monitorKeyName, StringComparison.OrdinalIgnoreCase))
                        {
                            return parsedName;
                        }
                    }
                }
            }

            // If we couldn't match by ID, try to get any valid EDID name as fallback
            // (This happens when the monitor ID format doesn't match exactly)
            return GetAnyMatchingEdidName(displayKey, adapterDeviceName);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetAnyMatchingEdidName(RegistryKey displayKey, string adapterDeviceName)
    {
        // Extract display number (e.g., "1" from "\\.\DISPLAY1")
        var displayNum = adapterDeviceName.Replace(@"\\.\DISPLAY", "", StringComparison.OrdinalIgnoreCase);
        var displayIndex = int.TryParse(displayNum, out var idx) ? idx - 1 : -1;

        var foundDisplays = new List<string>();

        foreach (var monitorKeyName in displayKey.GetSubKeyNames())
        {
            using var monitorKey = displayKey.OpenSubKey(monitorKeyName);
            if (monitorKey == null) continue;

            foreach (var instanceKeyName in monitorKey.GetSubKeyNames())
            {
                using var instanceKey = monitorKey.OpenSubKey(instanceKeyName);
                if (instanceKey == null) continue;

                using var paramsKey = instanceKey.OpenSubKey("Device Parameters");
                if (paramsKey == null) continue;

                var edid = paramsKey.GetValue("EDID") as byte[];
                if (edid == null || edid.Length < 128) continue;

                var parsedName = ParseEdid(edid);
                if (!string.IsNullOrWhiteSpace(parsedName))
                {
                    foundDisplays.Add(parsedName);
                }
            }
        }

        // Return the display at the matching index if we have enough displays
        if (displayIndex >= 0 && displayIndex < foundDisplays.Count)
        {
            return foundDisplays[displayIndex];
        }

        return string.Empty;
    }

    /// <summary>
    /// Gets the monitor device ID from the display adapter name using SetupAPI.
    /// </summary>
    private static string GetMonitorDeviceId(string adapterDeviceName)
    {
        try
        {
            var device = new NativeMethods.DISPLAY_DEVICE();
            device.Initialize();

            if (NativeMethods.EnumDisplayDevices(adapterDeviceName, 0, ref device, 0))
            {
                return device.DeviceID ?? string.Empty;
            }
        }
        catch
        {
            // Ignore errors
        }

        return string.Empty;
    }

    /// <summary>
    /// Parses EDID binary data to extract manufacturer name and model descriptor.
    /// EDID structure: https://en.wikipedia.org/wiki/Extended_Display_Identification_Data
    /// </summary>
    private static string ParseEdid(byte[] edid)
    {
        if (edid.Length < 128)
        {
            return string.Empty;
        }

        // Bytes 8-9: Manufacturer ID (3 characters encoded in 2 bytes)
        var manufacturerCode = DecodeManufacturerId(edid[8], edid[9]);
        var manufacturer = ManufacturerCodes.TryGetValue(manufacturerCode, out var name) 
            ? name 
            : manufacturerCode;

        // Look for monitor name descriptor in the descriptor blocks (bytes 54-125)
        // Each descriptor is 18 bytes. Descriptor tag 0xFC indicates monitor name.
        var modelName = string.Empty;
        
        for (var i = 54; i + 18 <= 126; i += 18)
        {
            // Check if this is a descriptor block (first 2 bytes are 0)
            if (edid[i] == 0 && edid[i + 1] == 0)
            {
                var tag = edid[i + 3];
                
                // 0xFC = Monitor name descriptor
                if (tag == 0xFC)
                {
                    modelName = ExtractDescriptorString(edid, i + 5, 13);
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(modelName))
        {
            // Avoid duplicating manufacturer name if it's already in the model name
            if (modelName.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase))
            {
                return modelName;
            }
            return $"{manufacturer} {modelName}";
        }

        // Fallback: use manufacturer + product code
        var productCode = (edid[11] << 8) | edid[10];
        if (productCode != 0)
        {
            return $"{manufacturer} {productCode:X4}";
        }

        return manufacturer;
    }

    /// <summary>
    /// Decodes the manufacturer ID from EDID bytes 8-9.
    /// The ID is encoded as 3 5-bit characters (A=1, B=2, ..., Z=26).
    /// </summary>
    private static string DecodeManufacturerId(byte byte1, byte byte2)
    {
        var chars = new char[3];
        chars[0] = (char)('A' + ((byte1 >> 2) & 0x1F) - 1);
        chars[1] = (char)('A' + (((byte1 & 0x03) << 3) | ((byte2 >> 5) & 0x07)) - 1);
        chars[2] = (char)('A' + (byte2 & 0x1F) - 1);
        return new string(chars);
    }

    /// <summary>
    /// Extracts a string from an EDID descriptor block.
    /// Strings are padded with 0x0A (newline) and spaces.
    /// </summary>
    private static string ExtractDescriptorString(byte[] edid, int offset, int maxLength)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < maxLength && offset + i < edid.Length; i++)
        {
            var c = edid[offset + i];
            if (c == 0x0A || c == 0x00) // Newline or null terminates the string
            {
                break;
            }
            if (c >= 0x20 && c < 0x7F) // Printable ASCII
            {
                sb.Append((char)c);
            }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Fallback: Gets the friendly name using EnumDisplayDevices API.
    /// </summary>
    private static string GetFriendlyNameFromApi(string adapterDeviceName)
    {
        try
        {
            var device = new NativeMethods.DISPLAY_DEVICE();
            device.Initialize();

            if (NativeMethods.EnumDisplayDevices(adapterDeviceName, 0, ref device, 0))
            {
                if (!string.IsNullOrWhiteSpace(device.DeviceString))
                {
                    return device.DeviceString;
                }
            }
        }
        catch
        {
            // Fail safely
        }

        return string.Empty;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            _cachedDisplays = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _disposed = true;
    }
}
