using System.IO;
using System.Linq;
using System.Threading.Tasks;
using sWinShortcuts.Services;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

// Confirmation gate for Remove: declining leaves the profile untouched, accepting proceeds to the
// durable delete, Shift held at invocation bypasses the dialog entirely, and a confirmed-but-failed
// delete still surfaces (F-015) without losing the profile. The Shift seam is injected so the
// modifier state is pinned deterministically instead of read from the test host's keyboard.
public class MainViewModelRemoveProfileTests
{
    private static async Task<(MainViewModel vm, InMemoryProfileStore store, ProfileManager manager, FakeDialogService dialog)> BuildWithProfileAsync(bool shiftDown)
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        var dialog = new FakeDialogService();
        var vm = new MainViewModel(
            manager,
            dialog,
            new FakeDisplayService(),
            new RecordingColorControlService(),
            removeBypassModifierDown: () => shiftDown);
        await vm.InitializeAsync();
        await manager.AddProfileAsync("Alpha", "alpha.exe");
        return (vm, store, manager, dialog);
    }

    [Fact]
    public async Task Remove_ConfirmDeclined_KeepsProfile()
    {
        var (vm, store, manager, dialog) = await BuildWithProfileAsync(shiftDown: false);
        dialog.ConfirmRemoveProfileResult = false;

        await vm.RemoveProfileCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ConfirmRemoveProfileCount);
        Assert.Equal("Alpha", dialog.LastConfirmRemoveProfileName);
        Assert.False(store.WasDeleted("Alpha"));
        Assert.Contains(vm.Profiles, p => p.Name == "Alpha");
        Assert.Contains(manager.Profiles, p => p.Name == "Alpha");
    }

    [Fact]
    public async Task Remove_ConfirmAccepted_RemovesProfile()
    {
        var (vm, store, manager, dialog) = await BuildWithProfileAsync(shiftDown: false);

        await vm.RemoveProfileCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ConfirmRemoveProfileCount);
        Assert.True(store.WasDeleted("Alpha"));
        Assert.DoesNotContain(vm.Profiles, p => p.Name == "Alpha");
        Assert.DoesNotContain(manager.Profiles, p => p.Name == "Alpha");
    }

    [Fact]
    public async Task Remove_ShiftHeld_SkipsConfirmationAndRemoves()
    {
        var (vm, store, manager, dialog) = await BuildWithProfileAsync(shiftDown: true);

        await vm.RemoveProfileCommand.ExecuteAsync(null);

        Assert.Equal(0, dialog.ConfirmRemoveProfileCount);
        Assert.True(store.WasDeleted("Alpha"));
        Assert.DoesNotContain(vm.Profiles, p => p.Name == "Alpha");
        Assert.DoesNotContain(manager.Profiles, p => p.Name == "Alpha");
    }

    [Fact]
    public async Task Remove_ConfirmedButDeleteFails_ShowsErrorAndKeepsProfile()
    {
        var (vm, store, manager, dialog) = await BuildWithProfileAsync(shiftDown: false);
        store.DeleteException = new IOException("locked");

        await vm.RemoveProfileCommand.ExecuteAsync(null);

        Assert.Equal(1, dialog.ConfirmRemoveProfileCount); // gate ran, then the durable delete failed
        Assert.True(dialog.ErrorCount >= 1);               // F-015: surfaced, not swallowed
        Assert.False(store.WasDeleted("Alpha"));
        Assert.Contains(vm.Profiles, p => p.Name == "Alpha");
        Assert.Contains(manager.Profiles, p => p.Name == "Alpha");
    }
}
