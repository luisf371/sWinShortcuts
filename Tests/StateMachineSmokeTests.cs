using System.Windows.Input;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class StateMachineSmokeTests
{
    [Fact]
    public void AntiAfkSequence_IsWasdInOrder()
    {
        var runtime = new InputRuntimeState();
        var queue = new RecordingInputQueue();
        var random = new ThreadLocal<Random>(() => new Random(1));
        var logger = new NullLoggerService();
        var autoRun = new AutoRunStateMachine(runtime, queue, random, logger);
        using var antiAfk = new AntiAfkStateMachine(runtime, autoRun, random, logger);

        Assert.Equal(
            new[] { Key.W, Key.A, Key.S, Key.D },
            antiAfk.BuildSequence().Select(step => step.Key).ToArray());
    }

    [Theory]
    [InlineData(25, 0, 25)]
    [InlineData(25, 24.1, 1)]
    [InlineData(25, 30, 25)]
    public void RapidFireSuccessorDelay_CompensatesOnlyWithinInterval(
        int target,
        double elapsed,
        int expected)
    {
        Assert.Equal(expected, RapidFireStateMachine.CalculateSuccessorDelay(target, elapsed));
    }
}
