using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class MacroStatusTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(200)]
    [InlineData(1000)]
    public Task Playback_ProgressBurst_BoundsFullValidationAndKeepsLatestStatus(int rows) => MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var input = MacroPlaybackTests.Create(new RecordingInputSender(), out var profile,
            Enumerable.Repeat(new MacroStep { Kind = MacroStepKind.Wait }, rows).ToArray());
        var store = new InMemoryProfileStore();
        store.Profiles.Add(profile);
        using var vm = new MainViewModel(new ProfileManager(store), new FakeDialogService(), new FakeDisplayService(),
            new RecordingColorControlService(), inputHookService: input, dispatcher: dispatcher);
        await vm.InitializeAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var editor = vm.Profiles.Single(p => ReferenceEquals(p.Model, profile)).Macros;
        var validations = 0;
        editor.Definitions[0].PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MacroViewModel.Error)) validations++; };
        var inputQueued = 0;
        double inputDelay = -1;
        input.MacroSessionChanged += (_, _) =>
        {
            if (input.GetMacroSession().Mode != MacroSessionMode.Playing || Interlocked.Exchange(ref inputQueued, 1) != 0) return;
            var inputStarted = Stopwatch.GetTimestamp();
            dispatcher.BeginInvoke(DispatcherPriority.Input,
                new Action(() => inputDelay = Stopwatch.GetElapsedTime(inputStarted).TotalMilliseconds));
        };
        var started = Stopwatch.GetTimestamp();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        MacroPlaybackTests.Press(input, 0x75);
        while (input.GetMacroSession().Mode != MacroSessionMode.Idle || input.GetMacroSession().SessionId == 0)
            await Task.Delay(1);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var allocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        output.WriteLine($"{rows} rows: {validations} validations, {allocation:N0} UI-thread bytes, {elapsed:N0} ms to idle, {inputDelay:N0} ms input-priority delay.");
        Assert.InRange(validations, 1, 10);
        Assert.True(inputDelay >= 0);
        Assert.Equal("Idle", editor.SessionStatus);
        Assert.False(editor.IsPlaying);
    });

    [Fact]
    public Task SessionNotifications_Burst_RequeriesLatestStateAndShortcutConflict() => MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
    {
        var profile = new Profile { Name = "Game", Executable = "game.exe" };
        profile.Macros.Definitions = [new MacroDefinition { IsEnabled = true, ShortcutKey = Key.F6,
            Steps = [new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A }] }];
        var store = new InMemoryProfileStore();
        store.Profiles.Add(profile);
        var input = new FakeInputHookService();
        using var vm = new MainViewModel(new ProfileManager(store), new FakeDialogService(), new FakeDisplayService(),
            new RecordingColorControlService(), inputHookService: input, dispatcher: Dispatcher.CurrentDispatcher);
        await vm.InitializeAsync();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var editor = vm.Profiles.Single(p => ReferenceEquals(p.Model, profile)).Macros;
        var validations = 0;
        input.MacroShortcutError = (_, _) => { validations++; return "The shortcut was reassigned."; };
        for (var row = 1; row <= 500; row++)
        {
            input.MacroSession = new(1, profile, profile.Macros.Definitions[0].Id, MacroSessionMode.Playing, TimeSpan.Zero, row);
            input.RaiseMacroSessionChanged();
        }
        input.MacroSession = input.MacroSession with { Mode = MacroSessionMode.Idle };
        input.RaiseMacroSessionChanged();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(1, validations);
        Assert.Equal("The shortcut was reassigned.", editor.Definitions[0].ValidationMessage);
        Assert.Equal("Idle", editor.SessionStatus);
    });

    [Theory]
    [InlineData(MacroSessionMode.Playing)]
    [InlineData(MacroSessionMode.Recording)]
    public Task SessionProgress_TimerReadsLatestRowsWithoutRevalidation(MacroSessionMode mode) => MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
    {
        var profile = new Profile { Name = "Game", Executable = "game.exe" };
        profile.Macros.Definitions = [new MacroDefinition()];
        var store = new InMemoryProfileStore();
        store.Profiles.Add(profile);
        var input = new FakeInputHookService();
        using var vm = new MainViewModel(new ProfileManager(store), new FakeDialogService(), new FakeDisplayService(),
            new RecordingColorControlService(), inputHookService: input, dispatcher: Dispatcher.CurrentDispatcher);
        await vm.InitializeAsync();
        var editor = vm.Profiles.Single(p => ReferenceEquals(p.Model, profile)).Macros;
        input.MacroSession = new(1, profile, profile.Macros.Definitions[0].Id, mode, TimeSpan.Zero, 1);
        input.RaiseMacroSessionChanged();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var validations = 0;
        input.MacroShortcutError = (_, _) => { validations++; return null; };
        // Progress changes no longer raise an event per row; the existing timer polls it.
        input.MacroSession = input.MacroSession with { RowCount = 27, Elapsed = TimeSpan.FromSeconds(2) };
        var started = Stopwatch.GetTimestamp();
        while (!editor.SessionStatus.Contains("27 rows") && Stopwatch.GetElapsedTime(started).TotalSeconds < 3)
            await Task.Delay(10);
        Assert.Contains("00:02 · 27 rows", editor.SessionStatus);
        Assert.Equal(0, validations);
        input.MacroSession = input.MacroSession with { Mode = MacroSessionMode.Idle };
        input.RaiseMacroSessionChanged();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("Idle", editor.SessionStatus);
    });
}
