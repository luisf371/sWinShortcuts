using System.Collections.Concurrent;
using System.Reflection;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class DisplayRefreshTests
{
    [Fact]
    public async Task TopologyEvent_DuringEnumeration_ReturnsAndRefreshesBeforeResult()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var service = new DisplayService(() =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return [Display("old")];
            }
            return [Display("latest")];
        });
        var enumeration = Task.Run(service.GetDisplays);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var notification = Task.Run(() => RaiseTopology(service));
        try
        {
            await notification.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.False(enumeration.IsCompleted);
        }
        finally
        {
            release.Set();
            await notification;
            await enumeration;
        }
        Assert.Equal("latest", (await enumeration).Single().Id);
        Assert.Equal(2, calls);
        Assert.Equal("latest", service.GetDisplays().Single().Id);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task InitialEditorLoad_BlockedEnumeration_KeepsOwnerContextResponsive()
    {
        using var ui = new OwnerContext();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var enumerationThread = 0;
        using var service = new DisplayService(() =>
        {
            enumerationThread = Environment.CurrentManagedThreadId;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            return [Display("first")];
        });
        var color = new RecordingColorControlService();
        var construction = ui.Run(() => new ColorSettingsViewModel(new ColorSettings(), service, color, allowLiveUpdates: true));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.Equal(ui.ThreadId, await ui.Run(() => Environment.CurrentManagedThreadId).WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.NotEqual(ui.ThreadId, enumerationThread);
            Assert.Empty((await construction).DisplayViewModels);
        }
        finally
        {
            release.Set();
            var vm = await construction;
            await ui.Run(vm.Dispose);
            await vm.DisplayRefreshTask;
        }
        Assert.Empty(color.AppliedProfiles);
    }

    [Fact]
    public async Task VariantSwitch_ReusesSnapshotWithoutReenumeration()
    {
        var calls = 0;
        using var service = new DisplayService(() => { calls++; return [Display("first")]; });
        var color = new RecordingColorControlService();
        using var ui = new OwnerContext();
        var vm = await ui.Run(() => new ColorSettingsViewModel(new ColorSettings { HasSecondary = true }, service, color, allowLiveUpdates: true));
        await WaitForRows(ui, vm, "first");
        await ui.Run(() =>
        {
            // Invalidating without raising an editor event isolates the variant-rebuild path.
            typeof(DisplayService).GetField("_cachedDisplays", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, null);
            vm.IsEditingSecondary = true;
            Assert.Single(vm.DisplayViewModels);
            vm.Dispose();
        });
        Assert.Equal(1, calls);
        Assert.Empty(color.AppliedProfiles);
    }

    [Fact]
    public async Task EditorRefresh_EventBurstDuringLoad_DiscardsOldRowsAndUsesLatestVariant()
    {
        using var ui = new OwnerContext();
        var service = new ControlledDisplayService();
        var settings = new ColorSettings { IsEnabled = true, HasSecondary = true };
        settings.SetProfile(new DisplayColorProfile { DisplayId = "latest", Brightness = 35 });
        var color = new RecordingColorControlService();
        var vm = await ui.Run(() => new ColorSettingsViewModel(settings, service, color, allowLiveUpdates: true));
        var publicationThreads = new List<int>();
        await ui.Run(() =>
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(vm.HasDisplays)) publicationThreads.Add(Environment.CurrentManagedThreadId);
            };
            vm.IsEditingSecondary = true;
            for (var i = 0; i < 20; i++) service.RaiseDisplaysChanged();
        });
        Assert.Equal(1, service.Calls);

        service.First.SetResult([Display("obsolete")]);
        await service.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ui.Run(() => Assert.Empty(vm.DisplayViewModels));
        service.Second.SetResult([Display("latest")]);
        await vm.DisplayRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));

        await ui.Run(() =>
        {
            var row = Assert.Single(vm.DisplayViewModels);
            Assert.Equal("latest", row.DisplayName);
            Assert.Equal(35, row.Brightness);
            Assert.True(vm.IsEditingSecondary);
            Assert.All(publicationThreads, id => Assert.Equal(ui.ThreadId, id));
            Assert.Empty(color.AppliedProfiles);
            vm.Dispose();
        });
        Assert.Equal(2, service.Calls);
    }

    [Fact]
    public async Task EditorDisposed_DuringLoad_DiscardsResultAndIgnoresFurtherEvents()
    {
        using var ui = new OwnerContext();
        var service = new ControlledDisplayService();
        var color = new RecordingColorControlService();
        var vm = await ui.Run(() => new ColorSettingsViewModel(new ColorSettings(), service, color, allowLiveUpdates: true));
        var publications = 0;
        await ui.Run(() =>
        {
            vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.HasDisplays)) publications++; };
            vm.Dispose();
        });
        await Task.Run(service.RaiseDisplaysChanged);
        service.First.SetResult([Display("late")]);
        await vm.DisplayRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        await ui.Run(() =>
        {
            Assert.Empty(vm.DisplayViewModels);
            Assert.Equal(0, publications);
        });
        Assert.Equal(1, service.Calls);
        Assert.Empty(color.AppliedProfiles);
    }

    [Fact]
    public async Task EditorRefresh_FailedSnapshot_KeepsRowsAndRetriesNextEvent()
    {
        using var ui = new OwnerContext();
        var service = new ControlledDisplayService();
        service.First.SetResult([Display("previous")]);
        var vm = await ui.Run(() => new ColorSettingsViewModel(new ColorSettings(), service, new RecordingColorControlService()));
        await vm.DisplayRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));

        service.Second.SetException(new InvalidOperationException("Enumeration failed."));
        await ui.Run(service.RaiseDisplaysChanged);
        await vm.DisplayRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        await ui.Run(() => Assert.Equal("previous", Assert.Single(vm.DisplayViewModels).DisplayName));

        service.Third.SetResult([Display("recovered")]);
        await ui.Run(service.RaiseDisplaysChanged);
        await vm.DisplayRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        await ui.Run(() =>
        {
            Assert.Equal("recovered", Assert.Single(vm.DisplayViewModels).DisplayName);
            vm.Dispose();
        });
        Assert.Equal(3, service.Calls);
    }

    [Fact]
    public async Task EditorTopologyEvent_BlockedEnumeration_KeepsOwnerResponsiveAndRetainsNextEvent()
    {
        using var ui = new OwnerContext();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        using var service = new DisplayService(() =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 2)
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
            return [Display(call.ToString())];
        });
        var color = new RecordingColorControlService();
        var vm = await ui.Run(() => new ColorSettingsViewModel(new ColorSettings(), service, color, allowLiveUpdates: true));
        await vm.DisplayRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        var notification = ui.Run(() => RaiseTopology(service));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            await notification.WaitAsync(TimeSpan.FromSeconds(1));
            await ui.Run(() =>
            {
                RaiseTopology(service);
                Assert.Equal("1", Assert.Single(vm.DisplayViewModels).DisplayName);
            }).WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally { release.Set(); }
        await vm.DisplayRefreshTask.WaitAsync(TimeSpan.FromSeconds(5));
        await ui.Run(() =>
        {
            Assert.Equal("3", Assert.Single(vm.DisplayViewModels).DisplayName);
            vm.Dispose();
        });
        Assert.Equal(3, calls);
        Assert.Empty(color.AppliedProfiles);
    }

    [Fact]
    public void DisposedService_IgnoresEventsAndRejectsNewEnumeration()
    {
        var calls = 0;
        using var service = new DisplayService(() => { calls++; return [Display("first")]; });
        var events = 0;
        service.DisplaysChanged += (_, _) => events++;
        Assert.Single(service.GetDisplays());
        service.Dispose();

        RaiseTopology(service);

        Assert.Empty(service.GetDisplays());
        Assert.Equal(1, calls);
        Assert.Equal(0, events);
    }

    [Fact]
    public void RefreshAfterMidEnumerationEvent_Fails_DoesNotCacheIntermediateSnapshot()
    {
        var calls = 0;
        DisplayService? service = null;
        using (service = new DisplayService(() =>
        {
            switch (++calls)
            {
                case 1:
                    RaiseTopology(service!);
                    return [Display("obsolete")];
                case 2:
                    throw new InvalidOperationException("Enumeration failed.");
                default:
                    return [Display("latest")];
            }
        }))
        {
            Assert.Throws<InvalidOperationException>(() => service.GetDisplays());
            Assert.Equal("latest", service.GetDisplays().Single().Id);
            Assert.Equal(3, calls);
        }
    }

    private static async Task WaitForRows(OwnerContext ui, ColorSettingsViewModel vm, string id)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ui.Run(() =>
        {
            void Check()
            {
                if (vm.DisplayViewModels.Count == 1 && vm.DisplayViewModels[0].DisplayName == id) ready.TrySetResult();
            }
            vm.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(vm.HasDisplays)) Check(); };
            Check();
        });
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static DisplayInfo Display(string id) => new() { Id = id, Name = id, DeviceName = id };

    private static void RaiseTopology(DisplayService service) =>
        typeof(DisplayService).GetMethod("OnDisplaySettingsChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [null, EventArgs.Empty]);

    private sealed class ControlledDisplayService : IDisplayService
    {
        public TaskCompletionSource<IReadOnlyList<DisplayInfo>> First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<DisplayInfo>> Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<DisplayInfo>> Third { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public event EventHandler? DisplaysChanged;
        public IReadOnlyList<DisplayInfo> GetDisplays() => throw new InvalidOperationException("Editor enumeration must be asynchronous.");
        public Task<IReadOnlyList<DisplayInfo>> GetDisplaysAsync()
        {
            if (++Calls == 1) return First.Task;
            if (Calls == 3) return Third.Task;
            SecondEntered.TrySetResult();
            return Second.Task;
        }
        public void RaiseDisplaysChanged() => DisplaysChanged?.Invoke(this, EventArgs.Empty);
    }

    // An owner-thread message loop exercises UI affinity without creating any native window.
    private sealed class OwnerContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<Action> _pending = new();
        private readonly Thread _thread;
        public int ThreadId => _thread.ManagedThreadId;

        public OwnerContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var work in _pending.GetConsumingEnumerable()) work();
            }) { IsBackground = true };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback callback, object? state) => _pending.Add(() => callback(state));

        public Task Run(Action action) => Run(() => { action(); return true; });

        public Task<T> Run<T>(Func<T> action)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try { result.SetResult(action()); }
                catch (Exception ex) { result.SetException(ex); }
            }, null);
            return result.Task;
        }

        public void Dispose()
        {
            _pending.CompleteAdding();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)));
            _pending.Dispose();
        }
    }
}
