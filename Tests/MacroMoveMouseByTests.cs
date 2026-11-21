using System.Drawing;
using System.Reflection;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class MacroMoveMouseByTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(72, 0)]
    [InlineData(73, -1)]
    [InlineData(-7, 5)]
    [InlineData(1000, -333)]
    [InlineData(-100000, 99999)]
    public void RelativeChunks_AddUpExactlyAndStayWithinOneUpdateOfMotion(int dx, int dy)
    {
        var count = MacroCursorPath.RelativeChunkCount(dx, dy);
        long sumX = 0, sumY = 0;
        for (long i = 1; i <= count; i++)
        {
            var (x, y) = MacroCursorPath.RelativeChunk(dx, dy, count, i);
            Assert.InRange(Math.Abs(x), 0, 73);
            Assert.InRange(Math.Abs(y), 0, 73);
            sumX += x;
            sumY += y;
        }

        Assert.Equal((dx, dy), ((int)sumX, (int)sumY));
    }

    [Fact]
    public void MoveMouseBy_SendsOneRelativeTaggedMoveWithoutAbsoluteFlags()
    {
        var inputs = new List<NativeMethods.INPUT>();
        var sender = new WindowsInputSender(new NullLoggerService(), sent => { inputs.AddRange(sent); return (uint)sent.Length; },
            () => [new Rectangle(0, 0, 100, 100)]);

        Assert.True(sender.MoveMouseBy(-25, 40));
        Assert.False(sender.MoveMouseBy(5, 5, () => false));

        var input = Assert.Single(inputs);
        Assert.Equal(NativeMethods.MouseEventFlags.MOUSEEVENTF_MOVE, input.U.mi.dwFlags);
        Assert.Equal((-25, 40), (input.U.mi.dx, input.U.mi.dy));
        Assert.Equal(NativeMethods.INPUT_IGNORE, input.U.mi.dwExtraInfo);
    }

    [Fact]
    public void Playback_SendsSmoothRelativeChunksThatAddUpToTheOffset()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, Step(MacroStepKind.MoveMouseBy, 300, -40));

        Run(service);

        Assert.Null(service.GetMacroSession().FailureReason);
        Assert.Empty(sender.MouseMoves);
        Assert.Equal(MacroCursorPath.RelativeChunkCount(300, -40), sender.RelativeMouseMoves.Count);
        Assert.Equal((300, -40), (sender.RelativeMouseMoves.Sum(m => m.Dx), sender.RelativeMouseMoves.Sum(m => m.Dy)));
    }

    [Theory]
    [InlineData(MacroStepKind.MoveTo)]
    [InlineData(MacroStepKind.MoveCursorBy)]
    [InlineData(MacroStepKind.MoveMouseBy)]
    public void HeldButton_Stop_FailsAtTheStepWithoutMoving(MacroStepKind kind)
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, Step(kind, 50, 20));
        MacroMoveCursorByTests.FakeDesktop(service, sender);
        Physical(service).ObserveButton(MouseButton.Left, true);

        Run(service);

        Assert.Contains("Release physical mouse buttons", service.GetMacroSession().FailureReason);
        Assert.Empty(sender.MouseMoves);
        Assert.Empty(sender.RelativeMouseMoves);
    }

    [Theory]
    [InlineData(MacroStepKind.MoveTo)]
    [InlineData(MacroStepKind.MoveCursorBy)]
    [InlineData(MacroStepKind.MoveMouseBy)]
    public void HeldButton_Allow_MovesWhileTheButtonIsHeld(MacroStepKind kind)
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, Step(kind, 50, 20, MacroHeldButtonPolicy.Allow));
        MacroMoveCursorByTests.FakeDesktop(service, sender);
        Physical(service).ObserveButton(MouseButton.Right, true);

        Run(service);

        Assert.Null(service.GetMacroSession().FailureReason);
        Assert.Equal(Expected(kind), Moved(sender, kind));
    }

    [Theory]
    [InlineData(MacroStepKind.MoveTo)]
    [InlineData(MacroStepKind.MoveCursorBy)]
    [InlineData(MacroStepKind.MoveMouseBy)]
    public void HeldButton_Wait_PausesUntilReleaseThenFinishes(MacroStepKind kind)
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, Step(kind, 50, 20, MacroHeldButtonPolicy.Wait));
        MacroMoveCursorByTests.FakeDesktop(service, sender);
        var physical = Physical(service);
        physical.ObserveButton(MouseButton.Left, true);

        MacroPlaybackTests.Press(service, 0x75);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.WaitingForMouseRelease);
        Thread.Sleep(50);
        Assert.Empty(sender.MouseMoves);
        Assert.Empty(sender.RelativeMouseMoves);
        Assert.Equal(1, service.GetMacroSession().RowCount);

        physical.ObserveButton(MouseButton.Left, false);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { SessionId: 1, Mode: MacroSessionMode.Idle });

        Assert.Null(service.GetMacroSession().FailureReason);
        Assert.Equal(Expected(kind), Moved(sender, kind));
    }

    [Fact]
    public void HeldButton_Wait_PressedMidMove_PausesAndStillSendsTheWholeOffset()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, Step(MacroStepKind.MoveMouseBy, 3000, 0, MacroHeldButtonPolicy.Wait));
        var physical = Physical(service);

        MacroPlaybackTests.Press(service, 0x75);
        MacroPlaybackTests.WaitUntil(() => !sender.RelativeMouseMoves.IsEmpty);
        physical.ObserveButton(MouseButton.Middle, true);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.WaitingForMouseRelease);
        var sentWhilePaused = sender.RelativeMouseMoves.Count;
        Thread.Sleep(50);
        Assert.Equal(sentWhilePaused, sender.RelativeMouseMoves.Count);
        Assert.True(sentWhilePaused < MacroCursorPath.RelativeChunkCount(3000, 0));

        physical.ObserveButton(MouseButton.Middle, false);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { SessionId: 1, Mode: MacroSessionMode.Idle });

        Assert.Null(service.GetMacroSession().FailureReason);
        Assert.Equal(3000, sender.RelativeMouseMoves.Sum(m => m.Dx));
    }

    [Fact]
    public void HeldButton_Wait_F12StillStopsThePausedMacro()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, Step(MacroStepKind.MoveMouseBy, 50, 0, MacroHeldButtonPolicy.Wait));
        Physical(service).ObserveButton(MouseButton.Left, true);

        MacroPlaybackTests.Press(service, 0x75);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.WaitingForMouseRelease);
        MacroPlaybackTests.Press(service, 0x7B);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);

        Assert.Empty(sender.RelativeMouseMoves);
    }

    private static MacroStep Step(MacroStepKind kind, int x, int y, MacroHeldButtonPolicy held = MacroHeldButtonPolicy.Stop) =>
        new() { Kind = kind, X = x, Y = y, HeldButtons = held };

    // FakeDesktop starts the cursor at (10, 10).
    private static (int, int) Expected(MacroStepKind kind) => kind == MacroStepKind.MoveCursorBy ? (60, 30) : (50, 20);

    private static (int, int) Moved(RecordingInputSender sender, MacroStepKind kind) => kind == MacroStepKind.MoveMouseBy
        ? (sender.RelativeMouseMoves.Sum(m => m.Dx), sender.RelativeMouseMoves.Sum(m => m.Dy))
        : sender.MouseMoves.Last();

    private static void Run(InputHookService service)
    {
        MacroPlaybackTests.Press(service, 0x75);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { SessionId: 1, Mode: MacroSessionMode.Idle });
    }

    private static MacroPhysicalState Physical(InputHookService service)
    {
        var macros = typeof(InputHookService).GetField("_macros", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        return (MacroPhysicalState)typeof(MacroStateMachine).GetField("_physical", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(macros)!;
    }
}
