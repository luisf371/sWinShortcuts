using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

// Headless (no Application.Current): the window layer is skipped, and AppliedText shows what the badge would display.
public sealed class MacroStepOverlayServiceTests
{
    // Rows: 1 Key down A, 2 its hold, 3 Key up A, 4 Wait 200 ms, 5 Move 100, 200.
    private static readonly MacroDefinition Shown = new()
    {
        ShowPlayingSteps = true,
        Steps = [.. MacroPresses.Press(Key.A), new() { Kind = MacroStepKind.Wait, DurationMs = 200 }, new() { Kind = MacroStepKind.MoveTo, X = 100, Y = 200 }]
    };

    private static MacroSessionSnapshot Session(long id, MacroSessionMode mode, int row, MacroDefinition? definition) =>
        new(id, null, definition?.Id ?? Guid.Empty, mode, TimeSpan.Zero, row, null, definition);

    private static void Raise(FakeInputHookService input, MacroSessionSnapshot session)
    {
        input.MacroSession = session;
        input.RaiseMacroSessionChanged();
    }

    [Fact]
    public void Handler_EnqueueOnly_NeverAppliesInline()
    {
        var input = new FakeInputHookService();
        var pending = new Queue<Action>();
        using var service = new MacroStepOverlayService(new NullLoggerService(), input, pending.Enqueue);

        Raise(input, Session(1, MacroSessionMode.Playing, 1, Shown));

        Assert.Null(service.AppliedText);
        Assert.Single(pending)();
        Assert.Equal("1/5 · Key down A", service.AppliedText);
    }

    [Fact]
    public void Session_ShowsStepsOnlyForOptedInPlayback_AndHidesAtTheEnd()
    {
        var input = new FakeInputHookService();
        using var service = new MacroStepOverlayService(new NullLoggerService(), input, action => action());

        Raise(input, Session(1, MacroSessionMode.Playing, 2, Shown with { ShowPlayingSteps = false }));
        Assert.Null(service.AppliedText);

        Raise(input, Session(2, MacroSessionMode.WaitingForShortcutRelease, 0, Shown));
        Assert.Equal("Waiting for the shortcut to be released", service.AppliedText);
        Raise(input, Session(2, MacroSessionMode.Playing, 2, Shown));
        Assert.Equal("2/5 · Hold A (50 ms)", service.AppliedText);
        Raise(input, Session(2, MacroSessionMode.WaitingForPhysicalModifiers, 5, Shown));
        Assert.Equal("5/5 · Waiting for modifier keys to be released", service.AppliedText);
        Raise(input, Session(2, MacroSessionMode.Finishing, 5, Shown));
        Assert.Null(service.AppliedText);

        // Recording sessions carry no definition.
        Raise(input, Session(3, MacroSessionMode.Recording, 4, null));
        Assert.Null(service.AppliedText);
    }

    [Fact]
    public void SameSessionModeAndRow_DedupsApply_AndLoopWrapUpdatesText()
    {
        var input = new FakeInputHookService();
        using var service = new MacroStepOverlayService(new NullLoggerService(), input, action => action());

        Raise(input, Session(1, MacroSessionMode.Playing, 5, Shown));
        Raise(input, Session(1, MacroSessionMode.Playing, 5, Shown));
        Assert.Equal(1, service.AppliedCount);

        Raise(input, Session(1, MacroSessionMode.Playing, 1, Shown));
        Assert.Equal(2, service.AppliedCount);
        Assert.Equal("1/5 · Key down A", service.AppliedText);
    }

    [Fact]
    public void Dispose_FencesEventsAndQueuedApplies()
    {
        var input = new FakeInputHookService();
        var pending = new Queue<Action>();
        var service = new MacroStepOverlayService(new NullLoggerService(), input, pending.Enqueue);
        Raise(input, Session(1, MacroSessionMode.Playing, 1, Shown));

        service.Dispose();
        Raise(input, Session(1, MacroSessionMode.Playing, 2, Shown));

        Assert.Equal(0, input.MacroSessionSubscriberCount);
        Assert.Single(pending)();
        Assert.Equal(0, service.AppliedCount);
    }

    [Fact]
    public void DescribeSteps_EveryPlayableKind_UsesStepListWordingAndNamesPressHolds()
    {
        MacroStep[] steps =
        [
            .. MacroPresses.Press(Key.A),
            // A drag: the hold still counts when moves come between the Wait and the Up, as in the step list.
            new() { Kind = MacroStepKind.MouseDown, MouseButton = MouseButton.Left },
            new() { Kind = MacroStepKind.Wait, DurationMs = 80 },
            new() { Kind = MacroStepKind.MoveTo, X = 100, Y = 200 },
            new() { Kind = MacroStepKind.MouseUp, MouseButton = MouseButton.Left },
            new() { Kind = MacroStepKind.Wait, DurationMs = 200 },
            new() { Kind = MacroStepKind.MoveCursorBy, X = 40, Y = -5 },
            new() { Kind = MacroStepKind.MoveMouseBy, X = -3, Y = 0 },
            new() { Kind = MacroStepKind.SavePosition, Slot = 2 },
            new() { Kind = MacroStepKind.MoveToSaved, Slot = 2 },
            new() { Kind = MacroStepKind.MouseWheel, WheelDelta = 120 },
            new() { Kind = MacroStepKind.FreezeMouse },
            new() { Kind = MacroStepKind.UnfreezeMouse }
        ];

        Assert.Equal(
            [
                "Key down A", "Hold A (50 ms)", "Key up A", "Mouse down Left", "Hold Left (80 ms)", "Move 100, 200",
                "Mouse up Left", "Wait 200 ms", "Move +40, -5 px", "Move -3, 0 raw", "Save position #2", "Move to saved #2",
                "Mouse wheel +120 vertical", "Freeze mouse", "Unfreeze mouse"
            ],
            MacroStepOverlayService.DescribeSteps(steps));
    }

    [Theory]
    [InlineData(MacroSessionMode.Playing, 0, null)]
    [InlineData(MacroSessionMode.Playing, 4, null)]
    [InlineData(MacroSessionMode.WaitingForMouseRelease, 2, "2/3 · Waiting for mouse buttons to be released")]
    [InlineData(MacroSessionMode.WaitingForMouseRelease, 0, "Waiting for mouse buttons to be released")]
    [InlineData(MacroSessionMode.PreparingPlayback, 1, null)]
    [InlineData(MacroSessionMode.Idle, 1, null)]
    [InlineData(MacroSessionMode.Faulted, 1, null)]
    public void FormatSession_ModeAndRow_FormatsBadgeText(MacroSessionMode mode, int row, string? expected)
    {
        Assert.Equal(expected, MacroStepOverlayService.FormatSession(mode, row, ["A", "B", "C"]));
    }
}
