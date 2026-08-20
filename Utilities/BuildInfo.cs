using System.Reflection;

namespace sWinShortcuts.Utilities;

// Reads back the build stamp baked in at compile time: the csproj generates an
// [assembly: AssemblyMetadata("BuildNumber", ...)] attribute from the BuildNumber MSBuild property
// (GenerateAssemblyInfo is disabled in this project, so the SDK's own attribute pipeline doesn't
// run). CI passes the workflow run number — the same number that forms the "build-N" GitHub
// Release tag — so the Settings footer label matches the release the exe came from.
internal static class BuildInfo
{
    private const string Fallback = "dev";

    public static string Number { get; } = Load();

    private static string Load()
    {
        var attribute = typeof(BuildInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildNumber");

        return string.IsNullOrWhiteSpace(attribute?.Value) ? Fallback : attribute.Value!;
    }
}
