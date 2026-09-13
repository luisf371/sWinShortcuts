using System.Drawing;
using System.Reflection;
using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class MacroMovementTests
{
    [Theory]
    [InlineData(-400, 30, 400, 80)]
    [InlineData(0, 0, 1, 1)]
    [InlineData(100, 100, 100, 100)]
    public void Curve_SignedEndpointsAndShortMoves_RemainVisibleAndLandExactly(int fromX, int fromY, int toX, int toY)
    {
        var points = MacroCursorPath.Create(fromX, fromY, toX, toY, [new Rectangle(-1000, -1000, 2000, 2000)], 1);
        if (fromX == toX && fromY == toY) Assert.Empty(points);
        else Assert.Equal(new Point(toX, toY), points[^1]);
        var previous = new Point(fromX, fromY);
        foreach (var point in points)
        {
            Assert.InRange(MacroCursorPath.Distance(previous, point), 0, 26);
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
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService(), macroInputContext: physical);
        executor.Start();
        var autoRun = new AutoRunStateMachine(runtime, executor, new ThreadLocal<Random>(() => new Random(1)), new NullLoggerService(), transport);
        using var macros = new MacroStateMachine(runtime, executor, physical, autoRun, new NullLoggerService(), () => { },
            action => { action(); return Task.CompletedTask; },
            cursor: () => clipped || sender.MouseMoves.IsEmpty ? (0, 0) : sender.MouseMoves.Last(),
            monitors: () => [new Rectangle(-100, -100, 300, 300)]);
        macros.Rebuild(profile, null, 0, 0, 0);
        Assert.True(macros.HandleKey(0x75, true, 0));
        Assert.True(macros.HandleKey(0x75, false, 1));
        MacroPlaybackTests.WaitUntil(() => macros.GetSession().SessionId == 1 && macros.GetSession().Mode == MacroSessionMode.Idle);
        Assert.NotEmpty(sender.MouseMoves);
        if (clipped)
        {
            Assert.Empty(sender.MouseTransitions);
            Assert.Contains("cursor", macros.GetSession().FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal((50, 20), sender.MouseMoves.Last());
            Assert.Collection(sender.MouseTransitions,
                down => Assert.True(down.IsDown), up => Assert.False(up.IsDown));
        }
    }
}
