using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using sWinShortcuts.Utilities;
using sWinShortcuts.ViewModels;

namespace sWinShortcuts.Services;

// Optional GitHub update check: compares this build's CI run number (BuildInfo.Number, which IS
// the "build-N" release tag) against the latest published GitHub release. The only network client
// in the app; every failure path is silent (log only — offline, rate-limited, malformed, "dev").
//
// Dispatch contract (same as RapidFireStatusService): the fetch runs on the thread pool and the
// result is applied to MainViewModel ONLY through _enqueue (Dispatcher.BeginInvoke in production,
// inline in tests) — never inline from the pool, never from a hook/dispatcher-blocking path.
public sealed class UpdateCheckService
{
    private const int CheckTimeoutSeconds = 10;

    private readonly ILoggerService _logger;
    private readonly MainViewModel _viewModel;   // touched ONLY via _enqueue
    // Captured in the ctor, which DI builds on the UI thread. Null (headless tests) makes the
    // production enqueue a no-op.
    private readonly Dispatcher? _dispatcher;
    private readonly Action<Action> _enqueue;    // test seam (BeginInvoke(Render) in production)
    private readonly Func<CancellationToken, Task<string?>>? _fetch; // test seam (canned JSON/exceptions)
    private readonly string _settingsPath;

    private HttpClient? _client;
    private int _checkInFlight;                  // single-flight (Interlocked CAS)
    private volatile bool _enabled;

    // Test seams. InitialDelay is the post-Show() grace before the first startup check.
    internal TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(5);

    // Ambient BuildInfo.Number is "dev" locally but NUMERIC in the CI test host (ci.yml builds the
    // test project with -p:BuildNumber=<run number>, and the global property propagates to the app
    // project) — so every test MUST pin this explicitly; no test may rely on the ambient stamp.
    internal string CurrentBuildNumber { get; set; } = BuildInfo.Number;

    public bool Enabled { get => _enabled; set => _enabled = value; }

    public UpdateCheckService(MainViewModel viewModel, ILoggerService logger)
        : this(viewModel, logger, enqueue: null, fetch: null, settingsPath: null)
    {
    }

    // enqueue/fetch/settingsPath: test seams. null enqueue -> production enqueue below (never runs
    // inline; BeginInvoke queues even from the dispatcher thread), null fetch -> the real GitHub
    // request, null settingsPath -> the production INI path.
    internal UpdateCheckService(MainViewModel viewModel, ILoggerService logger,
        Action<Action>? enqueue, Func<CancellationToken, Task<string?>>? fetch, string? settingsPath)
    {
        _viewModel = viewModel;
        _logger = logger;
        _dispatcher = System.Windows.Application.Current?.Dispatcher;
        _enqueue = enqueue ?? ProductionEnqueue;
        _fetch = fetch;
        // REQUIRED: production resolves the real INI path here. A null flowing into Start() would
        // call AppSettings.LoadCheckForUpdatesEnabled(null) -> IniDocument.Load(null) ->
        // File.Exists(null)=false => empty document => Enabled=false on EVERY launch: the startup
        // trigger silently dead, hidden behind the fail-closed framing. Tests inject a temp-dir path.
        _settingsPath = settingsPath ?? AppSettings.GetSettingsPath();
    }

    /// <summary>Called once from App.OnStartup after MainWindow is shown. Fire-and-forget.</summary>
    public void Start()
    {
        try
        {
            _enabled = AppSettings.LoadCheckForUpdatesEnabled(_settingsPath);
        }
        catch (Exception ex)
        {
            // Fail closed: an unreadable settings file disables the check for the session (zero network).
            _enabled = false;
            _logger.Log($"[UpdateCheck] Failed to read CheckForUpdates setting: {ex.Message}");
        }

        if (_enabled)
        {
            _ = DelayedCheckAsync();
        }
    }

