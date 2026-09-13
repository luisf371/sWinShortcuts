using System.Windows.Threading;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class MacroRecordingLifetimeTests
{
    [Fact]
    public async Task Exit_PlaybackCleanupPending_WaitsBeforeFlushingSaves()
    {
        var (vm, store, _, input, owner, _) = await CreateAsync();
        using (vm)
        {
            var retirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = new List<string>();
            input.MacroRecordingStopRequested = () => calls.Add("stop");
            input.RetireMacroSessionHandler = () => { calls.Add("retire"); return retirement.Task; };
            input.MacroSession = new(1, owner.Model, Guid.NewGuid(), MacroSessionMode.Playing, TimeSpan.Zero, 1);
            owner.Macros.SelectedMacro!.Label = "Pending save";
            var before = store.SaveCount;
            store.SaveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var exit = vm.PrepareForCloseAsync();

            Assert.Equal(new[] { "stop", "retire" }, calls);
            Assert.False(exit.IsCompleted);
            Assert.False(store.SaveEntered.Task.IsCompleted);
            retirement.SetResult(true);
            Assert.Equal(0, await exit.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(before + 1, store.SaveCount);
            Assert.False(vm.MacroSessionRetirementFailed);
        }
    }

    [Fact]
    public async Task Exit_PreparingRecording_RetiresBeforeAwaitingTake()
    {
        var (vm, store, _, input, owner, macro) = await CreateAsync();
        using (vm)
        {
            var recording = new TaskCompletionSource<MacroRecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            input.RecordMacroHandler = (_, _, _, _) => recording.Task;
            input.RetireMacroSessionHandler = () =>
            {
                Assert.Equal(1, input.StopMacroRecordingCount);
                recording.SetResult(Take(owner, macro));
                return Task.FromResult(true);
            };
            _ = vm.RecordMacroAsync(owner, macro);
            var before = store.SaveCount;

            Assert.Equal(0, await vm.PrepareForCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.Single(macro.Steps);
            Assert.Equal(before + 1, store.SaveCount);
            Assert.Equal(1, input.StopMacroRecordingCount);
        }
    }

    [Fact]
    public async Task Exit_RetirementTimesOut_PreservesPendingTakeAndAllowsRetry()
    {
        var (vm, store, _, input, owner, macro) = await CreateAsync();
        using (vm)
        {
            var recording = new TaskCompletionSource<MacroRecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            input.RecordMacroHandler = (_, _, _, _) => recording.Task;
            input.RetireMacroSessionHandler = () => Task.FromResult(false);
            _ = vm.RecordMacroAsync(owner, macro);
            var before = store.SaveCount;

            var first = vm.PrepareForCloseAsync();
            Assert.Equal(0, await first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(vm.MacroSessionRetirementFailed);
            Assert.True(owner.Macros.IsRecording);
            Assert.Empty(macro.Steps);
            Assert.Equal(before, store.SaveCount);

            input.RetireMacroSessionHandler = () =>
            {
                recording.SetResult(Take(owner, macro));
                return Task.FromResult(true);
            };
            var retry = vm.PrepareForCloseAsync();

            Assert.NotSame(first, retry);
            Assert.Equal(0, await retry.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(vm.MacroSessionRetirementFailed);
            Assert.False(owner.Macros.IsRecording);
            Assert.Single(macro.Steps);
            Assert.Equal(before + 1, store.SaveCount);
        }
    }

    [Fact]
    public async Task DisposeEditor_DuringRecording_RetainsTakeBeforeDetachingRows()
    {
        var (vm, _, _, input, owner, macro) = await CreateAsync();
        using (vm)
        {
            var completion = new TaskCompletionSource<MacroRecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            input.RecordMacroHandler = (_, _, _, _) => completion.Task;
            var recording = vm.RecordMacroAsync(owner, macro);

            owner.Macros.Dispose();
            Assert.True(input.StopMacroRecordingCount > 0);
            completion.SetResult(Take(owner, macro));
            await recording;

            Assert.Single(macro.Steps);
            Assert.Single(owner.Model.Macros.Definitions[0].Steps);
            Assert.False(macro.CanEdit);
            Assert.Equal(0, await vm.FlushPendingSavesAsync());
        }
    }

    [Fact]
    public async Task LeaveProfile_RecordingFinishesIntoOriginalDestination_Once()
    {
        var (vm, store, manager, input, owner, macro) = await CreateAsync();
        using (vm)
        {
            var completion = new TaskCompletionSource<MacroRecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            input.RecordMacroHandler = (_, _, _, _) => completion.Task;
            var recording = vm.RecordMacroAsync(owner, macro);
            var other = await manager.AddProfileAsync("Other", "other.exe");
            vm.SelectedProfile = vm.Profiles.Single(profile => ReferenceEquals(profile.Model, other));
            Assert.True(input.StopMacroRecordingCount > 0);
            Assert.True(owner.Macros.IsRecording);
            var before = store.SaveCount;

            completion.SetResult(Take(owner, macro));
            await recording;
            await vm.FinalizeMacroRecordingAsync();
            Assert.Equal(0, await vm.FlushPendingSavesAsync());

            Assert.Single(macro.Steps);
            Assert.Empty(other.Macros.Definitions);
            Assert.False(owner.Macros.IsRecording);
            Assert.Equal(before + 1, store.SaveCount);
        }
    }

    [Fact]
    public async Task ExitRequests_ShareOneOperation_ApplyRecordingBeforeSaving()
    {
        var (vm, store, _, input, owner, macro) = await CreateAsync();
        using (vm)
        {
            var completion = new TaskCompletionSource<MacroRecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            input.RecordMacroHandler = (_, _, _, _) => completion.Task;
            _ = vm.RecordMacroAsync(owner, macro);
            var before = store.SaveCount;
            var first = vm.PrepareForCloseAsync();
            var second = vm.PrepareForCloseAsync();
            Assert.Same(first, second);
            Assert.False(first.IsCompleted);
            Assert.Equal(1, input.StopMacroRecordingCount);

            completion.SetResult(Take(owner, macro));

            Assert.Equal(0, await first);
            Assert.Single(macro.Steps);
            Assert.Equal(before + 1, store.SaveCount);
            Assert.Single(store.SavedProfiles[^1].Macros.Definitions[0].Steps);
        }
    }

    [Fact]
    public async Task DeleteRecordingProfile_WaitsForApplication_BeforeRemoving()
    {
        var (vm, store, manager, input, owner, macro) = await CreateAsync();
        using (vm)
        {
            var completion = new TaskCompletionSource<MacroRecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            input.RecordMacroHandler = (_, _, _, _) => completion.Task;
            _ = vm.RecordMacroAsync(owner, macro);
            var rowsWhenRemoved = -1;
            manager.ProfileRemoved += (_, profile) =>
            {
                if (ReferenceEquals(profile, owner.Model)) rowsWhenRemoved = profile.Macros.Definitions[0].Steps.Length;
            };
            var remove = vm.RemoveProfileCommand.ExecuteAsync(null);
            Assert.False(remove.IsCompleted);
            Assert.False(store.WasDeleted(owner.Name));

            completion.SetResult(Take(owner, macro));
            await remove;

            Assert.Equal(1, rowsWhenRemoved);
            Assert.True(store.WasDeleted(owner.Name));
            Assert.DoesNotContain(owner, vm.Profiles);
        }
    }

    [Fact]
    public async Task ShutdownDispatcher_RetainsFinalizedTake_WithoutWaitingForUi()
    {
        Dispatcher? stoppedDispatcher = null;
        var thread = new Thread(() =>
        {
            stoppedDispatcher = Dispatcher.CurrentDispatcher;
            stoppedDispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
        var (vm, _, _, input, owner, macro) = await CreateAsync(stoppedDispatcher);
        using (vm)
        {
            var result = Take(owner, macro);
            input.RecordMacroHandler = (_, _, _, _) => Task.FromResult(result);

            await vm.RecordMacroAsync(owner, macro).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(result, vm.UnappliedMacroRecording);
            Assert.Empty(macro.Steps);
            Assert.Equal(1, await vm.PrepareForCloseAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task SessionNotification_RequeriesCurrentState_AndDetachesOnDispose()
    {
        var (vm, _, _, input, owner, _) = await CreateAsync();
        Assert.Equal(1, input.MacroSessionSubscriberCount);
        input.MacroSession = new(7, owner.Model, Guid.NewGuid(), MacroSessionMode.Playing, TimeSpan.FromSeconds(3), 5);
        input.RaiseMacroSessionChanged();
        Assert.True(owner.Macros.IsPlaying);
        Assert.Contains("Playing", owner.Macros.SessionStatus);

        vm.Dispose();

        Assert.Equal(0, input.MacroSessionSubscriberCount);
    }

    [Fact]
    public Task ExitOnUiDispatcher_AppliesPoolCompletionBeforeFlush() => RunOnStaAsync(async () =>
    {
        var (vm, store, _, input, owner, macro) = await CreateAsync(Dispatcher.CurrentDispatcher);
        using (vm)
        {
            var uiThread = Environment.CurrentManagedThreadId;
            var editThread = 0;
            owner.ProfileChanged += (_, e) =>
            {
                if (e.Kind == ProfileChangeKind.Macros) editThread = Environment.CurrentManagedThreadId;
            };
            var completion = new TaskCompletionSource<MacroRecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            input.RecordMacroHandler = (_, _, _, _) => completion.Task;
            _ = vm.RecordMacroAsync(owner, macro);
            var exit = vm.PrepareForCloseAsync();
            await Task.Run(() => completion.SetResult(Take(owner, macro)));

            Assert.Equal(0, await exit);
            Assert.Equal(uiThread, editThread);
            Assert.Single(store.SavedProfiles[^1].Macros.Definitions[0].Steps);
        }
    });

    private static MacroRecordingResult Take(ProfileViewModel owner, MacroViewModel macro) =>
        new(1, owner.Model, macro.Id, [new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 25 }], MacroRecordingEndReason.Stopped, false);

    private static async Task<(MainViewModel Vm, InMemoryProfileStore Store, ProfileManager Manager, FakeInputHookService Input,
        ProfileViewModel Owner, MacroViewModel Macro)> CreateAsync(Dispatcher? dispatcher = null)
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        var input = new FakeInputHookService();
        var vm = new MainViewModel(manager, new FakeDialogService(), new FakeDisplayService(), new RecordingColorControlService(),
            removeBypassModifierDown: () => true, inputHookService: input, dispatcher: dispatcher);
        await vm.InitializeAsync();
        var model = await manager.AddProfileAsync("Game", "game.exe");
        var owner = vm.Profiles.Single(profile => ReferenceEquals(profile.Model, model));
        owner.Macros.NewMacroCommand.Execute(null);
        Assert.Equal(0, await vm.FlushPendingSavesAsync());
        return (vm, store, manager, input, owner, owner.Macros.SelectedMacro!);
    }

    internal static async Task RunOnStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
