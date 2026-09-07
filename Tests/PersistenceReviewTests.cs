using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using sWinShortcuts;
using sWinShortcuts.Configuration;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Utilities;
using sWinShortcuts.ViewModels;
using sWinShortcuts.Views;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class PersistenceReviewTests
{
    [Theory]
    [InlineData(false, "false", true, false)]
    [InlineData(false, "true", false, true)]
    [InlineData(false, null, true, true)]
    [InlineData(false, null, false, false)]
    [InlineData(true, "false", true, false)]
    public async Task AdvancedModeStartup_ReadResult_PreservesExplicitPreferenceOrInfersMissingKey(
        bool failRead, string? persisted, bool hasAdvancedFeature, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "sWinShortcuts-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        FileStream? fileLock = null;
        try
        {
            var path = Path.Combine(root, "settings.ini");
            File.WriteAllText(path, "[App]\n" + (persisted is null ? "" : $"AdvancedMode={persisted}\n"));
            var profile = new Profile { Name = "Game", Executable = "game.exe" };
            profile.RightClickHoldBreath.IsEnabled = hasAdvancedFeature;
            var store = new InMemoryProfileStore();
            store.Profiles.Add(profile);
            var vm = new MainViewModel(new ProfileManager(store), new FakeDialogService(),
                new FakeDisplayService(), new RecordingColorControlService());
            await vm.InitializeAsync();
            var hook = new FakeInputHookService();
            var logger = new CallbackLogger(() => fileLock?.Dispose());

            // Exercise only the code-behind resolver; do not construct a WPF window or install hooks.
            var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindow).GetField("_settingsPath", fields)!.SetValue(window, path);
            typeof(MainWindow).GetField("_viewModel", fields)!.SetValue(window, vm);
            typeof(MainWindow).GetField("_inputHook", fields)!.SetValue(window, hook);
            typeof(MainWindow).GetField("_logger", fields)!.SetValue(window, logger);
            if (failRead)
            {
                // The error log releases the lock before any fallback could enqueue a successful write.
                fileLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            await ((Task)typeof(MainWindow).GetMethod("ResolveAndApplyAdvancedModeAsync", fields)!
                .Invoke(window, null)!).WaitAsync(TimeSpan.FromSeconds(5));
            var saved = await AppSettings.LoadAsync(path);

            Assert.Equal(failRead ? 1 : 0, logger.Calls);
            Assert.Equal(expected, hook.AdvancedModeEnabled);
            Assert.Equal(expected, vm.AdvancedModeEnabled);
            Assert.Equal(expected ? "true" : "false", saved.GetValue("App", "AdvancedMode"));
        }
        finally
        {
            fileLock?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CallbackLogger(Action onLog) : ILoggerService
    {
        public bool IsEnabled { get; set; } = true;
        public int Calls { get; private set; }
        public void Log(string message)
        {
            Calls++;
            onLog();
        }
    }

    [Fact]
    public async Task AppSettings_QueuedTransactionsAndFlush_PreserveBothUpdates()
    {
        var root = Path.Combine(Path.GetTempPath(), "sWinShortcuts-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var releaseFirst = new ManualResetEventSlim();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var path = Path.Combine(root, "settings.ini");
            var first = AppSettings.UpdateAsync(path, document =>
            {
                firstEntered.SetResult();
                Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
                document.SetValue("Window", "Width", "1200");
            });
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = AppSettings.UpdateAsync(path, document =>
            {
                Assert.Equal("1200", document.GetValue("Window", "Width"));
                document.SetValue("App", "CheckForUpdates", "true");
            });
            var flush = AppSettings.FlushAsync();
            Assert.False(flush.IsCompleted);
            releaseFirst.Set();
            await Task.WhenAll(first, second, flush).WaitAsync(TimeSpan.FromSeconds(5));

            var saved = await AppSettings.LoadAsync(path);
            Assert.Equal("1200", saved.GetValue("Window", "Width"));
            Assert.Equal("true", saved.GetValue("App", "CheckForUpdates"));
        }
        finally
        {
            releaseFirst.Set();
            await AppSettings.FlushAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AppSettings_FailedTransaction_DoesNotBlockLaterSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "sWinShortcuts-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.ini");
            await Assert.ThrowsAsync<IOException>(() => AppSettings.UpdateAsync(path, _ => throw new IOException("write failed")));
            await AppSettings.UpdateAsync(path, document => document.SetValue("App", "Value", @"C:\Games\name=value.exe"));
            await AppSettings.FlushAsync();
            Assert.Equal(@"C:\Games\name=value.exe", (await AppSettings.LoadAsync(path)).GetValue("App", "Value"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveIni_FailsAfterStartupApply_ReportsFailedCompensation(bool throws)
    {
        var root = Path.Combine(Path.GetTempPath(), "sWinShortcuts-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.ini");
            Directory.CreateDirectory(path); // A directory cannot be replaced by the INI file.
            var startup = new FailedStartupRestoration(throws);
            var snapshot = new IniDocument();
            snapshot.SetValue("App", "StartWithWindows", "false");

            var error = await SettingsWindow.SaveIniAsync(path, snapshot, startup,
                restoreStartup: true, baselineStartup: true, baselineAdmin: true);

            Assert.NotNull(error);
            Assert.Contains("Startup restoration failed: restoration denied", error);
            Assert.Equal([(true, true)], startup.Calls);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SettingsViewModel_Saving_DisablesAndNotifiesSettingsEditing()
    {
        var vm = new SettingsViewModel(new NullLoggerService(), new FakeInputHookService());
        Assert.True(vm.TryLoadIniState(new IniDocument(), out _));
        Assert.True(vm.CanEditSettings);
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        vm.IsSaving = true;

        Assert.False(vm.CanEditSettings);
        Assert.Contains(nameof(SettingsViewModel.CanEditSettings), notifications);
        notifications.Clear();
        vm.IsSaving = false;
        Assert.True(vm.CanEditSettings);
        Assert.Contains(nameof(SettingsViewModel.CanEditSettings), notifications);
    }

    [Theory]
    [InlineData("Game\nIsEnabled=false")]
    [InlineData("Game\rOther=value")]
    public void SetValue_MultilineValue_RejectsWithoutChangingExistingEntry(string value)
    {
        var ini = new IniDocument();
        ini.SetValue("Profile", "Name", "Original");
        Assert.Throws<ArgumentException>(() => ini.SetValue("Profile", "Name", value));
        Assert.Equal("Original", ini.GetValue("Profile", "Name"));
        ini.SetValue("Profile", "Name", " \r\n ");
        Assert.Null(ini.GetValue("Profile", "Name"));
    }

    [Theory]
    [InlineData("invalid.txt")]
    [InlineData("duplicate.exe")]
    public async Task ModifyProfile_RepairsNameAndExecutableTogether(string oldExecutable)
    {
        var store = new InMemoryProfileStore();
        var profile = new Profile { Name = "Old", Executable = oldExecutable, SourcePath = "original.ini" };
        store.Profiles.AddRange([profile, new Profile { Name = "Other", Executable = "duplicate.exe" }]);
        var dialog = new EditDialog(new AddProfileDialogResult("Repaired", "repaired.exe"));
        var manager = new ProfileManager(store);
        var vm = new MainViewModel(manager, dialog, new FakeDisplayService(), new RecordingColorControlService());
        await vm.InitializeAsync();
        vm.SelectedProfile = vm.Profiles.Single(p => ReferenceEquals(p.Model, profile));

        await vm.ModifyProfileCommand.ExecuteAsync(null);
        await vm.FlushPendingSavesAsync();

        Assert.Empty(dialog.Errors);
        Assert.Equal("Repaired", profile.Name);
        Assert.Equal("repaired.exe", profile.Executable);
        Assert.Equal("original.ini", profile.SourcePath);
        Assert.Equal("Repaired", store.SavedProfiles[^1].Name);
        Assert.Equal("repaired.exe", store.SavedProfiles[^1].Executable);
    }

    [Fact]
    public async Task UpdateProfileIdentity_SaveFailure_PreservesOriginalIdentity()
    {
        var store = new InMemoryProfileStore();
        var profile = new Profile { Name = "Old", Executable = "invalid.txt", SourcePath = "original.ini" };
        store.Profiles.Add(profile);
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();
        store.SaveException = new IOException("write denied");

        await Assert.ThrowsAsync<IOException>(() => manager.UpdateProfileIdentityAsync(profile, "Repaired", "repaired.exe"));

        Assert.Equal("Old", profile.Name);
        Assert.Equal("invalid.txt", profile.Executable);
        Assert.Equal("original.ini", profile.SourcePath);
    }

    [Fact]
    public async Task SaveProfileSnapshot_AfterIdentityRepair_PreservesRepairedIdentityAndCapturedFeatures()
    {
        var store = new InMemoryProfileStore();
        var profile = new Profile { Name = "Old", Executable = "invalid.txt" };
        store.Profiles.Add(profile);
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();
        var snapshot = ProfilePersistenceSnapshot.Create(profile);
        snapshot.IsEnabled = false;
        await manager.UpdateProfileIdentityAsync(profile, "Repaired", "repaired.exe");

        await manager.SaveProfileSnapshotAsync(profile, snapshot);

        Assert.Equal("Repaired", store.SavedProfiles[^1].Name);
        Assert.Equal("repaired.exe", store.SavedProfiles[^1].Executable);
        Assert.False(store.SavedProfiles[^1].IsEnabled);
        Assert.True(profile.IsEnabled);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("rename")]
    public async Task ProfileCrud_StorageDoesNotRunOnCallingThread(string operation)
    {
        var store = new ThreadRecordingStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();
        var profile = manager.Profiles.Single(p => !p.IsWindowsProfile);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            try
            {
                store.CallerThread = Environment.CurrentManagedThreadId;
                var task = operation switch
                {
                    "add" => manager.AddProfileAsync("New", "new.exe"),
                    "remove" => manager.RemoveProfileAsync(profile),
                    _ => manager.RenameProfileAsync(profile, "Renamed")
                };
                task.GetAwaiter().GetResult();
                finished.SetResult();
            }
            catch (Exception ex) { finished.SetException(ex); }
        });
        caller.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(store.CallerThread, store.StorageThread);
    }

    private sealed class ThreadRecordingStore : IProfileStore
    {
        public int CallerThread;
        public int StorageThread;
        public Task<IReadOnlyList<Profile>> LoadProfilesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Profile>>([new Profile { Name = "Old", Executable = "old.exe" }]);
        public Task SaveProfileAsync(Profile profile, CancellationToken cancellationToken)
        {
            StorageThread = Environment.CurrentManagedThreadId;
            return Task.CompletedTask;
        }
        public Task DeleteProfileAsync(Profile profile, CancellationToken cancellationToken)
        {
            StorageThread = Environment.CurrentManagedThreadId;
            return Task.CompletedTask;
        }
    }

    private sealed class FailedStartupRestoration(bool throws) : IStartupService
    {
        public List<(bool, bool)> Calls { get; } = [];
        public StartupState GetState() => new(true, true);
        public bool Apply(bool startWithWindows, bool startAsAdmin, out string? errorMessage)
        {
            Calls.Add((startWithWindows, startAsAdmin));
            if (throws) throw new IOException("restoration denied");
            errorMessage = "restoration denied";
            return false;
        }
    }

    private sealed class EditDialog(AddProfileDialogResult result) : IDialogService
    {
        private bool _returned;
        public List<string> Errors { get; } = [];
        public AddProfileDialogResult? ShowEditProfileDialog(string profileName, string executableName)
        {
            if (_returned) return null;
            _returned = true;
            return result;
        }
        public AddProfileDialogResult? ShowAddProfileDialog(string? profileName = null, string? executableName = null) => null;
        public string? ShowOpenFileDialog(string title, string filter, string? initialPath = null) => null;
        public bool ShowRemoveProfileConfirmation(string profileName) => true;
        public void ShowError(string message, string title) => Errors.Add(message);
    }
}
