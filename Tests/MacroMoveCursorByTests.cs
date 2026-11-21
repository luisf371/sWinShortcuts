using System.Drawing;
using System.Reflection;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class MacroMoveCursorByTests
{
    [Fact]
    public void Playback_MovesRelativeToWhereTheCursorIsWhenEachStepRuns()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, By(30, -15), By(-50, 40));
        FakeDesktop(service, sender);

        MacroPlaybackTests.Press(service, 0x75);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { SessionId: 1, Mode: MacroSessionMode.Idle });

        Assert.Null(service.GetMacroSession().FailureReason);
        Assert.Contains((40, -5), sender.MouseMoves);
        Assert.Equal((-10, 35), sender.MouseMoves.Last());
    }

    [Theory]
    [InlineData(9999, 0)]
    [InlineData(0, -9999)]
    [InlineData(int.MaxValue, 0)]
    [InlineData(0, int.MinValue)]
    public void Playback_OffsetLandingOffScreen_FailsWithoutMoving(int dx, int dy)
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out _, By(dx, dy));
        FakeDesktop(service, sender);

        MacroPlaybackTests.Press(service, 0x75);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { SessionId: 1, Mode: MacroSessionMode.Idle });

        Assert.Contains("outside the current monitors", service.GetMacroSession().FailureReason);
        Assert.Empty(sender.MouseMoves);
    }

    private static MacroStep By(int x, int y) => new() { Kind = MacroStepKind.MoveCursorBy, X = x, Y = y };

    // The cursor starts at (10, 10), then follows the macro's injected moves, on one 300x300 monitor.
    internal static void FakeDesktop(InputHookService service, RecordingInputSender sender)
    {
        var macros = typeof(InputHookService).GetField("_macros", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        typeof(MacroStateMachine).GetField("_cursor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(macros,
            (Func<(int, int)>)(() => sender.MouseMoves.IsEmpty ? (10, 10) : sender.MouseMoves.Last()));
        typeof(MacroStateMachine).GetField("_monitors", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(macros,
            (Func<Rectangle[]>)(() => [new Rectangle(-100, -100, 300, 300)]));
    }
}
