using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class MacroPlaybackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Playback_BusyShortcutStillHeld_DoesNotBlockSameKeyOutput(bool control)
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 250 },
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.F6 });
        if (control)
        {
            profile.Macros.Definitions = [profile.Macros.Definitions[0] with { ShortcutModifiers = ModifierKeys.Control }];
            service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
            service.DispatchDecodedKeyboardEvent(0xA2, true, false);
        }
        Press(service, 0x75);
        if (control) service.DispatchDecodedKeyboardEvent(0xA2, false, true);
        WaitUntil(() => sender.Transitions.Any(x => x.Key == Key.A && !x.IsDown));
        var id = service.GetMacroSession().SessionId;
        if (control) service.DispatchDecodedKeyboardEvent(0xA2, true, false);
        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, true, false));
        if (control) service.DispatchDecodedKeyboardEvent(0xA2, false, true);
        WaitUntil(() => service.GetMacroSession().SessionId == id && service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.Null(service.GetMacroSession().FailureReason);
        Assert.Single(sender.Transitions, x => x.Key == Key.F6 && x.IsDown);
        Assert.True(service.DispatchDecodedKeyboardEvent(0x75, false, true));
        Assert.Single(sender.Transitions, x => x.Key == Key.A && x.IsDown);
    }

    [Fact]
    public async Task Retirement_NativeDownInFlight_AwaitsAccountingAndRelease()
    {
        var sender = new RecordingInputSender(blockFirstDown: true);
        using var service = Create(sender, out _,
            new MacroStep { Kind = MacroStepKind.KeyDown, Key = Key.A },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 30000 },
            new MacroStep { Kind = MacroStepKind.KeyUp, Key = Key.A });
        Press(service, 0x75);
        Assert.True(sender.DownEntered.Wait(TimeSpan.FromSeconds(2)));
        var retirement = service.RetireMacroSessionAsync();
        Assert.False(retirement.IsCompleted);
        sender.ReleaseDown.Set();
        Assert.True(await retirement.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Single(sender.KeyReleases, x => x.Key == Key.A && x.MacroRelease);
    }

    [Fact]
    public async Task Playback_LongWait_DoesNotBlockUnrelatedExecutorRelease()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out _,
            new MacroStep { Kind = MacroStepKind.KeyDown, Key = Key.A },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 30000 },
            new MacroStep { Kind = MacroStepKind.KeyUp, Key = Key.A });
        Press(service, 0x75);
        WaitUntil(() => sender.Transitions.Any(x => x.Key == Key.A && x.IsDown));
        var executor = Executor(service);
        var down = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(executor.Enqueue(new InputCommand(Key.B, true, HoldOwner: InputHoldOwner.Combined, Completion: down)));
        Assert.True(await down.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        var up = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(executor.Enqueue(new InputCommand(Key.B, false, HoldOwner: InputHoldOwner.Combined, Completion: up)));
        Assert.True(await up.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.DoesNotContain(sender.Transitions, x => x.Key == Key.A && !x.IsDown);
        Press(service, 0x7B);
        WaitUntil(() => sender.Transitions.Any(x => x.Key == Key.A && !x.IsDown));
    }

    [Fact]
    public void Playback_BusyControlChord_DefersNewOutputAndDoesNotRestart()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyDown, Key = Key.A },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 150 },
            new MacroStep { Kind = MacroStepKind.KeyUp, Key = Key.A },
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.B });
        profile.Macros.Definitions = [profile.Macros.Definitions[0] with { ShortcutModifiers = ModifierKeys.Control }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, true, false));
        Press(service, 0x75);
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, false, true));
        WaitUntil(() => sender.Transitions.Any(x => x.Key == Key.A && x.IsDown));
        var id = service.GetMacroSession().SessionId;
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, true, false));
        Press(service, 0x75);
        WaitUntil(() => sender.Transitions.Any(x => x.Key == Key.A && !x.IsDown));
        Assert.DoesNotContain(sender.Transitions, x => x.Key == Key.B && x.IsDown);
        Assert.Equal(id, service.GetMacroSession().SessionId);
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, false, true));
        WaitUntil(() => sender.Transitions.Any(x => x.Key == Key.B && !x.IsDown));
        Assert.Single(sender.Transitions, x => x.Key == Key.A && x.IsDown);
    }

    [Fact]
    public void Playback_PhysicalModifierOverlap_RestoresIntendedModifierBeforeNextKey()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out _,
            new MacroStep { Kind = MacroStepKind.KeyDown, Key = Key.LeftCtrl },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 150 },
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.S },
            new MacroStep { Kind = MacroStepKind.KeyUp, Key = Key.LeftCtrl });
        Press(service, 0x75);
        WaitUntil(() => sender.Transitions.Any(x => x.Key == Key.LeftCtrl && x.IsDown));
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, true, false));
        WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.WaitingForPhysicalModifiers);
        Assert.DoesNotContain(sender.Transitions, x => x.Key == Key.S);
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA2, false, true));
        WaitUntil(() => sender.Transitions.Any(x => x.Key == Key.S && !x.IsDown));
        var downs = sender.Transitions.Where(x => x.IsDown).Select(x => x.Key).ToArray();
        Assert.Equal(new[] { Key.LeftCtrl, Key.LeftCtrl, Key.S }, downs);
    }

    [Fact]
    public void Playback_ForegroundChanges_ReleasesAndDoesNotResume()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out _,
            new MacroStep { Kind = MacroStepKind.KeyDown, Key = Key.A },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 30000 },
            new MacroStep { Kind = MacroStepKind.KeyUp, Key = Key.A });
        Press(service, 0x75);
        WaitUntil(() => sender.Transitions.Any(x => x.Key == Key.A && x.IsDown));
        service.SetForegroundIdentity((IntPtr)200, 43, "other.exe", 2);
        WaitUntil(() => sender.Transitions.Any(x => x.Key == Key.A && !x.IsDown));
        Assert.Single(sender.Transitions, x => x.Key == Key.A && x.IsDown);
    }

    [Fact]
    public void Shortcut_ToggleReassignmentAndRollback_UpdatesActivationAndConflict()
    {
        var sender = new RecordingInputSender();
        using var service = Create(sender, out var profile, new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A });
        var id = profile.Macros.Definitions[0].Id;
        service.SetColorToggleKey(Key.F6);
        Assert.NotNull(service.GetMacroShortcutError(profile, id));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x75, true, false));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x75, false, true));
        Assert.Empty(sender.Transitions);
        service.SetColorToggleKey(null);
        Assert.Null(service.GetMacroShortcutError(profile, id));
        Press(service, 0x75);
        WaitUntil(() => sender.Transitions.Any(x => x.Key == Key.A && !x.IsDown));
    }

    internal static InputHookService Create(RecordingInputSender sender, out Profile profile, params MacroStep[] steps)
    {
        var service = InputHookServiceTestExtensions.CreateWithFakeForeground(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();
        service.AdvancedModeEnabled = true;
        profile = new Profile { Name = "Game", Executable = "game.exe" };
        profile.Macros.IsEnabled = true;
        profile.Macros.Definitions = [new MacroDefinition { IsEnabled = true, ShortcutKey = Key.F6, Steps = steps }];
        service.ConfigureActiveProfileForTesting(profile, 1, false);
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        return service;
    }

    internal static void Press(InputHookService service, int vk)
    {
        Assert.True(service.DispatchDecodedKeyboardEvent(vk, true, false));
        Assert.True(service.DispatchDecodedKeyboardEvent(vk, false, true));
    }

    internal static void WaitUntil(Func<bool> condition)
    {
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(3)), "Macro did not reach the expected state.");
    }

    private static InputExecutor Executor(InputHookService service) => (InputExecutor)typeof(InputHookService)
        .GetField("_inputExecutor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
}
