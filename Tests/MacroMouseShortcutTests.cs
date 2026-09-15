using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;
using static Tests.MacroPlaybackTests;

namespace Tests;

public sealed class MacroMouseShortcutTests
{
    [Fact]
    public void ConsumedRightButton_RecoveryDoesNotArmRightClickFeatures()
    {
        var held = false;
        var sender = new RecordingInputSender();
        var rightVk = NativeMethods.GetSystemMetrics(NativeMethods.SM_SWAPBUTTON) != 0 ? 1 : 2;
        using var service = new InputHookService(new NullLoggerService(), sender, System.Diagnostics.Stopwatch.GetTimestamp,
            vk => vk == rightVk && held, FakeAutoRunTransport.MatchingForeground());
        service.StartInputExecutorForTesting();
        service.AdvancedModeEnabled = true;
        var profile = new Profile { Name = "Game", Executable = "game.exe" };
        profile.Macros.IsEnabled = true;
        profile.Macros.Definitions = [new MacroDefinition { IsEnabled = true,
            Steps = [new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A }] }];
        service.ConfigureActiveProfileForTesting(profile, 1, false);
        Configure(service, profile, MouseButton.Right);
        held = true;
        Assert.True(Button(service, MouseButton.Right, true));
        Assert.False(Field<bool>(service, "_rightButtonPressed"));
        service.CancelMacroPlayback(profile);
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        typeof(InputHookService).GetMethod("RederivePhysicalModifierState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, null);
        Assert.False(Field<bool>(service, "_rightButtonPressed"));
        held = false;
        Assert.True(Button(service, MouseButton.Right, false));
        Assert.False(Field<bool>(service, "_rightButtonPressed"));
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Middle)]
    [InlineData(MouseButton.XButton1)]
    [InlineData(MouseButton.XButton2)]
    public void Chord_AllButtons_ConsumesPairAndWaitsForButtonAndModifiers(MouseButton button)
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.MouseDown, MouseButton = button },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 1 },
            new MacroStep { Kind = MacroStepKind.MouseUp, MouseButton = button });
        Configure(service, profile, button, ModifierKeys.Control | ModifierKeys.Alt);
        Assert.False(Button(service, button, true));
        Assert.False(Button(service, button, false));
        Modifiers(service, true);
        Assert.True(Button(service, button, true));
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.WaitingForShortcutRelease);
        Assert.True(Button(service, button, true));
        Assert.Empty(sender.MouseTransitions);
        Assert.True(Button(service, button, false));
        Assert.Equal(MacroSessionMode.WaitingForShortcutRelease, service.GetMacroSession().Mode);
        Modifiers(service, false);
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.Single(sender.MouseTransitions, edge => edge.IsDown);
        Assert.Single(sender.MouseTransitions, edge => !edge.IsDown);
        Assert.Null(service.GetMacroSession().FailureReason);
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Middle)]
    [InlineData(MouseButton.XButton1)]
    [InlineData(MouseButton.XButton2)]
    public void Toggle_StopWhileHoldingActivationButton_ReleasesSyntheticHoldAndConsumesStopPair(MouseButton button)
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.MouseDown, MouseButton = button },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 30000 },
            new MacroStep { Kind = MacroStepKind.MouseUp, MouseButton = button });
        Configure(service, profile, button, toggle: true);
        Assert.True(Button(service, button, true));
        Assert.True(Button(service, button, false));
        WaitUntil(() => sender.MouseTransitions.Any(edge => edge.IsDown));
        Assert.True(Button(service, button, true));
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.Single(sender.MouseTransitions, edge => !edge.IsDown && edge.MacroRelease);
        Assert.Contains("shortcut", service.GetMacroSession().FailureReason);
        var session = service.GetMacroSession().SessionId;
        Assert.True(Button(service, button, true));
        Assert.Equal(session, service.GetMacroSession().SessionId);
        Assert.True(Button(service, button, false));
    }

    [Fact]
    public void Toggle_MouseChord_RepeatsUntilSameChordAndIgnoresKeyboardMacroWhileBusy()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A, DurationMs = 1 });
        Configure(service, profile, MouseButton.Middle, ModifierKeys.Control | ModifierKeys.Alt, true);
        profile.Macros.Definitions = [profile.Macros.Definitions[0], new MacroDefinition
        {
            IsEnabled = true, ShortcutKey = Key.F7,
            Steps = [new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.B }]
        }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        Modifiers(service, true);
        Assert.True(Button(service, MouseButton.Middle, true));
        Assert.True(Button(service, MouseButton.Middle, false));
        Modifiers(service, false);
        WaitUntil(() => sender.Transitions.Count(edge => !edge.IsDown) >= 2);
        Press(service, 0x76);
        Assert.DoesNotContain(sender.Transitions, edge => edge.Key == Key.B);
        Modifiers(service, true);
        Assert.True(Button(service, MouseButton.Middle, true));
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.True(Button(service, MouseButton.Middle, false));
        Modifiers(service, false);
        Assert.Equal(sender.Transitions.Count(edge => edge.IsDown), sender.Transitions.Count(edge => !edge.IsDown));
    }

    [Fact]
    public void BusyMouseShortcut_MatchingOwnedButton_DoesNotTakeOverOrCancelOneShot()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.MouseDown, MouseButton = MouseButton.Middle },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 30000 },
            new MacroStep { Kind = MacroStepKind.MouseUp, MouseButton = MouseButton.Middle });
        profile.Macros.Definitions = [profile.Macros.Definitions[0], new MacroDefinition
        {
            IsEnabled = true, ShortcutMouseButton = MouseButton.Middle,
            Steps = [new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.B }]
        }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        Press(service, 0x75);
        WaitUntil(() => sender.MouseTransitions.Any(edge => edge.IsDown));
        Assert.True(Button(service, MouseButton.Middle, true));
        Assert.Null(service.GetMacroSession().FailureReason);
        Press(service, 0x7B);
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.Single(sender.MouseTransitions, edge => !edge.IsDown && edge.MacroRelease);
        Assert.True(Button(service, MouseButton.Middle, false));
        Assert.DoesNotContain(sender.Transitions, edge => edge.Key == Key.B);
    }

    [Fact]
    public void AltMouseConflict_KeepsOriginalBindingAndExcludesMacro()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile, new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A });
        profile.AltMouse.IsEnabled = true;
        profile.AltMouse.Bindings[MouseButton.Middle] = new MouseButtonBinding { TapKey = Key.B };
        Configure(service, profile, MouseButton.Middle, ModifierKeys.Control | ModifierKeys.Alt);
        Assert.Contains("Alt + Mouse", service.GetMacroShortcutError(profile, profile.Macros.Definitions[0].Id));
        Modifiers(service, true);
        Assert.True(Button(service, MouseButton.Middle, true));
        Assert.True(Button(service, MouseButton.Middle, false));
        WaitUntil(() => sender.Transitions.Any(edge => edge.Key == Key.B && !edge.IsDown));
        Modifiers(service, false);
        Assert.DoesNotContain(sender.Transitions, edge => edge.Key == Key.A);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_ConsumedMousePair_RetainsHeldPairAndClearsMissedRelease(bool swapped)
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile, new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A });
        Configure(service, profile, MouseButton.Left);
        Assert.True(Button(service, MouseButton.Left, true));
        service.CancelMacroPlayback(profile);
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        var physical = Field<MacroPhysicalState>(service, "_macroPhysical");
        physical.Seed(vk => vk == (swapped ? 2 : 1), swapped);
        Assert.False(physical.IsPhysicalMouseButtonDown(MouseButton.Left));
        Assert.True(physical.CaptureHeld().Buttons[(int)MouseButton.Left]);
        physical.ReconcileActivationPairs(vk => vk == (swapped ? 2 : 1), swapped);
        Assert.True(physical.CaptureHeld().Buttons[(int)MouseButton.Left]);
        physical.ReconcileActivationPairs(_ => false);
        Assert.False(physical.CaptureHeld().Buttons[(int)MouseButton.Left]);
        Assert.False(Button(service, MouseButton.Left, false));
    }

    [Fact]
    public void MouseCallback_ReplacementAfterCancellation_StillConsumesActivationUp()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile, new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A });
        Configure(service, profile, MouseButton.Middle);
        Assert.True(Button(service, MouseButton.Middle, true));
        service.CancelMacroPlayback(profile);
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        typeof(InputHookService).GetField("_mouseReplacementInProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, true);
        var data = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MSLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(new NativeMethods.MSLLHOOKSTRUCT(), data, false);
            var result = typeof(InputHookService).GetMethod("MouseCallback", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [0, (IntPtr)NativeMethods.WM_MBUTTONUP, data]);
            Assert.Equal((IntPtr)1, result);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    private static T Field<T>(InputHookService service, string name) => (T)typeof(InputHookService)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static void Configure(InputHookService service, Profile profile, MouseButton button,
        ModifierKeys modifiers = ModifierKeys.None, bool toggle = false)
    {
        profile.Macros.Definitions = [profile.Macros.Definitions[0] with
        {
            ShortcutKey = Key.None, ShortcutMouseButton = button, ShortcutModifiers = modifiers, ToggleMode = toggle
        }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros | ProfileChangeKind.AltMouse);
    }

    private static void Modifiers(InputHookService service, bool down)
    {
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, down, !down));
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA4, down, !down));
    }

    private static bool Button(InputHookService service, MouseButton button, bool down) => service.DispatchDecodedMouseEvent(button switch
    {
        MouseButton.Left => down ? NativeMethods.WM_LBUTTONDOWN : NativeMethods.WM_LBUTTONUP,
        MouseButton.Right => down ? NativeMethods.WM_RBUTTONDOWN : NativeMethods.WM_RBUTTONUP,
        MouseButton.Middle => down ? NativeMethods.WM_MBUTTONDOWN : NativeMethods.WM_MBUTTONUP,
        _ => down ? NativeMethods.WM_XBUTTONDOWN : NativeMethods.WM_XBUTTONUP
    }, button is MouseButton.XButton1 ? 1u << 16 : button is MouseButton.XButton2 ? 2u << 16 : 0);
}
