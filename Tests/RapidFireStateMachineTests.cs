using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using sWinShortcuts.Utilities;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class RapidFireStateMachineTests
{
    [Fact]
    public void Toggle_TypematicFiresOnceAndReassignmentDisarms()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);

        Assert.False(rapidFire.SetToggleKey(Key.F8));
        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());

        Assert.True(rapidFire.SetToggleKey(Key.F9));
        Assert.Equal(RapidFireArmStatus.Off, rapidFire.GetStatus());
        Assert.False(rapidFire.HandleToggleKey(Vk(Key.F8), isKeyDown: true, isKeyUp: false));
        Assert.True(Toggle(rapidFire, Key.F9));
        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());
    }

    [Fact]
    public void Toggle_HeldAcrossRestartRequiresFreshDown()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.SetToggleKey(Key.F8);

        runtime.SetRunning(false);
        rapidFire.Release(preservePhysicalPairing: false);
        rapidFire.SeedTogglePhysicalState(vk => vk == Vk(Key.F8));
        runtime.SetRunning(true);

        Assert.False(rapidFire.HandleToggleKey(Vk(Key.F8), isKeyDown: true, isKeyUp: false));
        Assert.Equal(RapidFireArmStatus.Off, rapidFire.GetStatus());

        Assert.False(rapidFire.HandleToggleKey(Vk(Key.F8), isKeyDown: false, isKeyUp: true));
        Assert.True(rapidFire.HandleToggleKey(Vk(Key.F8), isKeyDown: true, isKeyUp: false));
        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());
    }

    [Fact]
    public void Toggle_RetargetsEligibleOwnerAndDisarmsStrandedArm()
    {
        var first = RapidFireProfile("First");
        var second = RapidFireProfile("Second");
        var runtime = RunningRuntime(first);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.SetToggleKey(Key.F8);

        Assert.True(Toggle(rapidFire, Key.F8));
        Publish(runtime, second, foregroundGeneration: 2);
        Assert.Equal(RapidFireArmStatus.ArmedNotReady, rapidFire.GetStatus());

        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());

        Publish(runtime, profile: null, foregroundGeneration: 3);
        Assert.Equal(RapidFireArmStatus.ArmedNotReady, rapidFire.GetStatus());
        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.Equal(RapidFireArmStatus.Off, rapidFire.GetStatus());
    }

    [Fact]
    public void ForegroundReleaseAndSameProfileRepublish_PreserveStickyArmAndStatusTransitions()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);

        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        rapidFire.CancelPress();
        rapidFire.FireTimerForTesting();
        Assert.Empty(sender.MouseClickThreadIds);
        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());

        runtime.SetForegroundIdentity(IntPtr.Zero, 0, profile.NormalizedExecutable, foregroundGeneration: 2);
        Assert.Equal(RapidFireArmStatus.ArmedNotReady, rapidFire.GetStatus());
        runtime.SetActiveProfile(profile, foregroundGeneration: 2);
        Assert.Equal(RapidFireArmStatus.Ready, rapidFire.GetStatus());
    }

    [Fact]
    public void StaleTimerAndOwnerProfileEdit_CannotClickAndDisarmOwner()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);

        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        rapidFire.CancelPress();
        rapidFire.FireTimerForTesting();
        Assert.Empty(sender.MouseClickThreadIds);

        rapidFire.HandleLeftButton(isDown: false, allowStart: false);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        profile.RapidFire.IsEnabled = false;
        Assert.True(rapidFire.ReleaseOwnedBy(profile));
        rapidFire.FireTimerForTesting();

        Assert.Empty(sender.MouseClickThreadIds);
        Assert.Equal(RapidFireArmStatus.Off, rapidFire.GetStatus());
    }

    [Fact]
    public void AdvancedModeOff_BlocksPendingClickAndMakesArmUnavailable()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);

        runtime.SetAdvancedMode(false);
        Assert.Equal(RapidFireArmStatus.Off, rapidFire.GetStatus());
        rapidFire.FireTimerForTesting();

        Assert.Empty(sender.MouseClickThreadIds);
        Assert.True(rapidFire.Release(preservePhysicalPairing: true));
    }

    [Fact]
    public async Task DisposalWhileClickBlocked_DoesNotScheduleSuccessor()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender(blockMouse: true);
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = Create(runtime, sender, random);
        rapidFire.ConfigureForTesting(profile, foregroundGeneration: 1);
        rapidFire.HandleLeftButton(isDown: true, allowStart: true);
        var fire = Task.Run(rapidFire.FireTimerForTesting);

        try
        {
            Assert.True(sender.MouseEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(runtime.TryBeginDispose());
            rapidFire.Dispose();
            sender.ReleaseMouse.Set();
            await fire.WaitAsync(TimeSpan.FromSeconds(2));

            rapidFire.FireTimerForTesting();
            Assert.Single(sender.MouseClickThreadIds);
        }
        finally
        {
            sender.ReleaseMouse.Set();
        }
    }

    [Theory]
    [InlineData(25, 0, 25)]
    [InlineData(25, 24.1, 1)]
    [InlineData(25, 30, 25)]
    public void CalculateSuccessorDelay_CompensatesOnlyWithinInterval(
        int target,
        double elapsed,
        int expected)
    {
        Assert.Equal(expected, RapidFireStateMachine.CalculateSuccessorDelay(target, elapsed));
    }

    [Fact]
    public void Disarm_ToggleOffAndReassignment_LogTheirReasons()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var logger = new NullLoggerService { IsEnabled = true };
        using var rapidFire = Create(runtime, sender, random, logger);

        // Reassignment before anything is armed is a no-op release: nothing to report.
        Assert.False(rapidFire.SetToggleKey(Key.F8));
        Assert.Empty(logger.Messages);

        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.Equal($"Rapid Fire armed for profile: {profile.Name}", Assert.Single(logger.Messages));

        // Toggle-off is a real disarm and carries its reason.
        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.Equal("Rapid Fire disarmed (toggle-off)", logger.Messages[^1]);
        Assert.Single(logger.Messages, m => m == "Rapid Fire disarmed (toggle-off)");

        logger.Messages.Clear();
        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.True(rapidFire.SetToggleKey(Key.F9));
        Assert.Equal("Rapid Fire disarmed (toggle key reassigned)", logger.Messages[^1]);
        Assert.Single(logger.Messages, m => m == "Rapid Fire disarmed (toggle key reassigned)");
    }

    [Fact]
    public void Disarm_OwnerReleaseLogsReason_NoOpReleasesStaySilent()
    {
        var profile = RapidFireProfile();
        var runtime = RunningRuntime(profile);
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var logger = new NullLoggerService { IsEnabled = true };
        using var rapidFire = Create(runtime, sender, random, logger);

        // Disarmed machine: neither a direct release nor an owner-scoped release logs.
        Assert.False(rapidFire.Release(preservePhysicalPairing: true));
        Assert.False(rapidFire.ReleaseOwnedBy(profile));
        Assert.Empty(logger.Messages);

        rapidFire.SetToggleKey(Key.F8);
        Assert.True(Toggle(rapidFire, Key.F8));
        Assert.True(rapidFire.ReleaseOwnedBy(profile));
        Assert.Single(logger.Messages, m => m == "Rapid Fire disarmed (owner settings changed/removed)");
    }

    private static RapidFireStateMachine Create(
        InputRuntimeState runtime,
        IInputSender sender,
        ThreadLocal<Random> random,
        NullLoggerService? logger = null) =>
        new(runtime, sender, random, logger ?? new NullLoggerService(), new object());

    private static Profile RapidFireProfile(string name = "Game")
    {
        return new Profile
        {
            Name = name,
            Executable = $"{name.ToLowerInvariant()}.exe",
            RapidFire =
            {
                IsEnabled = true,
                IntervalMilliseconds = RapidFireSettings.MaxIntervalMilliseconds,
                JitterMilliseconds = 0
            }
        };
    }

    private static InputRuntimeState RunningRuntime(Profile profile)
    {
        var runtime = new InputRuntimeState();
        runtime.SetRunning(true);
        runtime.SetAdvancedMode(true);
        Publish(runtime, profile, foregroundGeneration: 1);
        return runtime;
    }

    private static void Publish(
        InputRuntimeState runtime,
        Profile? profile,
        long foregroundGeneration)
    {
        runtime.SetActiveProfile(profile, foregroundGeneration);
        runtime.SetForegroundIdentity(
            IntPtr.Zero,
            0,
            profile?.NormalizedExecutable,
            foregroundGeneration);
    }

    private static bool Toggle(RapidFireStateMachine rapidFire, Key key)
    {
        var virtualKey = Vk(key);
        var changed = rapidFire.HandleToggleKey(virtualKey, isKeyDown: true, isKeyUp: false);
        Assert.False(rapidFire.HandleToggleKey(virtualKey, isKeyDown: true, isKeyUp: false));
        Assert.False(rapidFire.HandleToggleKey(virtualKey, isKeyDown: false, isKeyUp: true));
        return changed;
    }

    private static int Vk(Key key) => KeyInteropUtilities.ToVirtualKey(key);
}
