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
    private readonly HashSet<string> _gammaRestorePending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _vibranceRestorePending = new(StringComparer.OrdinalIgnoreCase);

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

            var gamma = ApplyTracked(_applyGamma, _gammaRestorePending, display, profile);
            var vibrance = ApplyTracked(ApplyVibrance, _vibranceRestorePending, display, profile);

            return Merge(gamma, vibrance);
        }
    }

    private static ColorApplyOutcome ApplyTracked(
        Func<DisplayInfo, DisplayColorProfile, ColorApplyOutcome> apply,
        HashSet<string> restorePending,
        DisplayInfo display,
        DisplayColorProfile profile)
    {
        // A failure or exception can follow a partial native write. Record each component before
        // calling it; a later skip cannot discharge an earlier write's restoration obligation.
        var firstAttempt = restorePending.Add(display.Id);
        var outcome = apply(display, profile);
        if ((outcome == ColorApplyOutcome.Applied && !profile.IsEnabled) ||
            (outcome == ColorApplyOutcome.Skipped && firstAttempt))
        {
            restorePending.Remove(display.Id);
        }

        return outcome == ColorApplyOutcome.Skipped && !firstAttempt
            ? ColorApplyOutcome.Failed
            : outcome;
    }

    private ColorApplyOutcome ApplyVibrance(DisplayInfo display, DisplayColorProfile profile) =>
        display.GpuVendor switch
        {
            GpuVendor.Nvidia => _applyNvidiaVibrance(display, profile),
            GpuVendor.Amd => _applyAmdVibrance(display, profile),
            GpuVendor.Intel => ColorApplyOutcome.Skipped,
            _ => ApplyUnknownVendorVibrance(display, profile)
        };

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
