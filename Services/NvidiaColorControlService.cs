using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

/// <summary>
/// Tries to apply brightness/contrast/gamma using Windows gamma ramps and digital vibrance via NVAPI (best effort).
/// </summary>
public sealed class NvidiaColorControlService : IColorControlService, IDisposable
{
    private const int DvcMinLevel = 0;
    private const int DvcMaxLevel = 63;

    private readonly ILoggerService _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, IntPtr> _handleCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _nvapiInitialized;
    private bool _nvapiAvailableChecked;

    public NvidiaColorControlService(ILoggerService logger)
    {
        _logger = logger;
        NvApiNative.Logger = logger;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    public bool Apply(DisplayInfo display, DisplayColorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(profile);

        lock (_sync)
        {
            var attempted = false;

            _logger.Log($@"[Color][NVAPI] Apply requested for display '{display.DeviceName}' (Id='{display.Id}').
                        Brightness={profile.Brightness}, Contrast={profile.Contrast}, Gamma={profile.Gamma}, DigitalVibrance={profile.DigitalVibrance}");

            // Apply gamma ramp as a baseline so brightness/contrast/gamma always work, even without NVAPI.
            attempted |= TryApplyGammaRampToDevice(profile, display.DeviceName);

            // Try digital vibrance via NVAPI; if NVAPI isn't available this silently fails.
            attempted |= TryApplyNvapiDvc(display, profile);

            return attempted;
        }
    }

    private bool TryApplyGammaRamp(DisplayColorProfile profile)
    {
        return TryApplyGammaRampToDevice(profile, null);
    }

    private static NativeMethods.GammaRamp BuildGammaRamp(DisplayColorProfile profile)
    {
        // Normalize values: 50 is neutral brightness/contrast, gamma is direct.
        var brightnessOffset = (profile.Brightness - 50) / 50.0; // -1..1
        var contrastFactor = Math.Max(0.1, profile.Contrast / 50.0); // avoid divide-by-zero
        var gamma = Math.Clamp(profile.Gamma, 0.5, 3.0);

        var ramp = new NativeMethods.GammaRamp();
        for (int i = 0; i < 256; i++)
        {
            var normalized = i / 255.0;

            // Apply contrast around midpoint, then brightness shift.
            var adjusted = (normalized - 0.5) * contrastFactor + 0.5 + (brightnessOffset * 0.5);
            adjusted = Math.Pow(Math.Clamp(adjusted, 0, 1), 1.0 / gamma);

            var value = (ushort)Math.Clamp((int)(adjusted * 65535.0 + 0.5), 0, 65535);
            ramp.Red[i] = value;
            ramp.Green[i] = value;
            ramp.Blue[i] = value;
        }

        return ramp;
    }

    private bool TryApplyGammaRampToDevice(DisplayColorProfile profile, string? deviceName)
    {
        IntPtr hdc = IntPtr.Zero;
        var createdDc = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                hdc = NativeMethods.CreateDC(null, deviceName, null, IntPtr.Zero);
                createdDc = hdc != IntPtr.Zero;
            }

            if (hdc == IntPtr.Zero)
            {
                hdc = NativeMethods.GetDC(IntPtr.Zero);
            }

            if (hdc == IntPtr.Zero)
            {
                return false;
            }

            var ramp = BuildGammaRamp(profile);
            return NativeMethods.SetDeviceGammaRamp(hdc, ref ramp);
        }
        finally
        {
            if (hdc != IntPtr.Zero)
            {
                if (createdDc)
                {
                    NativeMethods.DeleteDC(hdc);
                }
                else
                {
                    NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
                }
            }
        }
    }

