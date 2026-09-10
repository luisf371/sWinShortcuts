using System.Windows.Input;
using System.Reflection;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class InputForegroundAdmissionTests
{
    [Fact]
    public void Service_DefaultConstruction_UsesNativeForegroundTransport()
    {
        using var service = new InputHookService(new NullLoggerService(), new RecordingInputSender());
        var runtime = typeof(InputHookService).GetField("_runtime",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var transport = typeof(InputRuntimeState).GetField("_foregroundTransport",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime);
        Assert.IsType<NativeAutoRunTransport>(transport);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_ProfileOutputsUseSharedLiveTransport(bool rapidFireOutput)
    {
        var transport = FakeAutoRunTransport.MatchingForeground();
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender,
            () => 0, _ => false, transport);
        service.StartInputExecutorForTesting();
        try
        {
            var profile = new Profile { Name = "Game", Executable = "game.exe" };
            profile.CombinedMappings.IsEnabled = true;
            profile.CombinedMappings.Mappings = [new CombinedMappingEntry
            {
                Source = InputTrigger.FromKey(Key.E), TargetKey = Key.A
            }];
            profile.RapidFire.IsEnabled = true;
            profile.RapidFire.IntervalMilliseconds = RapidFireSettings.MaxIntervalMilliseconds;
            service.ConfigureRapidFireForTesting(profile, 1);
            transport.ProcessIds[(IntPtr)100] = 43;
            if (rapidFireOutput)
            {
                service.DispatchDecodedMouseEvent(NativeMethods.WM_LBUTTONDOWN, 0);
                service.FireRapidFireTimerForTesting();
            }
            else service.DispatchDecodedKeyboardEvent(KeyInterop.VirtualKeyFromKey(Key.E), true, false);
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Empty(sender.Transitions);
            Assert.Empty(sender.MouseClickThreadIds);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task ProfileDown_WatcherHasNotPublishedLiveChange_DropsOutput(int kind, bool reusedWindow)
    {
        var (runtime, transport, profile) = Setup();
        var sender = new RecordingInputSender();
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService(),
            clock: () => 0, keyState: _ => false);
        executor.Start();
        if (reusedWindow) transport.ProcessIds[(IntPtr)100] = 43;
        else transport.ForegroundWindow = (IntPtr)200;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(executor.Enqueue(new InputCommand(Key.A, true,
            Kind: (InputCommandKind)kind, Sequence: [new TapStep(Key.A, 0, 0)],
            ExpectedProfile: profile, ForegroundGeneration: 1, Completion: completion)));
        await Fence(executor);

        Assert.Empty(sender.Transitions);
        Assert.True(runtime.ProfileInputGenerationIsCurrent());
        Assert.True(executor.StopAndDrain());
    }

    [Fact]
    public async Task ProfileDown_IdentityChangesDuringPidLookup_RechecksGeneration()
    {
        var (runtime, transport, profile) = Setup();
        var sender = new RecordingInputSender();
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        executor.Start();
        transport.BlockProcessReadNumber = 1;
        try
        {
            Assert.True(executor.Enqueue(new InputCommand(Key.A, true,
                ExpectedProfile: profile, ForegroundGeneration: 1)));
            Assert.True(transport.ProcessReadEntered.Wait(TimeSpan.FromSeconds(2)));
            runtime.SetForegroundIdentity((IntPtr)200, 43, "other.exe", 2);
            transport.ReleaseProcessRead.Set();
            await Fence(executor);
            Assert.Empty(sender.Transitions);
        }
        finally
        {
            transport.ReleaseProcessRead.Set();
            executor.StopAndDrain();
        }
    }

    [Fact]
    public async Task ProfileDown_MatchingIdentitySends_ReleaseAndGlobalsSurviveFocusLoss()
    {
        var (runtime, transport, profile) = Setup();
        var sender = new RecordingInputSender();
        using var executor = new InputExecutor(runtime, sender, new NullLoggerService());
        executor.Start();
        Assert.True(executor.Enqueue(new InputCommand(Key.A, true,
            ExpectedProfile: profile, ForegroundGeneration: 1)));
        await Fence(executor);
        transport.ForegroundWindow = (IntPtr)200;
        Assert.True(executor.Enqueue(new InputCommand(Key.A, false,
            ExpectedProfile: profile, ForegroundGeneration: 1)));
        Assert.True(executor.Enqueue(new InputCommand(Key.B, true)));
        Assert.True(executor.Enqueue(new InputCommand(Key.B, false)));
        await Fence(executor);
        Assert.Equal(new[] { (Key.A, true), (Key.A, false), (Key.B, true), (Key.B, false) },
            sender.Transitions.Select(item => (item.Key, item.IsDown)));
        Assert.True(executor.StopAndDrain());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RapidFire_WatcherHasNotPublishedLiveChange_DropsClick(bool reusedWindow)
    {
        var (runtime, transport, profile) = Setup();
        var sender = new RecordingInputSender();
        using var random = new ThreadLocal<Random>(() => new Random(1));
        using var rapidFire = new RapidFireStateMachine(runtime, sender, random,
            new NullLoggerService(), new object());
        rapidFire.SetToggleKey(Key.F8);
        Assert.True(rapidFire.HandleToggleKey(KeyInterop.VirtualKeyFromKey(Key.F8), true, false));
        rapidFire.HandleLeftButton(true, allowStart: true);
        if (reusedWindow) transport.ProcessIds[(IntPtr)100] = 43;
        else transport.ForegroundWindow = (IntPtr)200;
        rapidFire.FireTimerForTesting();
        Assert.Empty(sender.MouseClickThreadIds);
        Assert.True(runtime.ProfileInputGenerationIsCurrent());
    }

    private static (InputRuntimeState, FakeAutoRunTransport, Profile) Setup()
    {
        var transport = new FakeAutoRunTransport();
        transport.ProcessIds[(IntPtr)100] = 42;
        transport.ProcessIds[(IntPtr)200] = 43;
        var runtime = new InputRuntimeState(transport);
        var profile = new Profile { Name = "Game", Executable = "game.exe" };
        profile.RapidFire.IsEnabled = true;
        profile.RapidFire.IntervalMilliseconds = RapidFireSettings.MaxIntervalMilliseconds;
        profile.RapidFire.JitterMilliseconds = 0;
        runtime.SetRunning(true);
        runtime.SetAdvancedMode(true);
        runtime.SetActiveProfile(profile, 1);
        runtime.SetForegroundIdentity((IntPtr)100, 42, profile.NormalizedExecutable, 1);
        return (runtime, transport, profile);
    }

    private static async Task Fence(InputExecutor executor)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(executor.Enqueue(new InputCommand(Key.None, false,
            Kind: InputCommandKind.DummyKey, Completion: completion)));
        Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
}
