using sWinShortcuts.Models;
using sWinShortcuts.Services;

namespace Tests.Fakes;

public sealed class RecordingColorControlService : IColorControlService
{
    public List<AppliedColorProfile> AppliedProfiles { get; } = [];

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

        return ColorApplyOutcome.Applied;
    }
}

public sealed record AppliedColorProfile(DisplayInfo Display, DisplayColorProfile Profile);