    private bool TryApplyNvapiDvc(DisplayInfo display, DisplayColorProfile profile)
    {
        if (!EnsureNvapi())
        {
            _logger.Log("[Color][NVAPI] NvAPI is not available; skipping digital vibrance.");
            return false;
        }

        var targetHandle = FindDisplayHandle(display.DeviceName);
        if (targetHandle != IntPtr.Zero)
        {
            if (ApplyDvc(targetHandle, profile))
            {
                return true;
            }

            EvictCachedHandle(display.DeviceName);
            var refreshedHandle = FindDisplayHandle(display.DeviceName);
            if (refreshedHandle != IntPtr.Zero && refreshedHandle != targetHandle)
            {
                return ApplyDvc(refreshedHandle, profile);
            }

            return false;
        }

        // Fallback: try all handles if we couldn't map the device
        var success = false;
        for (int i = 0; ; i++)
        {
            var status = NvApiNative.NvAPI_EnumNvidiaDisplayHandle(i, out var handle);
            if (status == NvApiNative.NVAPI_END_ENUMERATION)
            {
                _logger.Log("[Color][NVAPI] Reached end of NV display enumeration.");
                break;
            }

            if (status != NvApiNative.NVAPI_OK || handle == IntPtr.Zero)
            {
                _logger.Log($"[Color][NVAPI] NvAPI_EnumNvidiaDisplayHandle({{i}}) failed or returned null handle. Status={{status}} Handle={{handle}}.");
                continue;
            }

            _logger.Log($"[Color][NVAPI] Trying DVC apply on enumerated handle index {i}.");
            success |= ApplyDvc(handle, profile);
        }

        if (!success)
        {
            _logger.Log("[Color][NVAPI] Digital vibrance apply via NVAPI did not succeed on any handle.");
        }

        return success;
    }

    private bool ApplyDvc(IntPtr displayHandle, DisplayColorProfile profile)
    {
        var nvLevel = ConvertPercentToNvLevel(profile.DigitalVibrance);
        _logger.Log($"[Color][NVAPI] Applying DVC level {nvLevel} for requested {profile.DigitalVibrance}%.");

        var setStatus = NvApiNative.NvAPI_DVC_SetLevel(displayHandle, 0, nvLevel);
        if (setStatus != NvApiNative.NVAPI_OK)
        {
            _logger.Log($"[Color][NVAPI] NvAPI_DVC_SetLevel failed. Status={{setStatus}}.");
        }
        else
        {
            _logger.Log("[Color][NVAPI] NvAPI_DVC_SetLevel succeeded.");
        }
        return setStatus == NvApiNative.NVAPI_OK;
    }

    private static int ConvertPercentToNvLevel(int percent)
    {
        const int NvNeutralPercent = DisplayColorProfile.DefaultDigitalVibrance;
        var clampedPercent = Math.Clamp(percent, NvNeutralPercent, 100);
        var normalized = (clampedPercent - NvNeutralPercent) / (100.0 - NvNeutralPercent);
        var span = Math.Max(1, DvcMaxLevel - DvcMinLevel);
        return DvcMinLevel + (int)Math.Round(normalized * span);
    }

    private bool EnsureNvapi()
    {
        lock (_sync)
        {
            if (_nvapiInitialized)
            {
                return true;
            }

            if (_nvapiAvailableChecked && !_nvapiInitialized)
            {
                _logger.Log("[Color][NVAPI] NvAPI previously checked and unavailable.");
                return false;
            }

            _nvapiAvailableChecked = true;

            try
            {
                var status = NvApiNative.NvAPI_Initialize();
                _nvapiInitialized = status == NvApiNative.NVAPI_OK;
                _logger.Log($"[Color][NVAPI] NvAPI_Initialize called. Status={{status}}, Initialized={_nvapiInitialized}.");
            }
            catch
            {
                _nvapiInitialized = false;
                _logger.Log("[Color][NVAPI] Exception during NvAPI_Initialize. Marking as unavailable.");
            }

            return _nvapiInitialized;
        }
    }

