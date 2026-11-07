using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class MacroMouseFreezeTests
{
    private static readonly MacroStep Freeze = new() { Kind = MacroStepKind.FreezeMouse };
    private static readonly MacroStep Unfreeze = new() { Kind = MacroStepKind.UnfreezeMouse };
    private static readonly IntPtr Blocked = (IntPtr)1;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MouseCallback_WhileFrozen_BlocksOnlyPhysicalMovesUntilF12(bool injected)
    {
        using var service = MacroPlaybackTests.Create(new RecordingInputSender(), out _, Freeze, Wait(30000));
        MacroPlaybackTests.Press(service, 0x75);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { Mode: MacroSessionMode.Playing, RowCount: 2 });

        Assert.Equal(injected ? IntPtr.Zero : Blocked, Move(service, injected));
        Stop(service);
        Assert.Equal(IntPtr.Zero, Move(service));
    }

    [Fact]
    public void MouseCallback_AfterUnfreezeStep_PassesPhysicalMoves()
    {
        using var service = MacroPlaybackTests.Create(new RecordingInputSender(), out _, Freeze, Unfreeze, Wait(30000));
        MacroPlaybackTests.Press(service, 0x75);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { Mode: MacroSessionMode.Playing, RowCount: 3 });

        Assert.Equal(IntPtr.Zero, Move(service));
        Stop(service);
    }

    [Theory]
    [InlineData("complete", null)]
    [InlineData("F12", "F12")]
    [InlineData("toggle", "macro shortcut")]
    [InlineData("focus", "lost focus")]
    [InlineData("failure", "outside the current monitors")]
    public void EveryEndPath_LiftsTheFreeze(string path, string? reason)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile, Freeze, MoveTo(path == "failure" ? 9999 : 50, 20));
        profile.Macros.Definitions = [profile.Macros.Definitions[0] with { ToggleMode = path == "toggle" }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        GateCursor(service, sender, entered, release);
        try
        {
            MacroPlaybackTests.Press(service, 0x75);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.Equal(Blocked, Move(service));
            switch (path)
            {
                case "F12": MacroPlaybackTests.Press(service, 0x7B); break;
                case "toggle": MacroPlaybackTests.Press(service, 0x75); break;
                case "focus": service.DeactivateProfile(1); break;
            }
            release.Set();
            MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);

            Assert.Equal(IntPtr.Zero, Move(service));
            if (reason is null) Assert.Null(service.GetMacroSession().FailureReason);
            else Assert.Contains(reason, service.GetMacroSession().FailureReason);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void NextSession_AfterAFreezeWithoutUnfreeze_StartsUnfrozen()
    {
        using var service = MacroPlaybackTests.Create(new RecordingInputSender(), out var profile, Freeze, Wait(50));
        profile.Macros.Definitions = [profile.Macros.Definitions[0],
            new MacroDefinition { IsEnabled = true, ShortcutKey = Key.F7, Steps = [Wait(30000)] }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        MacroPlaybackTests.Press(service, 0x75);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { SessionId: 1, Mode: MacroSessionMode.Idle } && !Macros(service).IsBusy);

        MacroPlaybackTests.Press(service, 0x76);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { SessionId: 2, Mode: MacroSessionMode.Playing });
        Assert.Equal(IntPtr.Zero, Move(service));
        Stop(service);
    }

    [Fact]
    public void Cancel_LiftsTheFreezeBeforeTheBlockedWorkerFinishes()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, Freeze, MoveTo(50, 20));
        GateCursor(service, sender, entered, release);
        try
        {
            MacroPlaybackTests.Press(service, 0x75);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.Equal(Blocked, Move(service));

            MacroPlaybackTests.Press(service, 0x7B);
            // The worker is still inside the cursor probe, so its finally has not cleared the freeze yet.
            Assert.True(Macros(service).IsBusy);
            Assert.Equal(IntPtr.Zero, Move(service));
            release.Set();
            MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void MouseCallback_FreezeWinsOverCancelOnMouseMovement_UntilUnfreeze()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile, Freeze, MoveTo(50, 20), Unfreeze, Wait(30000));
        profile.Macros.Definitions = [profile.Macros.Definitions[0] with { CancelOnMouseMovement = true }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        GateCursor(service, sender, entered, release);
        try
        {
            MacroPlaybackTests.Press(service, 0x75);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            Assert.Equal(Blocked, Move(service));
            release.Set();
            MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { Mode: MacroSessionMode.Playing, RowCount: 4 });
            Assert.Null(service.GetMacroSession().FailureReason);

            Assert.Equal(IntPtr.Zero, Move(service));
            MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
            Assert.Contains("Physical mouse movement", service.GetMacroSession().FailureReason);
        }
        finally { release.Set(); }
    }

    private static MacroStep Wait(int milliseconds) => new() { Kind = MacroStepKind.Wait, DurationMs = milliseconds };

    private static MacroStep MoveTo(int x, int y) => new() { Kind = MacroStepKind.MoveTo, X = x, Y = y };

    private static void Stop(InputHookService service)
    {
        MacroPlaybackTests.Press(service, 0x7B);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
    }

    private static MacroStateMachine Macros(InputHookService service) => (MacroStateMachine)typeof(InputHookService)
        .GetField("_macros", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    // Holds the worker at the start of its first Move to until released, then reports the last injected move.
    private static void GateCursor(InputHookService service, RecordingInputSender sender, ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        var macros = Macros(service);
        typeof(MacroStateMachine).GetField("_cursor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(macros,
            (Func<(int, int)>)(() =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Cursor probe was not released.");
                return sender.MouseMoves.IsEmpty ? (0, 0) : sender.MouseMoves.Last();
            }));
        typeof(MacroStateMachine).GetField("_monitors", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(macros,
            (Func<Rectangle[]>)(() => [new Rectangle(-100, -100, 300, 300)]));
    }

    // Returns 1 when the hook blocks the move, 0 when it passes it on.
    private static IntPtr Move(InputHookService service, bool injected = false)
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MSLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(new NativeMethods.MSLLHOOKSTRUCT
            {
                flags = injected ? NativeMethods.MouseLlFlags.LLMHF_INJECTED : 0,
                dwExtraInfo = injected ? NativeMethods.INPUT_IGNORE : IntPtr.Zero
            }, pointer, false);
            return (IntPtr)typeof(InputHookService).GetMethod("MouseCallback", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [0, (IntPtr)NativeMethods.WM_MOUSEMOVE, pointer])!;
        }
        finally { Marshal.FreeHGlobal(pointer); }
    }
}
