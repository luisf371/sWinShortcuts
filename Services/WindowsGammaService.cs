using sWinShortcuts.Interop;
using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

/// <summary>Applies vendor-neutral brightness, contrast, and gamma through Windows GDI.</summary>
public sealed class WindowsGammaService
{
    private readonly ILoggerService _logger;
    private readonly object _sync = new();

    public WindowsGammaService(ILoggerService logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal ColorApplyOutcome ApplyGamma(DisplayInfo display, DisplayColorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(profile);

        lock (_sync)
        {
            return TryApplyGammaRampToDevice(profile, display.DeviceName);
        }
    }

    internal static NativeMethods.GammaRamp BuildGammaRamp(DisplayColorProfile profile)
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

    private ColorApplyOutcome TryApplyGammaRampToDevice(DisplayColorProfile profile, string? deviceName)
    {
        IntPtr hdc = IntPtr.Zero;
        var createdDc = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                hdc = NativeMethods.CreateDC(null, deviceName, null, IntPtr.Zero);
                if (hdc == IntPtr.Zero)
                {
                    // Fail closed: a named per-display gamma apply must affect ONLY that display. If its DC
                    // can't be created (e.g. monitor unplugged mid-plan) do NOT fall back to GetDC(NULL)
                    // (the primary/desktop DC). Treat as a deliberate skip (not a retry-worthy failure).
                    _logger.Log($"[Color] CreateDC failed for device '{deviceName}'; skipping gamma apply (fail-closed).");
                    return ColorApplyOutcome.Skipped;
                }

                createdDc = true;
            }
            else
            {
                // Only the unnamed (whole-desktop) overload legitimately targets the primary DC.
                hdc = NativeMethods.GetDC(IntPtr.Zero);
            }

            if (hdc == IntPtr.Zero)
            {
                return ColorApplyOutcome.Skipped;
            }

            var ramp = BuildGammaRamp(profile);
            return NativeMethods.SetDeviceGammaRamp(hdc, ref ramp)
                ? ColorApplyOutcome.Applied
                : ColorApplyOutcome.Failed;
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
}