    private IntPtr FindDisplayHandle(string deviceName)
    {
        // deviceName usually looks like "\\.\DISPLAY1"
        var cacheKey = deviceName ?? string.Empty;
        var normalized = cacheKey.Replace(@"\\.\", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return IntPtr.Zero;
        }

        if (_handleCache.TryGetValue(cacheKey, out var cachedHandle))
        {
            return cachedHandle;
        }

        _logger.Log($"[Color][NVAPI] Resolving NVAPI display handle for device '{cacheKey}' (normalized='{normalized}').");

        for (int i = 0; ; i++)
        {
            var status = NvApiNative.NvAPI_EnumNvidiaDisplayHandle(i, out var handle);
            if (status == NvApiNative.NVAPI_END_ENUMERATION)
            {
                _logger.Log("[Color][NVAPI] Reached end of NV display enumeration while resolving handle.");
                break;
            }

            if (status != NvApiNative.NVAPI_OK || handle == IntPtr.Zero)
            {
                _logger.Log($"[Color][NVAPI] NvAPI_EnumNvidiaDisplayHandle({{i}}) failed or returned null handle while resolving. Status={{status}} Handle={{handle}}.");
                continue;
            }

            var nameBuilder = new StringBuilder(NvApiNative.NVAPI_DEFAULT_STRING_MAX);
            var nameStatus = NvApiNative.NvAPI_GetAssociatedDisplayName(handle, nameBuilder);
            if (nameStatus != NvApiNative.NVAPI_OK)
            {
                _logger.Log($"[Color][NVAPI] NvAPI_GetAssociatedDisplayName({{i}}) failed. Status={{nameStatus}}.");
                continue;
            }

            var name = nameBuilder.ToString();
            if (name.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Log($"[Color][NVAPI] Matched NVAPI display handle index {i} for device '{cacheKey}' with NV name '{name}'.");
                _handleCache[cacheKey] = handle;
                return handle;
            }

        }

        _logger.Log($"[Color][NVAPI] No matching NVAPI display handle found for device '{cacheKey}'.");
        return IntPtr.Zero;
    }

