using System.Drawing;
using System.Reflection;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class MacroSavedPositionTests
{
    [Fact]
    public void SaveThenMoveToSaved_AfterOtherMoves_ReturnsToTheSavedPoint()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, Move(40, 30), Save(1), Move(60, 50), Return(1));
        MacroMoveCursorByTests.FakeDesktop(service, sender);

        Run(service);

        Assert.Null(service.GetMacroSession().FailureReason);
        Assert.Contains((60, 50), sender.MouseMoves);
        Assert.Equal((40, 30), sender.MouseMoves.Last());
    }

    [Fact]
    public void TwoSlots_Nested_EachReturnsToItsOwnPoint()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, Save(1), Move(90, 10), Save(8), Move(90, 80), Return(8), Return(1));
        MacroMoveCursorByTests.FakeDesktop(service, sender);

        Run(service);

        Assert.Null(service.GetMacroSession().FailureReason);
        var moves = sender.MouseMoves.ToArray();
        var farthestMove = Array.IndexOf(moves, (90, 80));
        Assert.True(farthestMove >= 0);
        Assert.Contains((90, 10), moves.Skip(farthestMove + 1));
        Assert.Equal((10, 10), moves.Last());
    }

    [Fact]
    public void SaveSameSlotTwice_MoveToSaved_UsesTheNewestSave()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, Save(1), Move(50, 40), Save(1), Move(90, 80), Return(1));
        MacroMoveCursorByTests.FakeDesktop(service, sender);

        Run(service);

        Assert.Null(service.GetMacroSession().FailureReason);
        Assert.Contains((90, 80), sender.MouseMoves);
        Assert.Equal((50, 40), sender.MouseMoves.Last());
    }

    [Fact]
    public void MoveToSaved_PointNoLongerOnAMonitor_FailsLikeMoveTo()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, [Save(1), Move(60, 50),
            .. MacroPresses.Press(Key.A), Return(1)]);
        MacroMoveCursorByTests.FakeDesktop(service, sender);
        var monitorRemoved = false;
        sender.KeyResult = (_, _, _) => monitorRemoved = true;
        typeof(MacroStateMachine).GetField("_monitors", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(StateMachine(service),
            (Func<Rectangle[]>)(() => monitorRemoved
                ? [new Rectangle(20, 0, 180, 200)]
                : [new Rectangle(0, 0, 20, 200), new Rectangle(20, 0, 180, 200)]));

        Run(service);

        Assert.Contains("outside the current monitors", service.GetMacroSession().FailureReason);
        Assert.Equal((60, 50), sender.MouseMoves.Last());
    }

    [Fact]
    public void MoveToSaved_HeldButtonWait_WaitsThenMoves()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, Save(1),
            Move(50, 40) with { HeldButtons = MacroHeldButtonPolicy.Allow }, Return(1) with { HeldButtons = MacroHeldButtonPolicy.Wait });
        MacroMoveCursorByTests.FakeDesktop(service, sender);
        var physical = (MacroPhysicalState)typeof(MacroStateMachine).GetField("_physical", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(StateMachine(service))!;
        physical.ObserveButton(MouseButton.Left, true);

        MacroPlaybackTests.Press(service, 0x75);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { SessionId: 1, Mode: MacroSessionMode.WaitingForMouseRelease });

        Assert.Equal(3, service.GetMacroSession().RowCount);
        Assert.Equal((50, 40), sender.MouseMoves.Last());

        physical.ObserveButton(MouseButton.Left, false);
        WaitForCompletion(service);

        Assert.Null(service.GetMacroSession().FailureReason);
        Assert.Equal((10, 10), sender.MouseMoves.Last());
    }

    [Fact]
    public void SavePosition_SendsNoInput()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, Save(1));
        MacroMoveCursorByTests.FakeDesktop(service, sender);

        Run(service);

        Assert.Null(service.GetMacroSession().FailureReason);
        Assert.Empty(sender.Transitions);
        Assert.Empty(sender.MouseTransitions);
        Assert.Empty(sender.MouseMoves);
        Assert.Empty(sender.RelativeMouseMoves);
        Assert.Empty(sender.MouseWheels);
    }

    private static MacroStateMachine StateMachine(InputHookService service) =>
        (MacroStateMachine)typeof(InputHookService).GetField("_macros", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static MacroStep Move(int x, int y) => new() { Kind = MacroStepKind.MoveTo, X = x, Y = y };
    private static MacroStep Save(int slot) => new() { Kind = MacroStepKind.SavePosition, Slot = slot };
    private static MacroStep Return(int slot) => new() { Kind = MacroStepKind.MoveToSaved, Slot = slot };

    private static void Run(InputHookService service)
    {
        MacroPlaybackTests.Press(service, 0x75);
        WaitForCompletion(service);
    }

    private static void WaitForCompletion(InputHookService service) =>
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { SessionId: 1, Mode: MacroSessionMode.Idle });
}
