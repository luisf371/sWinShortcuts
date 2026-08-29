using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace sWinShortcuts.Services;

/// <summary>Thin managed boundary over the AMD Display Library functions used for saturation.</summary>
internal sealed class AmdAdlApi : IAmdAdlApi
{
    private const int ADL_OK = 0;
    private const int ADL_DISPLAY_COLOR_SATURATION = 1 << 2;
    private const int DISPLAY_CONNECTED_AND_MAPPED = 0x3;
    private const int MAX_ADAPTERS = 64;
    private const int MAX_DISPLAYS_PER_ADAPTER = 64;
    private const int ADL_MAX_PATH = 256;

    private static readonly AdlMemoryAllocDelegate MemoryAllocator = AllocateMemory;

    private IntPtr _libraryHandle;
    private IntPtr _context;
    private bool _initialized;

    private Adl2MainControlCreateDelegate? _mainControlCreate;
    private Adl2MainControlDestroyDelegate? _mainControlDestroy;
    private Adl2MainControlRefreshDelegate? _mainControlRefresh;
    private Adl2AdapterNumberOfAdaptersGetDelegate? _adapterCountGet;
    private Adl2AdapterAdapterInfoGetDelegate? _adapterInfoGet;
    private Adl2DisplayDisplayInfoGetDelegate? _displayInfoGet;
    private Adl2DisplayColorCapsGetDelegate? _colorCapsGet;
    private Adl2DisplayColorGetDelegate? _colorGet;
    private Adl2DisplayColorSetDelegate? _colorSet;
    private Adl2FlushDriverDataDelegate? _flushDriverData;

    public bool TryInitialize()
    {
        if (_initialized)
        {
            return true;
        }

        try
        {
            var libraryName = Environment.Is64BitProcess ? "atiadlxx.dll" : "atiadlxy.dll";
            if (!NativeLibrary.TryLoad(
                    libraryName,
                    typeof(AmdAdlApi).Assembly,
                    DllImportSearchPath.System32,
                    out _libraryHandle))
            {
                return false;
            }

            _mainControlCreate = GetExport<Adl2MainControlCreateDelegate>("ADL2_Main_Control_Create");
            _mainControlDestroy = GetExport<Adl2MainControlDestroyDelegate>("ADL2_Main_Control_Destroy");
            _mainControlRefresh = GetExport<Adl2MainControlRefreshDelegate>("ADL2_Main_Control_Refresh");
            _adapterCountGet = GetExport<Adl2AdapterNumberOfAdaptersGetDelegate>("ADL2_Adapter_NumberOfAdapters_Get");
            _adapterInfoGet = GetExport<Adl2AdapterAdapterInfoGetDelegate>("ADL2_Adapter_AdapterInfo_Get");
            _displayInfoGet = GetExport<Adl2DisplayDisplayInfoGetDelegate>("ADL2_Display_DisplayInfo_Get");
            _colorCapsGet = GetExport<Adl2DisplayColorCapsGetDelegate>("ADL2_Display_ColorCaps_Get");
            _colorGet = GetExport<Adl2DisplayColorGetDelegate>("ADL2_Display_Color_Get");
            _colorSet = GetExport<Adl2DisplayColorSetDelegate>("ADL2_Display_Color_Set");
            _flushDriverData = GetExport<Adl2FlushDriverDataDelegate>("ADL2_Flush_Driver_Data");

            if (_mainControlCreate(MemoryAllocator, 1, out _context) != ADL_OK || _context == IntPtr.Zero)
            {
                Cleanup();
                return false;
            }

            _initialized = true;
            return true;
        }
        catch
        {
            Cleanup();
            return false;
        }
    }

    public bool TryRefresh()
    {
        if (!_initialized || _mainControlRefresh is null)
        {
            return false;
        }

        try
        {
            if (_mainControlRefresh(_context) == ADL_OK)
            {
                return true;
            }
        }
        catch
        {
            // Recreate the ADL context after a driver/topology failure.
        }

        Cleanup();
        return false;
    }

    public IReadOnlyList<AmdDisplayTarget> GetDisplays()
    {
        if (!_initialized ||
            _adapterCountGet is null ||
            _adapterInfoGet is null ||
            _displayInfoGet is null)
        {
            return [];
        }

        var adapterCount = 0;
        if (_adapterCountGet(_context, ref adapterCount) != ADL_OK ||
            adapterCount <= 0 ||
            adapterCount > MAX_ADAPTERS)
        {
            return [];
        }

        var adapterSize = Marshal.SizeOf<AdapterInfo>();
        var totalSize = checked(adapterSize * adapterCount);
        var adapterBuffer = Marshal.AllocHGlobal(totalSize);

        try
        {
            Marshal.Copy(new byte[totalSize], 0, adapterBuffer, totalSize);
            for (var index = 0; index < adapterCount; index++)
            {
                Marshal.WriteInt32(IntPtr.Add(adapterBuffer, index * adapterSize), adapterSize);
            }

            if (_adapterInfoGet(_context, adapterBuffer, totalSize) != ADL_OK)
            {
                return [];
            }

            var targets = new HashSet<AmdDisplayTarget>();
            for (var index = 0; index < adapterCount; index++)
            {
                var adapter = Marshal.PtrToStructure<AdapterInfo>(
                    IntPtr.Add(adapterBuffer, index * adapterSize));

                if (adapter.Present == 0 ||
                    adapter.Exists == 0 ||
                    string.IsNullOrWhiteSpace(adapter.DisplayName) ||
                    !IsAmdAdapter(adapter.PnpString))
                {
                    continue;
                }

                AddDisplays(adapter, targets);
            }

            return [.. targets];
        }
        finally
        {
            Marshal.FreeHGlobal(adapterBuffer);
        }
    }