    private async Task DelayedCheckAsync()
    {
        try
        {
            await Task.Delay(InitialDelay).ConfigureAwait(false);
            await CheckAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Defensive: CheckAsync already swallows its own failures; this guards Task.Delay itself
            // so a faulted fire-and-forget never surfaces as an unobserved task exception.
            _logger.Log($"[UpdateCheck] Delayed check failed: {ex.Message}");
        }
    }

    public async Task CheckAsync()
    {
        if (!Enabled)
        {
            return;
        }

        // D1: an unnumbered ("dev") build NEVER phones home — return BEFORE the single-flight gate
        // and the fetch (zero network calls), not fetch-then-discard. Parsed once here and reused
        // by the notify closure below.
        int? current = ParseCurrent(CurrentBuildNumber);
        if (current is null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _checkInFlight, 1, 0) != 0)
        {
            return; // single-flight: at most one in-flight request; this caller is a no-op
        }

        try
        {
            string? json;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CheckTimeoutSeconds)))
            {
                json = await (_fetch ?? FetchLatestJsonAsync)(cts.Token).ConfigureAwait(false);
            }

            var latest = ParseBuildNumber(ParseLatestTag(json ?? string.Empty));
            if (!ShouldNotify(latest, CurrentBuildNumber))
            {
                return;
            }

            int latestValue = latest!.Value;
            _enqueue(() =>
            {
                if (Enabled)
                {
                    _viewModel.NotifyUpdateAvailable(latestValue, current.Value);
                }
            });
        }
        catch (Exception ex)
        {
            // D5: offline, DNS, timeout, 403 rate limit, malformed payload — one log line, no UI, no retry.
            _logger.Log($"[UpdateCheck] Check failed (offline, rate-limited, or unexpected response): {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _checkInFlight, 0);
        }
    }

    private async Task<string?> FetchLatestJsonAsync(CancellationToken ct)
    {
        // Lazy so DI construction (and tests that never check) pay no client cost. Redirects are
        // DISABLED: a 3xx fails EnsureSuccessStatusCode instead of silently following off GitHub.
        _client ??= new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(CheckTimeoutSeconds)
        };

        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubUrls.LatestReleaseApiUrl);
        // GitHub requires a User-Agent on every API request.
        request.Headers.UserAgent.ParseAdd($"sWinShortcuts/{CurrentBuildNumber} (+{GitHubUrls.LatestReleasePageUrl})");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();   // 3xx also throws: redirects are disabled (host pin)
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    // Copied from RapidFireStatusService's production enqueue: never inline, no-ops on a null or
    // shutting-down dispatcher, exception-isolated so a pool-thread caller can never observe it.
    private void ProductionEnqueue(Action action)
    {
        try
        {
            var dispatcher = _dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
            {
                return;
            }

            dispatcher.BeginInvoke(action, DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            _logger.Log($"[UpdateCheck] Dispatcher enqueue failed: {ex.Message}");
        }
    }

    // ---- pure, unit-tested core (InvariantCulture int parsing only — no culture-sensitive keys) ----

    /// <summary>"42" -> 42; "dev"/junk -> null. Pure digits only (no sign, no whitespace).</summary>
    internal static int? ParseCurrent(string? buildNumber)
        => int.TryParse(buildNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>"build-42" -> 42 (OrdinalIgnoreCase prefix, trailing int); anything else -> null.</summary>
    internal static int? ParseBuildNumber(string? tagName)
    {
        if (tagName is null)
        {
            return null;
        }

        var trimmed = tagName.Trim();
        const string Prefix = "build-";
        return trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed[Prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Root "tag_name" string of a GitHub releases/latest payload; malformed/missing -> null.</summary>
    internal static string? ParseLatestTag(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("tag_name", out var tag)
                && tag.ValueKind == JsonValueKind.String
                ? tag.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool ShouldNotify(int? latestBuild, string currentBuildNumber)
        => latestBuild is > 0
            && int.TryParse(currentBuildNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var current)
            && latestBuild.Value > current;
}
