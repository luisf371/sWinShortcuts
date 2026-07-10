using System.IO;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public class ProfileManagerRegressionTests
{
    [Fact]
    public async Task RemoveProfileAsync_DeleteFails_KeepsProfileManaged_AndNoRemovedEvent()
    {
        // F-015: a failed durable delete must leave the profile managed (delete-first ordering) and NOT
        // raise ProfileRemoved — the UI/manager stay consistent and a restart can't resurrect it.
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();
        var added = await manager.AddProfileAsync("Alpha", "alpha.exe");

        var removedRaised = false;
        manager.ProfileRemoved += (_, _) => removedRaised = true;
        store.DeleteException = new IOException("locked");

        await Assert.ThrowsAsync<IOException>(() => manager.RemoveProfileAsync(added));

        Assert.Contains(manager.Profiles, p => ReferenceEquals(p, added)); // still managed
        Assert.False(removedRaised);
    }

    [Fact]
    public async Task Initialize_ReservedCustomName_IsReclassifiedCustom_AndSuffixed()
    {
        // F-007: a custom profile whose Name collides with a reserved built-in name stays Kind=Custom
        // (never reclassified) and is deterministically suffixed; the real built-in remains the one the
        // WindowsProfile accessor returns.
        var store = new InMemoryProfileStore();
        store.Profiles.Add(new Profile
        {
            Name = ProfileConstants.WindowsProfileName,
            Executable = "reserved.exe",
            SourcePath = @"C:\p\reserved.ini"
        });

        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        Assert.Equal(ProfileKind.Windows, manager.WindowsProfile.Kind);
        var custom = manager.Profiles.Single(p => p.Kind == ProfileKind.Custom);
        Assert.Equal("Windows (2)", custom.Name);
        Assert.False(ReferenceEquals(custom, manager.WindowsProfile));
    }

    [Fact]
    public async Task SaveProfileAsync_RejectsEmptyAndDuplicateExecutable()
    {
        // F-017: executable validation is centralized in the manager, so every save path is protected.
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();
        await manager.AddProfileAsync("Alpha", "alpha.exe");
        var b = await manager.AddProfileAsync("Beta", "beta.exe");

        b.Executable = "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveProfileAsync(b));

        b.Executable = "alpha.exe"; // duplicate of Alpha's normalized executable
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveProfileAsync(b));
    }

    [Fact]
    public async Task Initialize_ReservedColorName_IsReclassifiedCustom_AndSuffixed()
    {
        // F-007: the OTHER reserved name gets the same treatment as "Windows".
        var store = new InMemoryProfileStore();
        store.Profiles.Add(new Profile
        {
            Name = ProfileConstants.ColorProfileName,
            Executable = "reservedcolor.exe",
            SourcePath = @"C:\p\reservedcolor.ini"
        });

        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        Assert.Equal(ProfileKind.Color, manager.ColorProfile.Kind);
        var custom = manager.Profiles.Single(p => p.Kind == ProfileKind.Custom);
        Assert.Equal("Color Settings (2)", custom.Name);
    }

    [Fact]
    public async Task Executable_ExePolicy_RejectsNonExe_AcceptsPathQualified()
    {
        // F-017: the centralized validation enforces the .exe policy on every custom-profile save path.
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();
        var a = await manager.AddProfileAsync("Alpha", "alpha.exe");

        // AddProfileAsync also enforces .exe.
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AddProfileAsync("Bad", "notepad"));

        a.Executable = "notepad"; // non-.exe
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.SaveProfileAsync(a));

        a.Executable = @"C:\Games\Some Folder\game.exe"; // path-qualified .exe is fine
        await manager.SaveProfileAsync(a);
    }

    [Fact]
    public async Task RenameProfileAsync_ValidatesExecutable_AndDoesNotMutateNameOnFailure()
    {
        // F-017 (codex #9): rename persists the whole profile, so a legacy/invalid executable must be
        // rejected on the rename path too, and the name must not be mutated on validation failure.
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();
        var a = await manager.AddProfileAsync("Alpha", "alpha.exe");

        a.Executable = "notepad"; // non-.exe (simulate legacy/invalid)
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RenameProfileAsync(a, "Renamed"));
        Assert.Equal("Alpha", a.Name);
    }

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
