using System.Windows.Input;
using sWinShortcuts.Models;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class MacroCapsShortcutTests
{
    [Theory]
    [InlineData(CapsLockMode.Normal, true, false, Key.B, 1)]
    [InlineData(CapsLockMode.Normal, true, true, Key.B, 1)]
    [InlineData(CapsLockMode.Disabled, false, false, Key.None, 0)]
    [InlineData(CapsLockMode.DoubleNormal, false, false, Key.CapsLock, 2)]
    public async Task Shortcut_GlobalCapsOverride_ReportsConflictAndPreservesDispatch(
        CapsLockMode mode, bool remap, bool customCapsEnabled, Key output, int presses)
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A });
        profile.Macros.Definitions = [profile.Macros.Definitions[0] with { ShortcutKey = Key.CapsLock }];
        // An enabled custom Normal/unremapped setting still falls back to a global override.
        profile.CapsLock.IsEnabled = customCapsEnabled;
        var windows = CreateWindowsCapsProfile();
        windows.CapsLock.Mode = mode;
        windows.CapsLock.IsRemapEnabled = remap;
        service.SetWindowsProfile(windows);
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros | ProfileChangeKind.CapsLock);

        Assert.NotNull(service.GetMacroShortcutError(profile, profile.Macros.Definitions[0].Id));
        MacroPlaybackTests.Press(service, 0x14);
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Equal(presses, sender.Transitions.Count(edge => edge.IsDown));
        Assert.Equal(presses, sender.Transitions.Count(edge => !edge.IsDown));
        Assert.All(sender.Transitions, edge => Assert.Equal(output, edge.Key));
        Assert.Equal(MacroSessionMode.Idle, service.GetMacroSession().Mode);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Shortcut_NoEffectiveGlobalCapsOverride_ExecutesMacro(
        bool masterEnabled, bool capsEnabled, bool remapEnabled)
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A });
        profile.Macros.Definitions = [profile.Macros.Definitions[0] with { ShortcutKey = Key.CapsLock }];
        profile.CapsLock.IsEnabled = true;
        var windows = CreateWindowsCapsProfile();
        windows.IsEnabled = masterEnabled;
        windows.CapsLock.IsEnabled = capsEnabled;
        windows.CapsLock.IsRemapEnabled = remapEnabled;
        service.SetWindowsProfile(windows);
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros | ProfileChangeKind.CapsLock);

        Assert.Null(service.GetMacroShortcutError(profile, profile.Macros.Definitions[0].Id));
        MacroPlaybackTests.Press(service, 0x14);
        MacroPlaybackTests.WaitUntil(() => sender.Transitions.Any(edge => edge.Key == Key.A && !edge.IsDown));
        Assert.Equal(new[] { (Key.A, true), (Key.A, false) },
            sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Playback_GlobalCapsOverrideEnabled_RetiresRunAndReadmitsAfterConflictClears(bool disableCaps)
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyDown, Key = Key.A },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 30000 },
            new MacroStep { Kind = MacroStepKind.KeyUp, Key = Key.A });
        profile.Macros.Definitions = [profile.Macros.Definitions[0] with { ShortcutKey = Key.CapsLock }];
        var id = profile.Macros.Definitions[0].Id;
        var windows = CreateWindowsCapsProfile();
        windows.CapsLock.IsEnabled = false;
        service.SetWindowsProfile(windows);
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        MacroPlaybackTests.Press(service, 0x14);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { Mode: MacroSessionMode.Playing, RowCount: 2 });
        var firstSession = service.GetMacroSession().SessionId;
        // A busy shortcut is consumed without restarting, and its physical UP remains owed.
        Assert.True(service.DispatchDecodedKeyboardEvent(0x14, true, false));

        windows.CapsLock.IsEnabled = true;
        service.ReconcileProfileSettings(windows, ProfileChangeKind.CapsLock);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.Equal(new[] { (Key.A, true), (Key.A, false) },
            sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
        Assert.Single(sender.KeyReleases, edge => edge.Key == Key.A && edge.MacroRelease);
        Assert.True(service.DispatchDecodedKeyboardEvent(0x14, false, true));
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.DoesNotContain(sender.Transitions, edge => edge.Key == Key.B);
        Assert.NotNull(service.GetMacroShortcutError(profile, id));

        if (disableCaps) windows.CapsLock.IsEnabled = false;
        else windows.CapsLock.IsRemapEnabled = false;
        service.ReconcileProfileSettings(windows, ProfileChangeKind.CapsLock);
        Assert.Null(service.GetMacroShortcutError(profile, id));
        MacroPlaybackTests.Press(service, 0x14);
        MacroPlaybackTests.WaitUntil(() => sender.Transitions.Count(edge => edge.Key == Key.A && edge.IsDown) == 2);
        Assert.NotEqual(firstSession, service.GetMacroSession().SessionId);
        MacroPlaybackTests.Press(service, 0x7B);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.Equal(new[] { (Key.A, true), (Key.A, false), (Key.A, true), (Key.A, false) },
            sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
    }

    private static Profile CreateWindowsCapsProfile() => new()
    {
        Name = "Window [Default]",
        Kind = ProfileKind.Windows,
        CapsLock = { IsEnabled = true, IsRemapEnabled = true, RemapTarget = Key.B }
    };
}
