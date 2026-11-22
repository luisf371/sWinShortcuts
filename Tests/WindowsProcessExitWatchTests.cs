using System.Diagnostics;
using sWinShortcuts.Services;
using Xunit;

namespace Tests;

public sealed class WindowsProcessExitWatchTests
{
    private const int TIMEOUT_MS = 5000;
    private const int QUIET_PERIOD_MS = 250;

    [Fact]
    public void TryWatch_LiveProcessExits_InvokesCallbackExactlyOnce()
    {
        using var process = StartChild(waitForExitSignal: true);
        using var exitSignal = process.StandardInput;
        int callbacks = 0;
        using var watch = WindowsProcessExitWatch.TryWatch(
            (uint)process.Id, () => true, () => Interlocked.Increment(ref callbacks), logger: null);

        Assert.NotNull(watch);
        Assert.False(process.HasExited);
        Assert.Equal(0, Volatile.Read(ref callbacks));

        exitSignal.Close();
        Assert.True(process.WaitForExit(TIMEOUT_MS));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref callbacks) > 0, TIMEOUT_MS));
        Assert.False(SpinWait.SpinUntil(() => Volatile.Read(ref callbacks) > 1, QUIET_PERIOD_MS));
    }

    [Fact]
    public void TryWatch_ProcessAlreadyExited_InvokesCallback()
    {
        using var process = StartChild();
        Assert.True(process.WaitForExit(TIMEOUT_MS));
        int callbacks = 0;
        using var watch = WindowsProcessExitWatch.TryWatch(
            (uint)process.Id, () => true, () => Interlocked.Increment(ref callbacks), logger: null);

        Assert.NotNull(watch);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref callbacks) > 0, TIMEOUT_MS));
    }

    [Fact]
    public void Dispose_ProcessExitsLater_DoesNotInvokeCallback()
    {
        using var process = StartChild(waitForExitSignal: true);
        using var exitSignal = process.StandardInput;
        int callbacks = 0;
        using var watch = WindowsProcessExitWatch.TryWatch(
            (uint)process.Id, () => true, () => Interlocked.Increment(ref callbacks), logger: null);

        Assert.NotNull(watch);
        Assert.False(process.HasExited);
        watch.Dispose();
        exitSignal.Close();

        Assert.True(process.WaitForExit(TIMEOUT_MS));
        Assert.False(SpinWait.SpinUntil(() => Volatile.Read(ref callbacks) > 0, QUIET_PERIOD_MS));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryWatch_OwnershipCheckFails_RejectsWatchWithoutCallback(bool throws)
    {
        using var process = StartChild();
        Assert.True(process.WaitForExit(TIMEOUT_MS));
        int callbacks = 0;
        using var watch = WindowsProcessExitWatch.TryWatch(
            (uint)process.Id,
            () => throws ? throw new InvalidOperationException("Ownership check failed.") : false,
            () => Interlocked.Increment(ref callbacks),
            logger: null);

        Assert.Null(watch);
        Assert.False(SpinWait.SpinUntil(() => Volatile.Read(ref callbacks) > 0, QUIET_PERIOD_MS));
    }

    [Theory]
    [InlineData(0, 1u)]
    [InlineData(1, 0u)]
    public void TryRegister_ZeroWindowHandleOrProcessId_ReturnsNull(int windowHandle, uint processId)
    {
        using var watch = WindowsProcessExitWatch.TryRegister((IntPtr)windowHandle, processId, () => { }, logger: null);

        Assert.Null(watch);
    }

    private static Process StartChild(bool waitForExitSignal = false)
    {
        // Keep the child alive until the test closes stdin, even if registration is delayed.
        return Process.Start(new ProcessStartInfo("cmd.exe", waitForExitSignal ? "/d /c set /p exitSignal=" : "/d /c exit 0")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = waitForExitSignal
        })!;
    }
}
