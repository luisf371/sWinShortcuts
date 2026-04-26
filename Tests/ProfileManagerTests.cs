using Xunit;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;

namespace Tests;

public class ProfileManagerTests
{
    [Fact]
    public async Task InitializeAsync_CreatesWindowsAndColorProfiles_WhenStoreIsEmpty()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);

        await manager.InitializeAsync();

        Assert.Equal(2, manager.Profiles.Count);
        Assert.NotNull(manager.WindowsProfile);
        Assert.NotNull(manager.ColorProfile);
        Assert.True(manager.WindowsProfile.IsWindowsProfile);
        Assert.True(manager.ColorProfile.IsColorProfile);
    }

    [Fact]
    public async Task InitializeAsync_PreservesExistingProfiles()
    {
        var store = new InMemoryProfileStore();
        var existingProfile = ProfileFactory.CreateCustomProfile("MyGame", "game.exe");
        store.Profiles.Add(existingProfile);

        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        Assert.Equal(3, manager.Profiles.Count); // Windows + Color + MyGame
        Assert.Contains(manager.Profiles, p => p.Name == "MyGame");
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);

        await manager.InitializeAsync();
        var countAfterFirst = manager.Profiles.Count;

        await manager.InitializeAsync();
        var countAfterSecond = manager.Profiles.Count;

        Assert.Equal(countAfterFirst, countAfterSecond);
    }

    [Fact]
    public async Task AddProfileAsync_AddsNewProfile()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        var profile = await manager.AddProfileAsync("TestGame", "test.exe");

        Assert.Contains(profile, manager.Profiles);
        Assert.Equal("TestGame", profile.Name);
        Assert.Equal("test", profile.NormalizedExecutable);
    }

    [Fact]
    public async Task AddProfileAsync_RaisesProfileAddedEvent()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        Profile? addedProfile = null;
        manager.ProfileAdded += (_, p) => addedProfile = p;

        var profile = await manager.AddProfileAsync("TestGame", "test.exe");

        Assert.Same(profile, addedProfile);
    }

    [Fact]
    public async Task AddProfileAsync_DuplicateName_ThrowsInvalidOperationException()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        await manager.AddProfileAsync("TestGame", "test.exe");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.AddProfileAsync("TestGame", "other.exe"));
        
        Assert.Contains("TestGame", ex.Message);
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public async Task AddProfileAsync_DuplicateExecutable_ThrowsInvalidOperationException()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        await manager.AddProfileAsync("Game1", "game.exe");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.AddProfileAsync("Game2", "game.exe"));
        
        Assert.Contains("game.exe", ex.Message);
    }

    [Fact]
    public async Task AddProfileAsync_NormalizesExecutablePath()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        await manager.AddProfileAsync("Game1", @"C:\Games\MyGame.exe");

        // Same normalized name should throw
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.AddProfileAsync("Game2", "mygame.exe"));
    }

    [Fact]
    public async Task RemoveProfileAsync_RemovesProfile()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        var profile = await manager.AddProfileAsync("TestGame", "test.exe");
        await manager.RemoveProfileAsync(profile);

        Assert.DoesNotContain(profile, manager.Profiles);
    }

    [Fact]
    public async Task RemoveProfileAsync_RaisesProfileRemovedEvent()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        Profile? removedProfile = null;
        manager.ProfileRemoved += (_, p) => removedProfile = p;

        var profile = await manager.AddProfileAsync("TestGame", "test.exe");
        await manager.RemoveProfileAsync(profile);

        Assert.Same(profile, removedProfile);
    }

    [Fact]
    public async Task RemoveProfileAsync_WindowsProfile_ThrowsInvalidOperationException()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.RemoveProfileAsync(manager.WindowsProfile));
        
        Assert.Contains("Windows", ex.Message);
    }

    [Fact]
    public async Task RemoveProfileAsync_ColorProfile_ThrowsInvalidOperationException()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.RemoveProfileAsync(manager.ColorProfile));
        
        Assert.Contains("Color", ex.Message);
    }

    [Fact]
    public async Task RemoveProfileAsync_DeletesFromStore()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        var profile = await manager.AddProfileAsync("TestGame", "test.exe");
        await manager.RemoveProfileAsync(profile);

        Assert.True(store.WasDeleted("TestGame"));
    }

    [Fact]
    public async Task FindByExecutable_ReturnsMatchingProfile()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        var profile = await manager.AddProfileAsync("TestGame", "test.exe");
        
        var found = manager.FindByExecutable("test.exe");

        Assert.Same(profile, found);
    }

    [Fact]
    public async Task FindByExecutable_IsCaseInsensitive()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        var profile = await manager.AddProfileAsync("TestGame", "Test.exe");
        
        var found = manager.FindByExecutable("TEST.EXE");

        Assert.Same(profile, found);
    }

    [Fact]
    public async Task FindByExecutable_MatchesWithoutExtension()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        var profile = await manager.AddProfileAsync("TestGame", "test.exe");
        
        // Searching with full path should still match
        var found = manager.FindByExecutable(@"C:\Games\test.exe");

        Assert.Same(profile, found);
    }

    [Fact]
    public async Task FindByExecutable_ReturnsNull_WhenNotFound()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        var found = manager.FindByExecutable("nonexistent.exe");

        Assert.Null(found);
    }

    [Fact]
    public async Task SaveProfileAsync_PersistsToStore()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        var profile = await manager.AddProfileAsync("TestGame", "test.exe");
        profile.IsEnabled = false;
        
        await manager.SaveProfileAsync(profile);

        Assert.Contains(store.Profiles, p => p.Name == "TestGame" && !p.IsEnabled);
    }

    [Fact]
    public async Task SaveProfileAsync_UnmanagedProfile_ThrowsInvalidOperationException()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        var orphanProfile = ProfileFactory.CreateCustomProfile("Orphan", "orphan.exe");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.SaveProfileAsync(orphanProfile));
    }

    [Fact]
    public async Task AddProfileAsync_ThrowsOnNullOrWhitespaceName()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.AddProfileAsync("", "test.exe"));
        
        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.AddProfileAsync("   ", "test.exe"));
    }

    [Fact]
    public async Task AddProfileAsync_ThrowsOnNullOrWhitespaceExecutable()
    {
        var store = new InMemoryProfileStore();
        var manager = new ProfileManager(store);
        await manager.InitializeAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.AddProfileAsync("Test", ""));
        
        await Assert.ThrowsAsync<ArgumentException>(
            () => manager.AddProfileAsync("Test", "   "));
    }
}
