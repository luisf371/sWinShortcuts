using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using sWinShortcuts.Services;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

// F-014: deterministic coverage of the coalesced-save machinery using a TaskCompletionSource-gated store
// (no flaky sleeps) — flush awaits in-flight saves, detach-during-save drops the profile, a persistent
// failure is reported (not a false success), and manual Save is coordinated.
public class MainViewModelSaveTests
{
    private static async Task<(MainViewModel vm, InMemoryProfileStore store, ProfileManager manager, FakeDialogService dialog, ProfileViewModel profileVm)> BuildWithProfileAsync()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        var dialog = new FakeDialogService();
        var vm = new MainViewModel(manager, dialog, new FakeDisplayService(), new RecordingColorControlService());
        await vm.InitializeAsync();
        var added = await manager.AddProfileAsync("Alpha", "alpha.exe");
        var profileVm = vm.Profiles.Single(p => ReferenceEquals(p.Model, added));
        return (vm, store, manager, dialog, profileVm);
    }

    [Fact]
    public async Task Flush_AwaitsInFlightSave()
    {
        var (vm, store, _, _, profileVm) = await BuildWithProfileAsync();
        store.SaveGate = new TaskCompletionSource();
        profileVm.IsEnabled = !profileVm.IsEnabled; // dirty

        var flushTask = vm.FlushPendingSavesAsync();
        Assert.False(flushTask.IsCompleted); // must be awaiting the gated in-flight save, not exiting early

        store.SaveGate.SetResult();
        var unsaved = await flushTask;
        Assert.Equal(0, unsaved);
    }

    [Fact]
    public async Task DetachDuringSave_PostSaveGuard_DoesNotRequeue()
    {
        var (vm, store, _, _, profileVm) = await BuildWithProfileAsync();
        store.SaveEntered = new TaskCompletionSource();
        store.SaveGate = new TaskCompletionSource();
        store.SaveException = new IOException("locked"); // save fails → would requeue WITHOUT the guard
        profileVm.IsEnabled = !profileVm.IsEnabled; // dirty

        var flushTask = vm.FlushPendingSavesAsync();
        await store.SaveEntered.Task; // the loop has removed the VM from _dirty and is gated in the store I/O

        // Mark the VM detached directly (DetachProfile via RemoveProfileAsync would deadlock on the manager
        // gate the in-flight save holds — production removes are fast) so the POST-SAVE requeue guard fires.
        profileVm.IsDetached = true;

        store.SaveGate.SetResult(); // release → save fails → post-save guard sees IsDetached → no requeue
        var unsaved = await flushTask;

        Assert.Equal(0, unsaved); // detached profile NOT requeued into _dirty
    }

    [Fact]
    public async Task Flush_PersistentFailure_ReportsUnsavedCount()
    {
        var (vm, store, _, dialog, profileVm) = await BuildWithProfileAsync();
        store.SaveException = new InvalidOperationException("permanent"); // non-transient → no retry latency
        profileVm.IsEnabled = !profileVm.IsEnabled; // dirty

        var unsaved = await vm.FlushPendingSavesAsync();

        Assert.Equal(1, unsaved);          // could not persist → reported, not a false success
        Assert.True(dialog.ErrorCount >= 1); // surfaced once
    }

    [Fact]
    public async Task ManualSave_Coordinates_AndClearsDirty()
    {
        var (vm, store, _, _, profileVm) = await BuildWithProfileAsync();
        var before = store.SaveCount;
        profileVm.IsEnabled = !profileVm.IsEnabled; // dirty
        vm.SelectedProfile = profileVm;

        await vm.SaveProfileCommand.ExecuteAsync(null);

        Assert.True(store.SaveCount > before);        // manual save persisted
        var unsaved = await vm.FlushPendingSavesAsync();
        Assert.Equal(0, unsaved);                      // dirty cleared by the coordinated save
    }

    [Fact]
    public async Task EditDuringSave_Coalesces_ToOneFollowUp()
    {
        var (vm, store, _, _, profileVm) = await BuildWithProfileAsync();
        var before = store.SaveCount;
        var savedProfilesBefore = store.SavedProfiles.Count;
        store.SaveEntered = new TaskCompletionSource();
        store.SaveGate = new TaskCompletionSource();
        profileVm.IsEnabled = false; // dirty #1 snapshot

        var flushTask = vm.FlushPendingSavesAsync();
        await store.SaveEntered.Task; // iter 1 has removed dirty #1 and is gated in the store I/O

        profileVm.IsEnabled = true; // dirty #2 during the gated save → re-adds to _dirty

        store.SaveGate.SetResult();
        var unsaved = await flushTask;

        Assert.Equal(0, unsaved);
        Assert.Equal(before + 2, store.SaveCount); // one active save + exactly one coalesced follow-up
        var saved = store.SavedProfiles.Skip(savedProfilesBefore).ToArray();
        Assert.Collection(
            saved,
            first => Assert.False(first.IsEnabled),
            second => Assert.True(second.IsEnabled));
        Assert.All(saved, snapshot => Assert.NotSame(profileVm.Model, snapshot));
    }

    [Fact]
    public async Task QueueAutoSave_RejectsDetachedProfile()
    {
        var (vm, _, _, _, profileVm) = await BuildWithProfileAsync();
        profileVm.IsDetached = true; // detached: a still-wired change callback must not re-queue it

        profileVm.IsEnabled = !profileVm.IsEnabled; // edit → QueueAutoSave must reject under the lock

        var unsaved = await vm.FlushPendingSavesAsync();
        Assert.Equal(0, unsaved); // nothing was queued for a detached profile
    }

    [Fact]
    public async Task NewEditAfterSaveCompletes_PersistsViaFreshLoop()
    {
        var (vm, store, _, _, profileVm) = await BuildWithProfileAsync();

        profileVm.IsEnabled = !profileVm.IsEnabled;           // dirty #1
        Assert.Equal(0, await vm.FlushPendingSavesAsync());   // save-1 persisted; the loop deregistered

        // codex-final #1: an edit landing AFTER the loop deregistered must persist via a FRESH loop, never
        // stranded. The fix makes deregistration atomic with the empty-dirty check so this can't be lost.
        var before = store.SaveCount;
        profileVm.IsEnabled = !profileVm.IsEnabled;           // dirty #2 (after deregister)
        Assert.Equal(0, await vm.FlushPendingSavesAsync());
        Assert.True(store.SaveCount > before);                // the post-deregister edit WAS persisted
    }

    [Fact]
    public async Task SaveLoopFault_RestoresDirty_NotSilentlyLost()
    {
        var (vm, store, _, dialog, profileVm) = await BuildWithProfileAsync();
        store.SaveException = new InvalidOperationException("permanent"); // non-transient → surfaces a dialog
        dialog.ThrowOnError = true; // the error dialog itself throws → the loop's fault path must restore dirty
        profileVm.IsEnabled = !profileVm.IsEnabled; // dirty

        var unsaved = await vm.FlushPendingSavesAsync();

        Assert.Equal(1, unsaved); // dirty restored despite the fault; NOT silently reported as 0
    }
}
