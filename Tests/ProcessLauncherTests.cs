using System.Runtime.InteropServices;
using sWinShortcuts.Interop;
using sWinShortcuts.Utilities;
using Xunit;

namespace Tests;

public sealed class ProcessLauncherTests
{
    public static bool InteractiveShellAvailable => NativeMethods.GetShellWindow() != IntPtr.Zero;

    [Theory(SkipUnless = nameof(InteractiveShellAvailable),
        Skip = "Requires an interactive Windows desktop with Explorer.")]
    [InlineData(ApartmentState.STA)]
    [InlineData(ApartmentState.MTA)]
    public async Task DesktopLookup_DoesNotRequireOrdinaryEnumeratedDesktop(ApartmentState apartment)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ProcessLauncher.WithDesktopShell(shell => Assert.True(Marshal.IsComObject(shell)));
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(apartment);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
