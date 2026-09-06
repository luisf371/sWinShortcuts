using System.Reflection;
using System.Runtime.InteropServices;
using sWinShortcuts.Services;
using Xunit;

namespace Tests;

public sealed class NativeLibraryLoadingTests
{
    [Fact]
    public void NvapiImports_UseOnlySystem32Search()
    {
        var native = typeof(NvidiaColorControlService).GetNestedType("NvApiNative", BindingFlags.NonPublic);
        Assert.NotNull(native);

        foreach (var name in new[] { "NvAPI_QueryInterface64", "NvAPI_QueryInterface32" })
        {
            var method = native.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            var policy = method.GetCustomAttribute<DefaultDllImportSearchPathsAttribute>();
            Assert.NotNull(policy);
            Assert.Equal(DllImportSearchPath.System32, policy.Paths);
        }
    }
}
