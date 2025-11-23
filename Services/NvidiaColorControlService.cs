using System;
using System.Runtime.InteropServices;
using System.Text;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

/// <summary>
/// Tries to apply brightness/contrast/gamma using Windows gamma ramps and digital vibrance via NVAPI (best effort).
/// </summary>
public sealed class NvidiaColorControlService : IColorControlService, IDisposable
{
    private readonly object _sync = new();
    private bool _nvapiInitialized;
    private bool _nvapiAvailableChecked;

    public bool Apply(DisplayInfo display, DisplayColorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(profile);

        var attempted = false;

        // Apply gamma ramp as a baseline so brightness/contrast/gamma always work, even without NVAPI.
        attempted |= TryApplyGammaRampToDevice(profile, display.DeviceName);

        // Try digital vibrance via NVAPI; if NVAPI isn't available this silently fails.
        attempted |= TryApplyNvapiDvc(display, profile);

        return attempted;
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
        try
        {
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                hdc = NativeMethods.CreateDC(null, deviceName, null, IntPtr.Zero);
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
                NativeMethods.DeleteDC(hdc);
                NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
            }
        }
    }

    private bool TryApplyNvapiDvc(DisplayInfo display, DisplayColorProfile profile)
    {
        if (!EnsureNvapi())
        {
            return false;
        }

        var targetHandle = FindDisplayHandle(display.DeviceName);
        if (targetHandle == IntPtr.Zero)
        {
            return false;
        }

        // Fetch info to get the supported range
        NvApiNative.NV_DISPLAY_DVC_INFO info = new() { Version = NvApiNative.NV_DISPLAY_DVC_INFO_VER };
        var getStatus = NvApiNative.NvAPI_DVC_GetInfo(targetHandle, ref info);
        if (getStatus != NvApiNative.NVAPI_OK)
        {
            return false;
        }

        var clamped = Math.Clamp(profile.DigitalVibrance, info.MinLevel, info.MaxLevel);
        info.CurrentLevel = clamped;

        var setStatus = NvApiNative.NvAPI_DVC_SetInfo(targetHandle, ref info);
        return setStatus == NvApiNative.NVAPI_OK;
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
                return false;
            }

            _nvapiAvailableChecked = true;

            try
            {
                var status = NvApiNative.NvAPI_Initialize();
                _nvapiInitialized = status == NvApiNative.NVAPI_OK;
            }
            catch
            {
                _nvapiInitialized = false;
            }

            return _nvapiInitialized;
        }
    }

    private IntPtr FindDisplayHandle(string deviceName)
    {
        // deviceName usually looks like "\\.\DISPLAY1"
        var normalized = (deviceName ?? string.Empty).Replace(@"\\.\", string.Empty, StringComparison.OrdinalIgnoreCase);

        for (int i = 0; ; i++)
        {
            var status = NvApiNative.NvAPI_EnumNvidiaDisplayHandle(i, out var handle);
            if (status == NvApiNative.NVAPI_END_ENUMERATION)
            {
                break;
            }

            if (status != NvApiNative.NVAPI_OK || handle == IntPtr.Zero)
            {
                continue;
            }

            var nameBuilder = new StringBuilder(NvApiNative.NVAPI_DEFAULT_STRING_MAX);
            var nameStatus = NvApiNative.NvAPI_GetAssociatedNvidiaDisplayName(handle, nameBuilder);
            if (nameStatus != NvApiNative.NVAPI_OK)
            {
                continue;
            }

            var name = nameBuilder.ToString();
            if (name.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
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

    private static class NvApiNative
    {
        internal const int NVAPI_OK = 0;
        internal const int NVAPI_END_ENUMERATION = -8;
        internal const int NVAPI_DEFAULT_STRING_MAX = 64;

        internal static readonly int NV_DISPLAY_DVC_INFO_VER = Marshal.SizeOf<NV_DISPLAY_DVC_INFO>() | (1 << 16);

        [StructLayout(LayoutKind.Sequential)]
        internal struct NV_DISPLAY_DVC_INFO
        {
            public int Version;
            public int CurrentLevel;
            public int MinLevel;
            public int MaxLevel;
            public int DefaultLevel;
            public int Colorimetry;
            public int DynamicRange;
        }

        [DllImport("nvapi64.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NvAPI_Initialize();

        [DllImport("nvapi64.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NvAPI_Unload();

        [DllImport("nvapi64.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NvAPI_EnumNvidiaDisplayHandle(int thisEnum, out IntPtr displayHandle);

        [DllImport("nvapi64.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NvAPI_GetAssociatedNvidiaDisplayName(IntPtr displayHandle, StringBuilder name);

        [DllImport("nvapi64.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NvAPI_DVC_SetInfo(IntPtr displayHandle, ref NV_DISPLAY_DVC_INFO info);

        [DllImport("nvapi64.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int NvAPI_DVC_GetInfo(IntPtr displayHandle, ref NV_DISPLAY_DVC_INFO info);
    }
}
