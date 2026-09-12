using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using Microsoft.Win32;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class AppToggleRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RecoverColorToggle_MissedRelease_UsesPhysicalState(bool held, bool sessionResume)
    {
        var physicalDown = false;
        var toggleVk = KeyInterop.VirtualKeyFromKey(Key.F8);
        using var service = new InputHookService(new NullLoggerService(), new RecordingInputSender(),
            Stopwatch.GetTimestamp, vk => vk == toggleVk && physicalDown,
            FakeAutoRunTransport.MatchingForeground());
        service.StartInputExecutorForTesting();
        try
        {
            var requests = 0;
            service.ColorVariantToggleRequested += (_, _) => requests++;
            service.SetColorToggleKey(Key.F8);
            Assert.False(Dispatch(service, true));
            Assert.Equal(1, requests);

            physicalDown = held;
            Recover(service, sessionResume);
            Assert.Equal(1, requests);
            Assert.False(Dispatch(service, true));
            Assert.Equal(held ? 1 : 2, requests);

            physicalDown = false;
            Assert.False(Dispatch(service, false));
            Assert.False(Dispatch(service, true));
            Assert.Equal(held ? 2 : 3, requests);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RecoverRapidFireToggle_MissedRelease_UsesPhysicalState(bool held, bool sessionResume)
    {
        var physicalDown = false;
        var toggleVk = KeyInterop.VirtualKeyFromKey(Key.F8);
        using var service = new InputHookService(new NullLoggerService(), new RecordingInputSender(),
            Stopwatch.GetTimestamp, vk => vk == toggleVk && physicalDown,
            FakeAutoRunTransport.MatchingForeground());
        service.StartInputExecutorForTesting();
        try
        {
            var profile = new Profile { Name = "Game", Executable = "game.exe" };
            profile.RapidFire.IsEnabled = true;
            service.ConfigureActiveProfileForTesting(profile, 1, altPressed: false);
            service.AdvancedModeEnabled = true;
            service.SetRapidFireToggleKey(Key.F8);
            Assert.False(Dispatch(service, true));
            Assert.Equal(RapidFireArmStatus.Ready, service.GetRapidFireArmStatus());

            physicalDown = held;
            Recover(service, sessionResume);
            // Watchdog recovery preserves the sticky arm; leaving the session disarms it.
            Assert.Equal(sessionResume ? RapidFireArmStatus.Off : RapidFireArmStatus.Ready,
                service.GetRapidFireArmStatus());
            Assert.False(Dispatch(service, true));
            Assert.Equal(held == sessionResume ? RapidFireArmStatus.Off : RapidFireArmStatus.Ready,
                service.GetRapidFireArmStatus());

            physicalDown = false;
            Assert.False(Dispatch(service, false));
            Assert.False(Dispatch(service, true));
            Assert.Equal(held == sessionResume ? RapidFireArmStatus.Ready : RapidFireArmStatus.Off,
                service.GetRapidFireArmStatus());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    private static void Recover(InputHookService service, bool sessionResume)
    {
        if (sessionResume)
        {
            var sessionSwitch = typeof(InputHookService).GetMethod("OnSessionSwitch",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            sessionSwitch.Invoke(service, [service, new SessionSwitchEventArgs(SessionSwitchReason.SessionLock)]);
            sessionSwitch.Invoke(service, [service, new SessionSwitchEventArgs(SessionSwitchReason.SessionUnlock)]);
            return;
        }

        service.ResetInputStateForTesting();
        // The successful keyboard-reinstall path uses this same routine before admitting callbacks.
        typeof(InputHookService).GetMethod("SeedAppTogglePhysicalState",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, null);
    }

    private static bool Dispatch(InputHookService service, bool down) =>
        service.DispatchDecodedKeyboardEvent(KeyInterop.VirtualKeyFromKey(Key.F8), down, !down);
}
