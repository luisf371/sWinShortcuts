using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using Microsoft.Win32;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class CrosshairOffsetHotkeyTests
{
    [Fact]
    public void DecodedKey_RepeatAndReassignment_FiresOncePerPressAndPassesThrough()
    {
        using var service = InputHookServiceTestExtensions.CreateWithFakeForeground(new NullLoggerService(), new RecordingInputSender());
        var offsetRequests = 0;
        var colorRequests = 0;
        service.CrosshairOffsetToggleRequested += (_, _) => offsetRequests++;
        service.ColorVariantToggleRequested += (_, _) => colorRequests++;
        service.SetCrosshairOffsetToggleKey(Key.F8);
        service.SetColorToggleKey(Key.F8);

        Assert.False(Dispatch(service, Key.F8, true));
        service.SetCrosshairOffsetToggleKey(Key.F8);
        Assert.False(Dispatch(service, Key.F8, true));
        Assert.Equal(1, offsetRequests);
        Assert.Equal(1, colorRequests);
        Assert.False(Dispatch(service, Key.F8, false));
        Assert.False(Dispatch(service, Key.F8, true));
        Assert.Equal(2, offsetRequests);

        service.SetCrosshairOffsetToggleKey(Key.F9);
        Assert.False(Dispatch(service, Key.F8, true));
        Assert.False(Dispatch(service, Key.F9, true));
        Assert.False(Dispatch(service, Key.F9, true));
        Assert.Equal(3, offsetRequests);
        service.SetCrosshairOffsetToggleKey(null);
        Assert.False(Dispatch(service, Key.F9, false));
        Assert.False(Dispatch(service, Key.F9, true));
        Assert.Equal(3, offsetRequests);
    }

    [Fact]
    public void ReassignToHeldKey_RequiresReleaseBeforeToggle()
    {
        var held = new HashSet<int>();
        using var service = new InputHookService(new NullLoggerService(), new RecordingInputSender(),
            Stopwatch.GetTimestamp, held.Contains, FakeAutoRunTransport.MatchingForeground());
        var requests = 0;
        service.CrosshairOffsetToggleRequested += (_, _) => requests++;
        service.SetCrosshairOffsetToggleKey(Key.F8);
        Dispatch(service, Key.F8, true);

        held.Add(KeyInterop.VirtualKeyFromKey(Key.F9));
        service.SetCrosshairOffsetToggleKey(Key.F9);
        Assert.False(Dispatch(service, Key.F9, true));
        Assert.Equal(1, requests);
        held.Clear();
        Assert.False(Dispatch(service, Key.F9, false));
        Assert.False(Dispatch(service, Key.F9, true));
        Assert.Equal(2, requests);
    }

    [Fact]
    public void DecodedKey_ForegroundGenerationMismatch_DoesNotDeferToggleUntilRepeat()
    {
        using var service = InputHookServiceTestExtensions.CreateWithFakeForeground(new NullLoggerService(), new RecordingInputSender());
        var profile = new Profile { Name = "Game", Executable = "game.exe" };
        service.ConfigureActiveProfileForTesting(profile, 1, altPressed: false);
        var runtime = (InputRuntimeState)typeof(InputHookService)
            .GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        service.SetForegroundIdentity((IntPtr)100, 42, "game.exe", 2);
        service.SetCrosshairOffsetToggleKey(Key.F8);
        var requests = 0;
        service.CrosshairOffsetToggleRequested += (_, _) => requests++;

        Assert.False(Dispatch(service, Key.F8, true));
        runtime.SetActiveProfile(profile, 2);
        Assert.False(Dispatch(service, Key.F8, true));
        Assert.Equal(0, requests);
        Assert.False(Dispatch(service, Key.F8, false));
        Assert.False(Dispatch(service, Key.F8, true));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightAlt)]
    [InlineData(Key.LWin)]
    [InlineData(Key.System)]
    [InlineData(Key.DeadCharProcessed)]
    [InlineData((Key)9999)]
    [InlineData(Key.None)]
    public void SetKey_InvalidKey_ClearsPreviousAssignment(Key key)
    {
        using var service = InputHookServiceTestExtensions.CreateWithFakeForeground(new NullLoggerService(), new RecordingInputSender());
        var requests = 0;
        service.CrosshairOffsetToggleRequested += (_, _) => requests++;
        service.SetCrosshairOffsetToggleKey(Key.F8);
        service.SetCrosshairOffsetToggleKey(key);

        Assert.False(Dispatch(service, Key.F8, true));
        Assert.False(Dispatch(service, Key.F8, false));
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeedPhysicalState_UsesHeldKeyAcrossHookBoundary(bool held)
    {
        var physicalDown = false;
        using var service = new InputHookService(new NullLoggerService(), new RecordingInputSender(),
            Stopwatch.GetTimestamp, _ => physicalDown, FakeAutoRunTransport.MatchingForeground());
        var requests = 0;
        service.CrosshairOffsetToggleRequested += (_, _) => requests++;
        service.SetCrosshairOffsetToggleKey(Key.F8);
        Dispatch(service, Key.F8, true);

        physicalDown = held;
        // Start and keyboard reinstall both use this routine while native callbacks are gated.
        typeof(InputHookService).GetMethod("SeedAppTogglePhysicalState",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, null);
        Assert.False(Dispatch(service, Key.F8, true));
        Assert.Equal(held ? 1 : 2, requests);
        physicalDown = false;
        Assert.False(Dispatch(service, Key.F8, false));
        Assert.False(Dispatch(service, Key.F8, true));
        Assert.Equal(held ? 2 : 3, requests);
    }

    [Theory]
    [InlineData(SessionSwitchReason.SessionUnlock, false)]
    [InlineData(SessionSwitchReason.ConsoleConnect, false)]
    [InlineData(SessionSwitchReason.RemoteConnect, false)]
    [InlineData(SessionSwitchReason.SessionUnlock, true)]
    public void SessionResume_ReseedsToggleAfterMissedRelease(SessionSwitchReason reason, bool stillHeld)
    {
        var held = new HashSet<int>();
        using var service = new InputHookService(new NullLoggerService(), new RecordingInputSender(),
            Stopwatch.GetTimestamp, held.Contains, FakeAutoRunTransport.MatchingForeground());
        service.StartInputExecutorForTesting();
        try
        {
            var requests = 0;
            service.CrosshairOffsetToggleRequested += (_, _) => requests++;
            service.SetCrosshairOffsetToggleKey(Key.F8);
            Dispatch(service, Key.F8, true);
            var sessionSwitch = typeof(InputHookService).GetMethod("OnSessionSwitch",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            sessionSwitch.Invoke(service, [null, new SessionSwitchEventArgs(SessionSwitchReason.SessionLock)]);

            // Key-up on the locked/disconnected desktop never reaches the hook.
            if (stillHeld) held.Add(KeyInterop.VirtualKeyFromKey(Key.F8));
            sessionSwitch.Invoke(service, [null, new SessionSwitchEventArgs(reason)]);
            Assert.False(Dispatch(service, Key.F8, true));
            Assert.Equal(stillHeld ? 1 : 2, requests);
            held.Clear();
            Dispatch(service, Key.F8, false);
            Dispatch(service, Key.F8, true);
            Assert.Equal(stillHeld ? 2 : 3, requests);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    private static bool Dispatch(InputHookService service, Key key, bool down) =>
        service.DispatchDecodedKeyboardEvent(KeyInterop.VirtualKeyFromKey(key), down, !down);
}
