using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;
using static Tests.MacroPlaybackTests;

namespace Tests;

public sealed class MacroTogglePlaybackTests
{
    [Theory]
    [InlineData(Key.F6)]
    [InlineData(Key.LeftCtrl)]
    public void Toggle_HeldShortcutWaitsForRelease_ThenLoopsUntilNextFreshPress(Key shortcut)
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A, DurationMs = 1 });
        EnableToggle(service, profile, shortcut: shortcut);
        var vk = KeyInterop.VirtualKeyFromKey(shortcut);

        Assert.True(service.DispatchDecodedKeyboardEvent(vk, true, false));
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.WaitingForShortcutRelease);
        for (var i = 0; i < 5; i++) Assert.True(service.DispatchDecodedKeyboardEvent(vk, true, false));
        Assert.Empty(sender.Transitions);
        Assert.True(service.DispatchDecodedKeyboardEvent(vk, false, true));
        WaitUntil(() => sender.Transitions.Count(edge => edge.Key == Key.A && !edge.IsDown) >= 3);
        var session = service.GetMacroSession().SessionId;

        Assert.True(service.DispatchDecodedKeyboardEvent(vk, true, false));
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        var edges = sender.Transitions.Count;
        for (var i = 0; i < 5; i++) Assert.True(service.DispatchDecodedKeyboardEvent(vk, true, false));
        Assert.Equal(session, service.GetMacroSession().SessionId);
        Assert.Equal(MacroSessionMode.Idle, service.GetMacroSession().Mode);
        Assert.Equal(edges, sender.Transitions.Count);
        Assert.True(service.DispatchDecodedKeyboardEvent(vk, false, true));

        Press(service, vk);
        WaitUntil(() => service.GetMacroSession().SessionId > session && sender.Transitions.Count > edges + 2);
        Press(service, vk);
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.Equal(sender.Transitions.Count(edge => edge.IsDown), sender.Transitions.Count(edge => !edge.IsDown));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Toggle_SecondShortcutDuringLongWait_ReleasesHeldKeyAndMouseImmediately(bool control)
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyDown, Key = Key.A },
            new MacroStep { Kind = MacroStepKind.MouseDown, MouseButton = MouseButton.Left },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 30000 },
            new MacroStep { Kind = MacroStepKind.MouseUp, MouseButton = MouseButton.Left },
            new MacroStep { Kind = MacroStepKind.KeyUp, Key = Key.A });
        EnableToggle(service, profile, control);
        if (control) Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, true, false));
        Press(service, 0x75);
        if (control) Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, false, true));
        WaitUntil(() => sender.MouseTransitions.Any(edge => edge.IsDown));

        if (control) Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, true, false));
        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, true, false));
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);

        Assert.Single(sender.KeyReleases, edge => edge.Key == Key.A && edge.MacroRelease);
        Assert.Single(sender.MouseTransitions, edge => edge.Button == MouseButton.Left && !edge.IsDown && edge.MacroRelease);
        Assert.Contains("shortcut", service.GetMacroSession().FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, false, true));
        if (control) Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, false, true));
    }

    [Fact]
    public void Toggle_SecondChordBeforeModifiersRelease_CancelsPendingStart()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A });
        EnableToggle(service, profile, control: true);
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, true, false));
        Press(service, 0x75);
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.WaitingForShortcutRelease);

        Press(service, 0x75);
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, false, true));
        Assert.Empty(sender.Transitions);
    }

    [Fact]
    public void Toggle_DifferentMacroShortcutWhileBusy_IsConsumedWithoutStoppingOrStartingIt()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A, DurationMs = 1 });
        EnableToggle(service, profile);
        profile.Macros.Definitions = [profile.Macros.Definitions[0], new MacroDefinition
        {
            IsEnabled = true, ToggleMode = true, ShortcutKey = Key.F7,
            Steps = [new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.B }]
        }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        Press(service, 0x75);
        WaitUntil(() => sender.Transitions.Count(edge => !edge.IsDown) >= 2);
        var session = service.GetMacroSession().SessionId;
        var previous = sender.Transitions.Count;

        Press(service, 0x76);
        WaitUntil(() => sender.Transitions.Count > previous + 2);
        Assert.Equal(session, service.GetMacroSession().SessionId);
        Assert.DoesNotContain(sender.Transitions, edge => edge.Key == Key.B);
        Press(service, 0x75);
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Toggle_ExistingCancellationStopsRepeatedPlayback(int cancellation)
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A, DurationMs = 1 });
        EnableToggle(service, profile);
        Press(service, 0x75);
        WaitUntil(() => sender.Transitions.Count(edge => !edge.IsDown) >= 2);

        switch (cancellation)
        {
            case 0: Press(service, 0x7B); break;
            case 1:
                profile.Macros.Definitions = [profile.Macros.Definitions[0] with { ToggleMode = false }];
                service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
                break;
            case 2: service.SetForegroundIdentity((IntPtr)200, 43, "other.exe", 2); break;
            case 3:
                profile.Macros.IsEnabled = false;
                service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
                break;
        }

        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.Equal(sender.Transitions.Count(edge => edge.IsDown), sender.Transitions.Count(edge => !edge.IsDown));
        Assert.NotNull(service.GetMacroSession().FailureReason);
    }

    [Fact]
    public void Toggle_StopWhileNativeDownIsInFlight_WaitsForItsMatchingRelease()
    {
        var sender = new RecordingInputSender(blockFirstDown: true);
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyDown, Key = Key.A },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 30000 },
            new MacroStep { Kind = MacroStepKind.KeyUp, Key = Key.A });
        EnableToggle(service, profile);
        try
        {
            Press(service, 0x75);
            Assert.True(sender.DownEntered.Wait(TimeSpan.FromSeconds(2)));
            Press(service, 0x75);
            Assert.DoesNotContain(sender.Transitions, edge => !edge.IsDown);
            sender.ReleaseDown.Set();
            WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
            Assert.Single(sender.Transitions, edge => edge.IsDown);
            Assert.Single(sender.KeyReleases, edge => edge.Key == Key.A && edge.MacroRelease);
        }
        finally { sender.ReleaseDown.Set(); }
    }

    private static void EnableToggle(InputHookService service, Profile profile, bool control = false, Key shortcut = Key.F6)
    {
        profile.Macros.Definitions = [profile.Macros.Definitions[0] with
        {
            ToggleMode = true, ShortcutKey = shortcut,
            ShortcutModifiers = control ? ModifierKeys.Control : ModifierKeys.None
        }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
    }
}