    private void EvictCachedHandle(string deviceName)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            _handleCache.Remove(deviceName);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        lock (_sync)
        {
            _handleCache.Clear();
        }
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        lock (_sync)
        {
            _handleCache.Clear();

            if (_nvapiInitialized)
            {
                try
                {
                    NvApiNative.NvAPI_Unload();
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private static class NvApiNative
    {
        internal static ILoggerService? Logger;

        private static void Log(string message) => Logger?.Log(message);

        internal const int NVAPI_OK = 0;
        internal const int NVAPI_END_ENUMERATION = -8;
        internal const int NVAPI_DEFAULT_STRING_MAX = 64;

        // Function IDs from NVAPI headers (nvapi.h)
        private const uint NVFUNC_Initialize = 0x0150E828;
        private const uint NVFUNC_Unload = 0xD22BDD7E;
        private const uint NVFUNC_EnumNvidiaDisplayHandle = 0x9ABDD40D;
        private const uint NVFUNC_GetAssociatedDisplayName = 0x22A78B05;
        private const uint NVFUNC_DVC_SetLevel = 0x172409B4;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_InitializeDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_UnloadDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_EnumNvidiaDisplayHandleDelegate(int thisEnum, out IntPtr displayHandle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_GetAssociatedDisplayNameDelegate(IntPtr displayHandle, StringBuilder name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NvAPI_DVC_SetLevelDelegate(IntPtr displayHandle, int outputId, int level);

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NvAPI_QueryInterface64(uint functionId);

        [DllImport("nvapi.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NvAPI_QueryInterface32(uint functionId);

        private static readonly object _loadSync = new();
        private static bool _functionsLoaded;
        private static NvAPI_InitializeDelegate? _initialize;
        private static NvAPI_UnloadDelegate? _unload;
        private static NvAPI_EnumNvidiaDisplayHandleDelegate? _enumDisplay;
        private static NvAPI_GetAssociatedDisplayNameDelegate? _getDisplayName;
        private static NvAPI_DVC_SetLevelDelegate? _dvcSetLevel;

        private static bool EnsureFunctionsLoaded()
        {
            if (_functionsLoaded)
            {
                return _initialize is not null;
            }

            lock (_loadSync)
            {
                if (_functionsLoaded)
                {
                    return _initialize is not null;
                }

                try
                {
                    _initialize = GetDelegate<NvAPI_InitializeDelegate>(NVFUNC_Initialize);
                    _unload = GetDelegate<NvAPI_UnloadDelegate>(NVFUNC_Unload);
                    _enumDisplay = GetDelegate<NvAPI_EnumNvidiaDisplayHandleDelegate>(NVFUNC_EnumNvidiaDisplayHandle);
                    _getDisplayName = GetDelegate<NvAPI_GetAssociatedDisplayNameDelegate>(NVFUNC_GetAssociatedDisplayName);
                    _dvcSetLevel = GetDelegate<NvAPI_DVC_SetLevelDelegate>(NVFUNC_DVC_SetLevel);
                }
                catch (DllNotFoundException ex)
                {
                    Log($"[Color][NVAPI] DllNotFoundException: {{ex.Message}}");
                    _initialize = null;
                }
                catch (EntryPointNotFoundException ex)
                {
                    Log($"[Color][NVAPI] EntryPointNotFoundException: {{ex.Message}}");
                    _initialize = null;
                }
                catch (Exception ex)
                {
                    Log($"[Color][NVAPI] Unexpected exception loading functions: {{ex}}");
                    _initialize = null;
                }

                _functionsLoaded = true;
                return _initialize is not null;
            }
        }

        private static IntPtr QueryInterface(uint functionId)
        {
            try
            {
                return Environment.Is64BitProcess
                    ? NvAPI_QueryInterface64(functionId)
                    : NvAPI_QueryInterface32(functionId);
            }
            catch (DllNotFoundException ex)
            {
                Log($"[Color][NVAPI] NvAPI library not found while querying 0x{{functionId:X8}}: {{ex.Message}}");
                return IntPtr.Zero;
            }
            catch (EntryPointNotFoundException ex)
            {
                Log($"[Color][NVAPI] NvAPI query failed for 0x{{functionId:X8}}: {{ex.Message}}");
                return IntPtr.Zero;
            }
        }

        private static T? GetDelegate<T>(uint id) where T : class
        {
            var ptr = QueryInterface(id);
            if (ptr == IntPtr.Zero)
            {
                Log($"[Color][NVAPI] Failed to get delegate for function ID 0x{{id:X8}} ({{typeof(T).Name}}).");
                return null;
            }

            return Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;
        }

        internal static int NvAPI_Initialize()
        {
            if (!EnsureFunctionsLoaded() || _initialize is null)
            {
                return -1;
            }

            return _initialize();
        }

        internal static int NvAPI_Unload()
        {
            if (!EnsureFunctionsLoaded() || _unload is null)
            {
                return -1;
            }

            return _unload();
        }

        internal static int NvAPI_EnumNvidiaDisplayHandle(int thisEnum, out IntPtr displayHandle)
        {
            displayHandle = IntPtr.Zero;
            if (!EnsureFunctionsLoaded() || _enumDisplay is null)
            {
                return -1;
            }

            return _enumDisplay(thisEnum, out displayHandle);
        }

        internal static int NvAPI_GetAssociatedDisplayName(IntPtr displayHandle, StringBuilder name)
        {
            if (!EnsureFunctionsLoaded() || _getDisplayName is null)
            {
                return -1;
            }

            return _getDisplayName(displayHandle, name);
        }

        internal static int NvAPI_DVC_SetLevel(IntPtr displayHandle, int outputId, int level)
        {
            if (!EnsureFunctionsLoaded() || _dvcSetLevel is null)
            {
                return -1;
            }

            return _dvcSetLevel(displayHandle, outputId, level);
        }
    }
}
