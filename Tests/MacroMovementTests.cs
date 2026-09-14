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

public sealed class MacroMovementTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void MouseCallback_Movement_RespectsPerMacroCancellationAndInjectedEvents(bool cancel, bool injected, bool duringMove)
    {
        using var cursorEntered = new ManualResetEventSlim();
        using var releaseCursor = new ManualResetEventSlim(!duringMove);
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.MouseClick, MouseButton = sWinShortcuts.Models.MouseButton.Left, X = 50, Y = 20, DurationMs = 30000 });
        profile.Macros.Definitions = [profile.Macros.Definitions[0] with { CancelOnMouseMovement = cancel }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        var macros = (MacroStateMachine)typeof(InputHookService).GetField("_macros", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        typeof(MacroStateMachine).GetField("_cursor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(macros,
            (Func<(int, int)>)(() =>
            {
                if (sender.MouseMoves.IsEmpty) return (0, 0);
                cursorEntered.Set();
                if (!releaseCursor.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Cursor probe was not released.");
                return sender.MouseMoves.Last();
            }));
        typeof(MacroStateMachine).GetField("_monitors", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(macros,
            (Func<Rectangle[]>)(() => [new Rectangle(-100, -100, 300, 300)]));
        try
        {
            MacroPlaybackTests.Press(service, 0x75);
            if (duringMove) Assert.True(cursorEntered.Wait(TimeSpan.FromSeconds(3)));
            else MacroPlaybackTests.WaitUntil(() => sender.MouseTransitions.Any(edge => edge.IsDown));
            var packet = new NativeMethods.MSLLHOOKSTRUCT
            {
                flags = injected ? NativeMethods.MouseLlFlags.LLMHF_INJECTED : 0,
                dwExtraInfo = injected ? NativeMethods.INPUT_IGNORE : IntPtr.Zero
            };
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MSLLHOOKSTRUCT>());
            try
            {
                Marshal.StructureToPtr(packet, pointer, false);
                typeof(InputHookService).GetMethod("MouseCallback", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [0, (IntPtr)NativeMethods.WM_MOUSEMOVE, pointer]);
            }
            finally { Marshal.FreeHGlobal(pointer); }
            releaseCursor.Set();
            if (cancel && !injected)
            {
                MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
                Assert.Contains("Physical mouse movement", service.GetMacroSession().FailureReason);
            }
            else
            {
                MacroPlaybackTests.WaitUntil(() => sender.MouseTransitions.Any(edge => edge.IsDown) || service.GetMacroSession().Mode == MacroSessionMode.Idle);
                Assert.NotEqual(MacroSessionMode.Idle, service.GetMacroSession().Mode);
                MacroPlaybackTests.Press(service, 0x7B);
                MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
            }
            Assert.Equal(sender.MouseTransitions.Count(edge => edge.IsDown), sender.MouseTransitions.Count(edge => !edge.IsDown));
        }
        finally { releaseCursor.Set(); }
    }

    [Theory]
    [InlineData(-400, 30, 400, 80, 12)]
    [InlineData(0, 0, 1, 1, 1)]
    [InlineData(100, 100, 100, 100, 0)]
    public void Curve_SignedEndpointsAndShortMoves_RemainVisibleAndLandExactly(int fromX, int fromY, int toX, int toY, int maximumUpdates)
    {
        var points = MacroCursorPath.Create(fromX, fromY, toX, toY, [new Rectangle(-1000, -1000, 2000, 2000)], 1);
        if (fromX == toX && fromY == toY) Assert.Empty(points);
        else Assert.Equal(new Point(toX, toY), points[^1]);
        var previous = new Point(fromX, fromY);
        // Faster travel needs fewer acknowledged updates; changing the delay divisor alone
        // would leave the minimum interval enforcing the old speed.
        Assert.InRange(points.Length, 0, maximumUpdates);
        foreach (var point in points)
        {
            Assert.InRange(MacroCursorPath.Distance(previous, point), 0, 74);
            previous = point;
        }
    }

    [Fact]
    public void Curve_MonitorGap_RejectsInsteadOfRedirecting()
    {
        Assert.Throws<InvalidOperationException>(() => MacroCursorPath.Create(-50, 10, 50, 10,
            [new Rectangle(-100, 0, 100, 100), new Rectangle(1, 0, 100, 100)], 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoordinateClick_ArrivalMustBeObservedBeforeButtonDown(bool clipped)
    {
        var transport = FakeAutoRunTransport.MatchingForeground();
        var runtime = new InputRuntimeState(transport);
        runtime.SetRunning(true);
        runtime.SetAdvancedMode(true);
        var profile = new Profile { Name = "Game", Executable = "game.exe" };
        profile.Macros.IsEnabled = true;
        profile.Macros.Definitions = [new MacroDefinition { IsEnabled = true, ShortcutKey = Key.F6,
            Steps = [new MacroStep { Kind = MacroStepKind.MouseClick, MouseButton = sWinShortcuts.Models.MouseButton.Left, X = 50, Y = 20 }] }];
        runtime.SetActiveProfile(profile, 1);
        runtime.SetForegroundIdentity((IntPtr)100, 42, "game.exe", 1);
        var sender = new RecordingInputSender();
        var physical = new MacroPhysicalState();
        var logger = new NullLoggerService { IsEnabled = clipped };
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService(), macroInputContext: physical);
        executor.Start();
        var autoRun = new AutoRunStateMachine(runtime, executor, new ThreadLocal<Random>(() => new Random(1)), new NullLoggerService(), transport);
        using var macros = new MacroStateMachine(runtime, executor, physical, autoRun, logger, () => { },
            action => { action(); return Task.CompletedTask; },
            cursor: () => clipped || sender.MouseMoves.IsEmpty ? (0, 0) : sender.MouseMoves.Last(),
            monitors: () => [new Rectangle(-100, -100, 300, 300)]);
        macros.Rebuild(profile, null, 0, 0, 0);
        Assert.True(macros.HandleKey(0x75, true, physical.KeyState(0x75)));
        Assert.True(macros.HandleKey(0x75, false, physical.KeyState(0x75)));
        MacroPlaybackTests.WaitUntil(() => macros.GetSession().SessionId == 1 && macros.GetSession().Mode == MacroSessionMode.Idle && !macros.IsBusy);
        Assert.NotEmpty(sender.MouseMoves);
        if (clipped)
        {
            Assert.Empty(sender.MouseTransitions);
            Assert.Contains("cursor", macros.GetSession().FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(logger.Messages, message => message.Contains("Cursor arrival timeout") &&
                message.Contains("row=1") && message.Contains("requested=(") && message.Contains("observed=(0,0)") && message.Contains("monitors="));
        }
        else
        {
            Assert.Equal((50, 20), sender.MouseMoves.Last());
            Assert.Collection(sender.MouseTransitions,
                down => Assert.True(down.IsDown), up => Assert.False(up.IsDown));
            Assert.Empty(logger.Messages);
        }
    }
}
