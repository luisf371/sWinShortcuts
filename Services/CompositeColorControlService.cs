using sWinShortcuts.Models;

namespace sWinShortcuts.Services;

/// <summary>Applies the Windows gamma ramp and routes vibrance to the detected GPU vendor.</summary>
public sealed class CompositeColorControlService : IColorControlService
{
    private readonly Func<DisplayInfo, DisplayColorProfile, ColorApplyOutcome> _applyGamma;
    private readonly Func<DisplayInfo, DisplayColorProfile, ColorApplyOutcome> _applyNvidiaVibrance;
    private readonly Func<DisplayInfo, DisplayColorProfile, ColorApplyOutcome> _applyAmdVibrance;
    private readonly ILoggerService? _logger;
    private readonly object _sync = new();

    public CompositeColorControlService(
        WindowsGammaService gamma,
        NvidiaColorControlService nvidia,
        AmdColorControlService amd,
        ILoggerService logger)
        : this(gamma.ApplyGamma, nvidia.ApplyDigitalVibrance, amd.ApplyDigitalVibrance)
    {
        _logger = logger;
    }

    internal CompositeColorControlService(
        Func<DisplayInfo, DisplayColorProfile, ColorApplyOutcome> applyGamma,
        Func<DisplayInfo, DisplayColorProfile, ColorApplyOutcome> applyNvidiaVibrance,
        Func<DisplayInfo, DisplayColorProfile, ColorApplyOutcome> applyAmdVibrance)
    {
        _applyGamma = applyGamma ?? throw new ArgumentNullException(nameof(applyGamma));
        _applyNvidiaVibrance = applyNvidiaVibrance ?? throw new ArgumentNullException(nameof(applyNvidiaVibrance));
        _applyAmdVibrance = applyAmdVibrance ?? throw new ArgumentNullException(nameof(applyAmdVibrance));
    }

    public ColorApplyOutcome Apply(DisplayInfo display, DisplayColorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(profile);

        lock (_sync)
        {
            _logger?.Log($"[Color] Applying profile to '{display.DeviceName}' using detected vendor {display.GpuVendor}.");

            var gamma = _applyGamma(display, profile);
            var vibrance = display.GpuVendor switch
            {
                GpuVendor.Nvidia => _applyNvidiaVibrance(display, profile),
                GpuVendor.Amd => _applyAmdVibrance(display, profile),
                GpuVendor.Intel => ColorApplyOutcome.Skipped,
                _ => ApplyUnknownVendorVibrance(display, profile)
            };

            return Merge(gamma, vibrance);
        }
    }

    private ColorApplyOutcome ApplyUnknownVendorVibrance(
        DisplayInfo display,
        DisplayColorProfile profile)
    {
        var nvidia = _applyNvidiaVibrance(display, profile);
        return nvidia == ColorApplyOutcome.Skipped
            ? _applyAmdVibrance(display, profile)
            : nvidia;
    }

    private static ColorApplyOutcome Merge(ColorApplyOutcome gamma, ColorApplyOutcome vibrance)
    {
        if (gamma == ColorApplyOutcome.Failed || vibrance == ColorApplyOutcome.Failed)
        {
            return ColorApplyOutcome.Failed;
        }

        return gamma == ColorApplyOutcome.Applied || vibrance == ColorApplyOutcome.Applied
            ? ColorApplyOutcome.Applied
            : ColorApplyOutcome.Skipped;
    }
}
