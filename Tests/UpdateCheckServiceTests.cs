using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using sWinShortcuts.Services;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;

namespace Tests;

// UpdateCheckService: the pure parse/compare core is tested directly; the service-level tests use
// the internal seam ctor (inline enqueue, canned fetch, temp-dir settings path).
//
// CI-PIN RULE — every service-level test sets CurrentBuildNumber explicitly. The CI gate builds this
// test project with -p:BuildNumber=<run number> (ci.yml), and that MSBuild global property propagates
// to the referenced app project, so the ambient BuildInfo.Number is NUMERIC in the CI test host but
// "dev" locally. An unpinned test would pass in one host and pass vacuously (or fail) in the other.
// Convention: tests expecting >=1 fetch pin "42"; the dev-build test pins "dev".
public sealed class UpdateCheckServiceTests
{
    private const string NewerBuildPayload = "{\"tag_name\":\"build-99\"}";

    // ---- pure helpers ----

    [Theory]
    [InlineData("build-42", 42)]
    [InlineData("Build-7", 7)]      // prefix is OrdinalIgnoreCase
    [InlineData(" build-42 ", 42)]  // surrounding whitespace is trimmed
    public void ParseBuildNumber_BuildTag_ReturnsNumber(string tagName, int expected)
    {
        Assert.Equal(expected, UpdateCheckService.ParseBuildNumber(tagName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1.2.3")]
    [InlineData("build-x")]
    [InlineData("build-")]
    [InlineData("42")]
    public void ParseBuildNumber_NonBuildFormat_ReturnsNull(string? tagName)
    {
        Assert.Null(UpdateCheckService.ParseBuildNumber(tagName));
    }

    [Fact]
    public void ParseLatestTag_ValidGithubPayload_ReturnsTagName()
    {
        // Trimmed real releases/latest payload shape (irrelevant sibling members included).
        var json = "{\"url\":\"https://api.github.com/repos/luisf371/sWinShortcuts/releases/1\","
            + "\"tag_name\":\"build-42\","
            + "\"html_url\":\"https://github.com/luisf371/sWinShortcuts/releases/tag/build-42\"}";

        Assert.Equal("build-42", UpdateCheckService.ParseLatestTag(json));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"tag_name\":\"build-42\"")] // truncated JSON
    public void ParseLatestTag_MalformedJson_ReturnsNull(string json)
    {
        Assert.Null(UpdateCheckService.ParseLatestTag(json));
    }

    [Theory]
    [InlineData("{\"name\":\"v1\",\"html_url\":\"https://github.com/x\"}")] // no tag_name
    [InlineData("{\"tag_name\":42}")]                                        // tag_name not a string
    public void ParseLatestTag_MissingTagName_ReturnsNull(string json)
    {
        Assert.Null(UpdateCheckService.ParseLatestTag(json));
    }

    [Fact]
    public void ShouldNotify_LatestGreaterThanCurrent_True()
    {
        Assert.True(UpdateCheckService.ShouldNotify(99, "42"));
    }

    [Theory]
    [InlineData(42, "42")] // equal
    [InlineData(7, "42")]  // older
    public void ShouldNotify_EqualOrOlder_ReturnsFalse(int latest, string current)
    {
        Assert.False(UpdateCheckService.ShouldNotify(latest, current));
    }

    [Fact]
    public void ShouldNotify_CurrentNotNumeric_ReturnsFalse()
    {
        Assert.False(UpdateCheckService.ShouldNotify(99, "dev"));
    }

    [Fact]
    public void ShouldNotify_NullLatest_ReturnsFalse()
    {
        Assert.False(UpdateCheckService.ShouldNotify(null, "42"));
    }

    // ---- service level (seam ctor; CurrentBuildNumber always pinned per the CI-pin rule) ----

    [Fact]
    public async Task CheckAsync_NewerBuild_NotifiesViewModel()
    {
        var vm = BuildViewModel();
        var fetch = new FakeFetch(NewerBuildPayload);
        var service = BuildService(vm, fetch, MakeSettingsPath(), currentBuild: "42");

        await service.CheckAsync();

        Assert.True(vm.UpdateAvailable);
        Assert.True(vm.ShowUpdateBanner);
        Assert.Contains("99", vm.UpdateBannerText);
        Assert.Contains("42", vm.UpdateBannerText);
    }

    [Fact]
    public async Task CheckAsync_Disabled_DoesNotFetch()
    {
        var vm = BuildViewModel();
        var fetch = new FakeFetch(NewerBuildPayload);
        // Numeric build, so this exercises the !Enabled early-out and not the dev-build guard.
        var service = BuildService(vm, fetch, MakeSettingsPath(), currentBuild: "42");
        service.Enabled = false;

        await service.CheckAsync();

        Assert.Equal(0, fetch.CallCount);
        Assert.False(vm.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_DevBuild_DoesNotFetch()
    {
        var vm = BuildViewModel();
        var fetch = new FakeFetch(NewerBuildPayload);
        // Pinned "dev" (never the ambient stamp): proves the guard precedes the fetch entirely.
        var service = BuildService(vm, fetch, MakeSettingsPath(), currentBuild: "dev");

        await service.CheckAsync();

        Assert.Equal(0, fetch.CallCount);
        Assert.False(vm.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_FetchThrows_DoesNotThrowAndDoesNotNotify()
    {
        var vm = BuildViewModel();
        var fetch = new FakeFetch(_ => throw new InvalidOperationException("offline"));
        var service = BuildService(vm, fetch, MakeSettingsPath(), currentBuild: "42");

        // D5: any fetch failure is swallowed by the service itself — an escaped exception here
        // fails the test directly, which is exactly the "does not throw" assertion.
        await service.CheckAsync();

        Assert.Equal(1, fetch.CallCount);
        Assert.False(vm.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_UnparseableTag_DoesNotNotify()
    {
        var vm = BuildViewModel();
        var fetch = new FakeFetch("{\"tag_name\":\"v1.0\"}");
        var service = BuildService(vm, fetch, MakeSettingsPath(), currentBuild: "42");

        await service.CheckAsync();

        Assert.Equal(1, fetch.CallCount);
        Assert.False(vm.UpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_InFlightSecondCall_SkipsFetch()
    {
        var vm = BuildViewModel();
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var calls = 0;

        async Task<string?> Gated(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            entered.SetResult();
            await release.Task;
            return NewerBuildPayload;
        }

        var service = new UpdateCheckService(vm, new NullLoggerService(), enqueue: a => a(), fetch: Gated,
            settingsPath: MakeSettingsPath())
        {
            Enabled = true,
            CurrentBuildNumber = "42",
        };

        var first = service.CheckAsync();
        await entered.Task;                 // the first check is now parked inside its fetch
        var second = service.CheckAsync();  // single-flight: must complete without touching fetch
        await second;

        Assert.Equal(1, Volatile.Read(ref calls));

        release.SetResult();
        await first;

        Assert.Equal(1, calls);             // exactly one fetch total
        Assert.True(vm.UpdateAvailable);    // the surviving fetch delivered its result
    }

    [Fact]
    public void Start_MissingOrDisabledKey_DoesNotFetch()
    {
        var vm = BuildViewModel();
        var fetch = new FakeFetch(NewerBuildPayload);
        var service = BuildService(vm, fetch, MakeSettingsPath(), currentBuild: "42");
        service.InitialDelay = TimeSpan.Zero;

        service.Start(); // no INI file in the temp dir -> absent key -> disabled

        Assert.False(service.Enabled);
        Assert.Equal(0, fetch.CallCount);
        Assert.False(vm.UpdateAvailable);
    }

    [Fact]
    public async Task Start_EnabledKey_FetchesAfterDelay()
    {
        var root = Path.Combine(Path.GetTempPath(), "sWinShortcutsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "sWinShortcuts.ini");
            File.WriteAllText(settingsPath, "[App]\r\nCheckForUpdates=true\r\n");

            var vm = BuildViewModel();
            var fetch = new FakeFetch(NewerBuildPayload);
            var service = BuildService(vm, fetch, settingsPath, currentBuild: "42");
            service.InitialDelay = TimeSpan.Zero;

            service.Start();

            // Deterministic wait on observable outcome (no bare Task.Delay): the delayed check has
            // run to completion — exactly one fetch, banner state applied via the enqueue seam.
            await WaitForAsync(() => vm.UpdateAvailable);
            Assert.True(vm.ShowUpdateBanner);
            Assert.Equal(1, fetch.CallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- view model ----

    [Fact]
    public void DismissUpdateBanner_HidesBannerWithoutClearingAvailability()
    {
        var vm = BuildViewModel();
        vm.NotifyUpdateAvailable(99, 42);

        Assert.True(vm.UpdateAvailable);
        Assert.True(vm.ShowUpdateBanner);

        vm.DismissUpdateBannerCommand.Execute(null);

        Assert.True(vm.UpdateAvailable);
        Assert.False(vm.ShowUpdateBanner);

        // ACCEPTANCE (D7): dismissal is session-sticky. A second notify that finds another build
        // must NOT re-show the banner — NotifyUpdateAvailable never resets UpdateBannerDismissed;
        // only a process restart clears it.
        vm.NotifyUpdateAvailable(100, 42);
        Assert.True(vm.UpdateAvailable);
        Assert.True(vm.UpdateBannerDismissed);
        Assert.False(vm.ShowUpdateBanner);
    }

    // ---- helpers ----

    private static MainViewModel BuildViewModel()
        => new(new ProfileManager(new InMemoryProfileStore()), new FakeDialogService(),
            new FakeDisplayService(), new RecordingColorControlService());

    private static UpdateCheckService BuildService(MainViewModel vm, FakeFetch fetch, string settingsPath,
        string currentBuild)
        => new(vm, new NullLoggerService(), enqueue: a => a(), fetch: fetch.Invoke, settingsPath: settingsPath)
        {
            Enabled = true,
            CurrentBuildNumber = currentBuild,
        };

    // A settings path in a temp dir that is NEVER created: IniDocument.Load no-ops on a missing
    // file and the service only reads it, so no directory is made and nothing needs cleaning up.
    private static string MakeSettingsPath()
        => Path.Combine(Path.GetTempPath(), "sWinShortcutsTests_" + Guid.NewGuid().ToString("N"),
            "sWinShortcuts.ini");

    /// <summary>Fetch fake: counts calls, returns a canned payload (or throws / gates via impl).</summary>
    private sealed class FakeFetch
    {
        private readonly Func<CancellationToken, Task<string?>> _impl;
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public FakeFetch(string? payload)
            => _impl = _ => Task.FromResult(payload);

        public FakeFetch(Func<CancellationToken, Task<string?>> impl)
            => _impl = impl;

        public Task<string?> Invoke(CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            return _impl(ct);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for the update-check condition.");
            }

            await Task.Delay(10);
        }
    }
}