    public bool TryGetSaturationRange(AmdDisplayTarget target, out AmdSaturationRange range)
    {
        range = default;
        if (!_initialized || _colorCapsGet is null || _colorGet is null)
        {
            return false;
        }

        var caps = 0;
        var valid = 0;
        if (_colorCapsGet(_context, target.AdapterIndex, target.DisplayIndex, ref caps, ref valid) != ADL_OK ||
            (caps & valid & ADL_DISPLAY_COLOR_SATURATION) == 0)
        {
            return false;
        }

        var current = 0;
        var @default = 0;
        var min = 0;
        var max = 0;
        var step = 0;
        if (_colorGet(
                _context,
                target.AdapterIndex,
                target.DisplayIndex,
                ADL_DISPLAY_COLOR_SATURATION,
                ref current,
                ref @default,
                ref min,
                ref max,
                ref step) != ADL_OK)
        {
            return false;
        }

        range = new AmdSaturationRange(@default, min, max, step);
        return true;
    }

    public bool TrySetSaturation(AmdDisplayTarget target, int value)
    {
        return _initialized &&
            _colorSet is not null &&
            _colorSet(
                _context,
                target.AdapterIndex,
                target.DisplayIndex,
                ADL_DISPLAY_COLOR_SATURATION,
                value) == ADL_OK;
    }

    public bool TryFlush(int adapterIndex)
    {
        return _initialized &&
            _flushDriverData is not null &&
            _flushDriverData(_context, adapterIndex) == ADL_OK;
    }

    private void AddDisplays(AdapterInfo adapter, HashSet<AmdDisplayTarget> targets)
    {
        var displayCount = 0;
        var displayBuffer = IntPtr.Zero;

        try
        {
            if (_displayInfoGet!(
                    _context,
                    adapter.AdapterIndex,
                    ref displayCount,
                    out displayBuffer,
                    0) != ADL_OK ||
                displayBuffer == IntPtr.Zero ||
                displayCount <= 0 ||
                displayCount > MAX_DISPLAYS_PER_ADAPTER)
            {
                return;
            }

            var displaySize = Marshal.SizeOf<AdlDisplayInfo>();
            for (var index = 0; index < displayCount; index++)
            {
                var display = Marshal.PtrToStructure<AdlDisplayInfo>(
                    IntPtr.Add(displayBuffer, index * displaySize));

                if ((display.InfoValue & DISPLAY_CONNECTED_AND_MAPPED) != DISPLAY_CONNECTED_AND_MAPPED ||
                    display.Id.LogicalAdapterIndex != adapter.AdapterIndex)
                {
                    continue;
                }

                targets.Add(new AmdDisplayTarget(
                    adapter.DisplayName,
                    adapter.AdapterIndex,
                    display.Id.LogicalIndex));
            }
        }
        finally
        {
            if (displayBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(displayBuffer);
            }
        }
    }

    private T GetExport<T>(string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_libraryHandle, name, out var address) || address == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException(name);
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    internal static bool IsAmdAdapter(string? pnpString)
    {
        return !string.IsNullOrWhiteSpace(pnpString) &&
            pnpString.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase);
    }

    private static IntPtr AllocateMemory(int size)
    {
        try
        {
            return size > 0 ? Marshal.AllocHGlobal(size) : IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private void Cleanup()
    {
        if (_context != IntPtr.Zero && _mainControlDestroy is not null)
        {
            try
            {
                _mainControlDestroy(_context);
            }
            catch
            {
                // Best-effort teardown during driver failure.
            }
        }

        _context = IntPtr.Zero;
        _initialized = false;

        if (_libraryHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(_libraryHandle);
            _libraryHandle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Cleanup();
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr AdlMemoryAllocDelegate(int size);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2MainControlCreateDelegate(
        AdlMemoryAllocDelegate callback,
        int enumerateConnectedAdapters,
        out IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2MainControlDestroyDelegate(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2MainControlRefreshDelegate(IntPtr context);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2AdapterNumberOfAdaptersGetDelegate(IntPtr context, ref int adapterCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2AdapterAdapterInfoGetDelegate(IntPtr context, IntPtr adapterInfo, int inputSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2DisplayDisplayInfoGetDelegate(
        IntPtr context,
        int adapterIndex,
        ref int displayCount,
        out IntPtr displayInfo,
        int forceDetect);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2DisplayColorCapsGetDelegate(
        IntPtr context,
        int adapterIndex,
        int displayIndex,
        ref int caps,
        ref int valid);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2DisplayColorGetDelegate(
        IntPtr context,
        int adapterIndex,
        int displayIndex,
        int colorType,
        ref int current,
        ref int @default,
        ref int min,
        ref int max,
        ref int step);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2DisplayColorSetDelegate(
        IntPtr context,
        int adapterIndex,
        int displayIndex,
        int colorType,
        int current);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Adl2FlushDriverDataDelegate(IntPtr context, int adapterIndex);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdapterInfo
    {
        public int Size;
        public int AdapterIndex;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string Udid;

        public int BusNumber;
        public int DeviceNumber;
        public int FunctionNumber;
        public int VendorId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string AdapterName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string DisplayName;

        public int Present;
        public int Exists;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string DriverPath;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string DriverPathExtension;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string PnpString;

        public int OsDisplayIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdlDisplayId
    {
        public int LogicalIndex;
        public int PhysicalIndex;
        public int LogicalAdapterIndex;
        public int PhysicalAdapterIndex;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct AdlDisplayInfo
    {
        public AdlDisplayId Id;
        public int ControllerIndex;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = ADL_MAX_PATH)]
        public string ManufacturerName;

        public int DisplayType;
        public int OutputType;
        public int Connector;
        public int InfoMask;
        public int InfoValue;
    }
}
