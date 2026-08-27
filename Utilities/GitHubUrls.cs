namespace sWinShortcuts.Utilities;

// The only outbound host the app ever contacts (card constraint: github.com only).
// api.github.com is GitHub's documented API subdomain; redirects are disabled on the
// client so a request can never be moved off GitHub.
internal static class GitHubUrls
{
    public const string RepoSlug = "luisf371/sWinShortcuts";
    public const string LatestReleaseApiUrl = "https://api.github.com/repos/" + RepoSlug + "/releases/latest";
    public const string LatestReleasePageUrl = "https://github.com/" + RepoSlug + "/releases/latest";
}
