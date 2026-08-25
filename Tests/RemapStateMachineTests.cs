using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Services.Input;
using sWinShortcuts.Utilities;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class RemapStateMachineTests
{
    [Fact]
    public void Combined_TwoSourcesShareTarget_ReleasesOnlyAfterFinalUp()
    {
        var profile = CombinedProfile(
            new() { SourceKey = Key.E, TargetKey = Key.F },
            new() { SourceKey = Key.G, TargetKey = Key.F });
        var runtime = RunningRuntime(profile);
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var remaps = new RemapStateMachine(runtime, queue, random, new NullLoggerService());

        Assert.True(Handle(remaps, Key.E, isDown: true));
        Assert.True(Handle(remaps, Key.G, isDown: true));
        Assert.Single(queue.Commands);

        Assert.True(Handle(remaps, Key.E, isDown: false));
        Assert.Single(queue.Commands);
        Assert.True(Handle(remaps, Key.G, isDown: false));

        Assert.Equal(
            new[] { (Key.F, true), (Key.F, false) },
            queue.Commands.Select(command => (command.Key, command.IsDown)).ToArray());
    }

    [Fact]
    public void Combined_ForcedRelease_PreservesSuppressionThroughMatchingUp()
    {
        var profile = CombinedProfile(new() { SourceKey = Key.E, TargetKey = Key.F });
        var runtime = RunningRuntime(profile);
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var remaps = new RemapStateMachine(runtime, queue, random, new NullLoggerService());

        Assert.True(Handle(remaps, Key.E, isDown: true));
        var guardedDown = Assert.Single(queue.Commands);
        remaps.ForceReleaseCombinedForTesting();

        Assert.False(remaps.CanExecute(in guardedDown));
        Assert.True(Handle(remaps, Key.E, isDown: true));
        Assert.True(Handle(remaps, Key.E, isDown: false));
        Assert.False(Handle(remaps, Key.E, isDown: false));
        Assert.Equal(
            new[] { (Key.F, true), (Key.F, false) },
            queue.Commands.Select(command => (command.Key, command.IsDown)).ToArray());
    }

    [Fact]
    public void CapsNormalRemap_TypematicAndUpUseLatchedOutputAfterLiveDisable()
    {
        var profile = CapsProfile();
        var runtime = RunningRuntime(profile);
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var remaps = new RemapStateMachine(runtime, queue, random, new NullLoggerService());

        Assert.True(Handle(remaps, Key.CapsLock, isDown: true));
        var initialDown = Assert.Single(queue.Commands);
        profile.CapsLock.IsEnabled = false;
        profile.CapsLock.RemapTarget = Key.B;

        Assert.True(remaps.CanExecute(in initialDown));
        Assert.True(Handle(remaps, Key.CapsLock, isDown: true));
        Assert.True(Handle(remaps, Key.CapsLock, isDown: false));
        Assert.Equal(
            new[] { (Key.Escape, true), (Key.Escape, true), (Key.Escape, false) },
            queue.Commands.Select(command => (command.Key, command.IsDown)).ToArray());
    }

    [Fact]
    public void CapsForcedRelease_InvalidatesDownAndConsumesPhysicalUpWithoutSecondRelease()
    {
        var profile = CapsProfile();
        var runtime = RunningRuntime(profile);
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        var remaps = new RemapStateMachine(runtime, queue, random, new NullLoggerService());

        Assert.True(Handle(remaps, Key.CapsLock, isDown: true));
        var guardedDown = Assert.Single(queue.Commands);
        remaps.ReleaseCapsStateOnly(preservePhysicalPairing: true);

        Assert.False(remaps.CanExecute(in guardedDown));
        Assert.True(Handle(remaps, Key.CapsLock, isDown: false));
        Assert.Equal(
            new[] { (Key.Escape, true), (Key.Escape, false) },
            queue.Commands.Select(command => (command.Key, command.IsDown)).ToArray());
    }

    [Fact]
    public void Launcher_TypematicWaitsForDummyAcknowledgementAndUpClearsLatch()
    {
        var runtime = RunningRuntime();
        var queue = new RecordingInputQueue();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var launched = new ManualResetEventSlim(false);
        (string Path, string Arguments, bool RunAsAdmin)? launch = null;
        var remaps = new RemapStateMachine(
            runtime,
            queue,
            random,
            new NullLoggerService(),
            isPhysicalKeyDown: virtualKey => virtualKey == Vk(Key.LWin),
            launchProcess: (path, arguments, runAsAdmin) =>
            {
                launch = (path, arguments, runAsAdmin);
                launched.Set();
            });
        var windowsProfile = new Profile
        {
            Name = ProfileConstants.WindowsProfileName,
            Kind = ProfileKind.Windows,
            Executable = string.Empty
        };
        windowsProfile.WindowsLauncher.Launchers[Key.NumPad1] = new LauncherBinding
        {
            Path = "tool.exe",
            Arguments = "--once",
            RunAsAdmin = true
        };
        remaps.SetWindowsProfile(windowsProfile);

        Assert.True(Handle(remaps, Key.NumPad1, isDown: true));
        Assert.True(Handle(remaps, Key.NumPad1, isDown: true));
        var dummy = Assert.Single(queue.Commands);
        Assert.Equal(InputCommandKind.DummyKey, dummy.Kind);
        Assert.Same(remaps, dummy.Guard);
        Assert.False(launched.IsSet);

        Assert.NotNull(dummy.Completion);
        Assert.True(dummy.Completion.TrySetResult(true));
        Assert.True(launched.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(launch.HasValue);
        Assert.Equal(("tool.exe", "--once", true), launch.Value);

        Assert.True(Handle(remaps, Key.NumPad1, isDown: false));
        Assert.False(Handle(remaps, Key.NumPad1, isDown: false));
    }

    private static Profile CombinedProfile(params CombinedMappingEntry[] mappings)
    {
        return new Profile
        {
            Name = "Game",
            Executable = "game.exe",
            CombinedMappings =
            {
                IsEnabled = true,
                Mappings = [.. mappings]
            }
        };
    }

    private static Profile CapsProfile()
    {
        return new Profile
        {
            Name = "Game",
            Executable = "game.exe",
            CapsLock =
            {
                IsEnabled = true,
                Mode = CapsLockMode.Normal,
                IsRemapEnabled = true,
                RemapTarget = Key.Escape
            }
        };
    }

    private static InputRuntimeState RunningRuntime(Profile? profile = null)
    {
        var runtime = new InputRuntimeState();
        runtime.SetRunning(true);
        runtime.SetAdvancedMode(true);
        runtime.SetActiveProfile(profile, foregroundGeneration: 1);
        runtime.SetForegroundIdentity(
            IntPtr.Zero,
            0,
            profile?.NormalizedExecutable,
            foregroundGeneration: 1);
        return runtime;
    }

    private static bool Handle(RemapStateMachine remaps, Key key, bool isDown) =>
        remaps.HandleKeyboardEvent(
            Vk(key),
            isKeyDown: isDown,
            isKeyUp: !isDown,
            rightButtonPressed: false);

    private static int Vk(Key key) => KeyInteropUtilities.ToVirtualKey(key);
}
