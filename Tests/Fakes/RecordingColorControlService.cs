using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class RecordingColorControlService : IColorControlService
{
    public List<AppliedColorProfile> AppliedProfiles { get; } = [];

    /// <summary>Outcome returned by the next apply (default Applied) so callers' failure paths are drivable.</summary>
    public ColorApplyOutcome Outcome { get; set; } = ColorApplyOutcome.Applied;

    public ColorApplyOutcome Apply(DisplayInfo display, DisplayColorProfile profile)
    {
        AppliedProfiles.Add(new AppliedColorProfile(
            display,
            new DisplayColorProfile
            {
                DisplayId = profile.DisplayId,
                IsEnabled = profile.IsEnabled,
                Brightness = profile.Brightness,
                Contrast = profile.Contrast,
                Gamma = profile.Gamma,
                DigitalVibrance = profile.DigitalVibrance
            }));

        return Outcome;
    }
}

public sealed record AppliedColorProfile(DisplayInfo Display, DisplayColorProfile Profile);
