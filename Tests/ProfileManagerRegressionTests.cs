using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public class ProfileManagerRegressionTests
{
    [Fact]
    public async Task FindByExecutable_DottedProcessName_MatchesDottedExe()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();
        await manager.AddProfileAsync("Paint", "paint.net.exe");

        // S1: ForegroundWatcher yields the already-extensionless process name "paint.net".
        var match = manager.FindByExecutable("paint.net");

        Assert.NotNull(match);
        Assert.Equal("Paint", match!.Name);
    }

    [Fact]
    public async Task RenameProfileAsync_KeepsSameInstance_AndRejectsBuiltIns()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();
        var added = await manager.AddProfileAsync("Alpha", "alpha.exe");

        await manager.RenameProfileAsync(added, "Beta");

        Assert.Equal("Beta", added.Name);                       // same instance mutated
        Assert.Contains(manager.Profiles, p => ReferenceEquals(p, added));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.RenameProfileAsync(manager.WindowsProfile, "NotAllowed"));
    }

    [Fact]
    public async Task RenameProfileAsync_DuplicateName_Throws()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();
        var a = await manager.AddProfileAsync("Alpha", "alpha.exe");
        await manager.AddProfileAsync("Beta", "beta.exe");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.RenameProfileAsync(a, "Beta"));
    }

    [Fact]
    public async Task Initialize_DuplicateNamesOnDisk_AreDeduplicated_AndNoneDropped()
    {
        var store = new InMemoryProfileStore();
        store.Profiles.Add(new Profile { Name = "Foo", SourcePath = @"C:\p\a.ini" });
        store.Profiles.Add(new Profile { Name = "Foo", SourcePath = @"C:\p\b.ini" });

        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        var customNames = manager.Profiles
            .Where(p => !p.IsWindowsProfile && !p.IsColorProfile)
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToList();

        // P3: both survive on their own files; only the colliding display name is suffixed.
        Assert.Equal(2, customNames.Count);
        Assert.Contains("Foo", customNames);
        Assert.Contains("Foo (2)", customNames);
    }
}
