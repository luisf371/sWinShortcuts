using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;
using static Tests.MacroPlaybackTests;

namespace Tests;

public sealed class MacroStartOnPressTests
{
    [Theory]
    [InlineData(Key.F6)]
    [InlineData(Key.LeftCtrl)]
    public void StartOnPress_HeldShortcut_PlaysBeforeReleaseWithoutRestarting(Key shortcut)
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A, DurationMs = 1 });
        Configure(service, profile, shortcut: shortcut);
        var vk = KeyInterop.VirtualKeyFromKey(shortcut);

        Assert.True(service.DispatchDecodedKeyboardEvent(vk, true, false));
        WaitUntil(() => sender.Transitions.Any(edge => edge.Key == Key.A && !edge.IsDown) &&
            service.GetMacroSession().Mode == MacroSessionMode.Idle);
        var session = service.GetMacroSession().SessionId;

        // Typematic repeats and the final UP of the consumed shortcut neither restart nor leak.
        for (var i = 0; i < 5; i++) Assert.True(service.DispatchDecodedKeyboardEvent(vk, true, false));
        Assert.True(service.DispatchDecodedKeyboardEvent(vk, false, true));
        Assert.Equal(session, service.GetMacroSession().SessionId);
        Assert.Null(service.GetMacroSession().FailureReason);
        Assert.Equal(2, sender.Transitions.Count);
        Assert.DoesNotContain(sender.Transitions, edge => edge.Key == shortcut);
    }

    [Theory]
    [InlineData(0xA2, ModifierKeys.Control)]
    [InlineData(0xA0, ModifierKeys.Shift)]
    public void StartOnPress_ShiftOrCtrlChord_PlaysThroughHeldModifier(int modifierVk, ModifierKeys modifier)
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A, DurationMs = 1 });
        Configure(service, profile, modifiers: modifier);

        Assert.False(service.DispatchDecodedKeyboardEvent(modifierVk, true, false));
        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, true, false));
        // Repeats of the held modifier pass through without pausing the run.
        for (var i = 0; i < 3; i++) Assert.False(service.DispatchDecodedKeyboardEvent(modifierVk, true, false));
        WaitUntil(() => sender.Transitions.Any(edge => edge.Key == Key.A && !edge.IsDown) &&
            service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, false, true));
        Assert.False(service.DispatchDecodedKeyboardEvent(modifierVk, false, true));
        Assert.Null(service.GetMacroSession().FailureReason);
        Assert.Equal(2, sender.Transitions.Count);
    }

    [Fact]
    public void StartOnPress_StepPressesTheHeldModifier_IsRefusedWithoutSending()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyDown, Key = Key.LeftCtrl },
            new MacroStep { Kind = MacroStepKind.KeyUp, Key = Key.LeftCtrl });
        Configure(service, profile, modifiers: ModifierKeys.Control);

        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, true, false));
        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, true, false));
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle && service.GetMacroSession().FailureReason is not null);
        Assert.Contains("refused", service.GetMacroSession().FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(sender.Transitions);
        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, false, true));
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, false, true));
    }

    [Fact]
    public void StartOnPress_AltChord_StillWaitsForAltRelease()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A, DurationMs = 1 });
        Configure(service, profile, modifiers: ModifierKeys.Alt);

        Assert.False(service.DispatchDecodedKeyboardEvent(0xA4, true, false));
        service.SetWindowsKeyDownForTesting(0xA4, true);
        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, true, false));
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.WaitingForPhysicalModifiers);
        Assert.Empty(sender.Transitions);

        // F6 is still held: only Alt gates output, so A is never sent as Alt+A.
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA4, false, true));
        service.SetWindowsKeyDownForTesting(0xA4, false);
        WaitUntil(() => sender.Transitions.Any(edge => edge.Key == Key.A && !edge.IsDown) &&
            service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, false, true));
        Assert.Null(service.GetMacroSession().FailureReason);
    }

    [Fact]
    public void StartOnPress_FreshModifierPressDuringRun_StillPausesOutput()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A, DurationMs = 1 },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 1000 },
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.B, DurationMs = 1 });
        Configure(service, profile, modifiers: ModifierKeys.Control);

        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, true, false));
        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, true, false));
        WaitUntil(() => sender.Transitions.Any(edge => edge.Key == Key.A && !edge.IsDown));
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, false, true));
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, true, false));

        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.WaitingForPhysicalModifiers);
        Assert.DoesNotContain(sender.Transitions, edge => edge.Key == Key.B);
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, false, true));
        WaitUntil(() => sender.Transitions.Any(edge => edge.Key == Key.B && !edge.IsDown) &&
            service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, false, true));
        Assert.Null(service.GetMacroSession().FailureReason);
    }

    [Fact]
    public void StartOnPress_PlayThroughEndsWithSession_LaterReleaseMacroWaitsForHeldModifier()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A, DurationMs = 1 });
        Configure(service, profile, modifiers: ModifierKeys.Control);
        profile.Macros.Definitions = [profile.Macros.Definitions[0], new MacroDefinition
        {
            IsEnabled = true, ShortcutKey = Key.F7, ShortcutModifiers = ModifierKeys.Control,
            Steps = [new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.B, DurationMs = 1 }]
        }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);

        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, true, false));
        Press(service, 0x75);
        WaitUntil(() => sender.Transitions.Any(edge => edge.Key == Key.A && !edge.IsDown) &&
            service.GetMacroSession().Mode == MacroSessionMode.Idle);

        // Ctrl is still the same physical hold, but the on-release macro must not send B as Ctrl+B.
        Press(service, 0x76);
        Assert.False(SpinWait.SpinUntil(() => sender.Transitions.Any(edge => edge.Key == Key.B), 200));
        Assert.Equal(MacroSessionMode.WaitingForShortcutRelease, service.GetMacroSession().Mode);
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, false, true));
        WaitUntil(() => sender.Transitions.Any(edge => edge.Key == Key.B && !edge.IsDown));
    }

    [Fact]
    public void StartOnPress_Toggle_LoopsWhileHeldAndNextFreshPressStops()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A, DurationMs = 1 });
        Configure(service, profile, toggle: true);

        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, true, false));
        WaitUntil(() => sender.Transitions.Count(edge => edge.Key == Key.A && !edge.IsDown) >= 2);
        var session = service.GetMacroSession().SessionId;
        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, false, true));
        Assert.NotEqual(MacroSessionMode.Idle, service.GetMacroSession().Mode);

        Press(service, 0x75);
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        // The stop can land mid-send, whose refusal then names the failure, so check the stop rather than its text.
        Assert.Equal(session, service.GetMacroSession().SessionId);
        Assert.Equal(sender.Transitions.Count(edge => edge.IsDown), sender.Transitions.Count(edge => !edge.IsDown));
    }

    [Fact]
    public void StartOnPress_HeldMouseShortcut_CanClickItsOwnButtonBeforeRelease()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.MouseDown, MouseButton = MouseButton.Middle },
            new MacroStep { Kind = MacroStepKind.MouseUp, MouseButton = MouseButton.Middle });
        Configure(service, profile, button: MouseButton.Middle);

        Assert.True(service.DispatchDecodedMouseEvent(NativeMethods.WM_MBUTTONDOWN, 0));
        WaitUntil(() => sender.MouseTransitions.Any(edge => edge.Button == MouseButton.Middle && !edge.IsDown) &&
            service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.True(service.DispatchDecodedMouseEvent(NativeMethods.WM_MBUTTONUP, 0));
        Assert.Null(service.GetMacroSession().FailureReason);
        Assert.Equal(2, sender.MouseTransitions.Count);
    }

    private static void Configure(InputHookService service, Profile profile, Key shortcut = Key.F6,
        MouseButton? button = null, ModifierKeys modifiers = ModifierKeys.None, bool toggle = false)
    {
        profile.Macros.Definitions = [profile.Macros.Definitions[0] with
        {
            StartOnPress = true, ToggleMode = toggle,
            ShortcutKey = button is null ? shortcut : Key.None, ShortcutMouseButton = button,
            ShortcutModifiers = modifiers
        }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
    }
}
