using System.Reflection;

namespace sWinShortcuts.Utilities;

// Reads back the build stamp baked in at compile time: the csproj generates an
// [assembly: AssemblyMetadata("BuildNumber", ...)] attribute from the BuildNumber MSBuild property
// (GenerateAssemblyInfo is disabled in this project, so the SDK's own attribute pipeline doesn't
// run). CI passes the workflow run number — the same number that forms the "build-N" GitHub
// Release tag — so the Settings footer label matches the release the exe came from.
internal static class BuildInfo
{
    private const string DevelopmentBuildNumber = "dev";

    public static string Number { get; } = Load("BuildNumber", DevelopmentBuildNumber);

    // Dev-only compile timestamp; the csproj only emits the BuildDate attribute for "dev" builds.
    // The runtime gate here is the second layer: a numbered build never exposes a date even if a
    // BuildDate attribute ever leaked into its binary, keeping the CI label at exactly "Build N".
    public static string Date { get; } =
        Number == DevelopmentBuildNumber ? Load("BuildDate", string.Empty) : string.Empty;

    private static string Load(string key, string fallback)
    {
        var attribute = typeof(BuildInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key);

        return string.IsNullOrWhiteSpace(attribute?.Value) ? fallback : attribute.Value!;
    }
}
