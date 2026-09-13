using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class MacroRecoveryTests
{
    [Theory]
    [InlineData(false, 0x01, MouseButton.Left)]
    [InlineData(false, 0x02, MouseButton.Right)]
    [InlineData(true, 0x01, MouseButton.Right)]
    [InlineData(true, 0x02, MouseButton.Left)]
    public void Seed_PhysicalMouseHold_TracksItsLogicalButtonAndRelease(bool swapped, int physicalVk, MouseButton logicalButton)
    {
        var physical = new MacroPhysicalState();
        physical.Seed(vk => vk == physicalVk, buttonsSwapped: swapped);
        Assert.True(physical.IsPhysicalMouseButtonDown(logicalButton));
        Assert.True(physical.CaptureHeld().Buttons[(int)logicalButton]);

        physical.ObserveButton(logicalButton, false);
        Assert.False(physical.AnyMouseButtonDown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Seed_PhysicalTakeover_RetainsHeldOwnershipAndRetiresConfirmedRelease(bool mouse)
    {
        var physical = new MacroPhysicalState();
        if (mouse)
        {
            physical.ObserveButton(MouseButton.Right, true);
            physical.TakeButton(MouseButton.Right);
        }
        else
        {
            physical.ObserveKey(0x41, true);
            physical.TakeKey(0x41);
        }
        physical.Seed(vk => vk == (mouse ? 0x01 : 0x41), buttonsSwapped: true);
        Assert.True(physical.HasTakeovers);
        Assert.True(mouse ? physical.HasPhysicalMouseTakeover(MouseButton.Right) : physical.HasPhysicalKeyTakeover(0x41));

        physical.Seed(_ => false, buttonsSwapped: true);
        Assert.False(physical.HasTakeovers);
        if (mouse) physical.ObserveButton(MouseButton.Right, false);
        else physical.ObserveKey(0x41, false);
        Assert.False(physical.HasTakeovers);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SessionUnlock_ConsumedKey_RecoversReleasedPairAndRetainsHeldPair(bool emergency, bool stillHeld)
    {
        var physicalKeys = new bool[256];
        var sender = new RecordingInputSender();
        using var service = Create(sender, physicalKeys, out var profile);
        var vk = emergency ? 0x7B : 0x75;
        if (emergency)
        {
            MacroPlaybackTests.Press(service, 0x75);
            MacroPlaybackTests.WaitUntil(() => sender.Transitions.Any(edge => edge.Key == Key.A && edge.IsDown));
        }
        physicalKeys[vk] = true;
        Assert.True(service.DispatchDecodedKeyboardEvent(vk, true, false));
        if (!emergency)
            MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.WaitingForShortcutRelease);
        var previousSession = service.GetMacroSession().SessionId;
        SwitchSession(service, SessionSwitchReason.SessionLock);
        WaitUntilIdle(service);

        // A release on the lock screen is absent from the hook stream; resume queries the device.
        physicalKeys[vk] = stillHeld;
        SwitchSession(service, SessionSwitchReason.SessionUnlock);
        if (stillHeld)
        {
            Assert.True(service.DispatchDecodedKeyboardEvent(vk, true, false));
            Assert.Equal(previousSession, service.GetMacroSession().SessionId);
            physicalKeys[vk] = false;
            Assert.True(service.DispatchDecodedKeyboardEvent(vk, false, true));
        }

        if (emergency)
        {
            Assert.False(service.DispatchDecodedKeyboardEvent(vk, true, false));
            Assert.False(service.DispatchDecodedKeyboardEvent(vk, false, true));
        }
        else
        {
            MacroPlaybackTests.Press(service, vk);
            MacroPlaybackTests.WaitUntil(() => sender.Transitions.Any(edge => edge.Key == Key.A && edge.IsDown));
            Assert.True(service.GetMacroSession().SessionId > previousSession);
            service.CancelMacroPlayback(profile);
            WaitUntilIdle(service);
        }
    }

    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.F12)]
    public void SessionUnlock_ReleaseStillPending_RecoversEmergencyPairWithoutLosingOwnedCleanup(Key heldKey)
    {
        using var upEntered = new ManualResetEventSlim();
        using var finishUp = new ManualResetEventSlim();
        var physicalKeys = new bool[256];
        var sender = new RecordingInputSender
        {
            KeyResult = (key, down, macroRelease) =>
            {
                if (key == heldKey && !down && macroRelease)
                {
                    upEntered.Set();
                    finishUp.Wait();
                    physicalKeys[0x7B] = false;
                }
                return true;
            }
        };
        using var service = Create(sender, physicalKeys, out _, heldKey);
        try
        {
            MacroPlaybackTests.Press(service, 0x75);
            MacroPlaybackTests.WaitUntil(() => sender.Transitions.Any(edge => edge.Key == heldKey && edge.IsDown));
            physicalKeys[0x7B] = true;
            Assert.True(service.DispatchDecodedKeyboardEvent(0x7B, true, false));
            Assert.True(upEntered.Wait(TimeSpan.FromSeconds(3)));
            SwitchSession(service, SessionSwitchReason.SessionLock);
            // The physical F12 release was missed. GetAsyncKeyState can still report the
            // macro's synthetic F12 hold until its blocked native UP finishes.
            physicalKeys[0x7B] = heldKey == Key.F12;
            SwitchSession(service, SessionSwitchReason.SessionUnlock);
        }
        finally
        {
            finishUp.Set();
        }

        WaitUntilIdle(service);
        Assert.Equal(new[] { (heldKey, true), (heldKey, false) }, sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x7B, true, false));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x7B, false, true));
    }

    [Fact]
    public Task SessionUnlock_OffHookThread_ReadsMacroStateOnHookDispatcher() => MacroRecordingLifetimeTests.RunOnStaAsync(async () =>
    {
        var readThreads = new ConcurrentQueue<int>();
        using var service = Create(new RecordingInputSender(), new bool[256], out _, observeRead: vk =>
        {
            if (vk == 0x7B) readThreads.Enqueue(Environment.CurrentManagedThreadId);
        });
        var dispatcher = Dispatcher.CurrentDispatcher;
        var hookThread = Environment.CurrentManagedThreadId;
        typeof(InputHookService).GetField("_hookDispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, dispatcher);
        readThreads.Clear();

        await Task.Run(() => SwitchSession(service, SessionSwitchReason.SessionUnlock));
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

        Assert.NotEmpty(readThreads);
        Assert.All(readThreads, thread => Assert.Equal(hookThread, thread));
    });

    private static InputHookService Create(RecordingInputSender sender, bool[] physicalKeys, out Profile profile,
        Key heldKey = Key.A, Action<int>? observeRead = null)
    {
        var service = new InputHookService(new NullLoggerService(), sender, Stopwatch.GetTimestamp,
            vk => { observeRead?.Invoke(vk); return Volatile.Read(ref physicalKeys[vk]); }, FakeAutoRunTransport.MatchingForeground());
        service.StartInputExecutorForTesting();
        service.AdvancedModeEnabled = true;
        profile = new Profile { Name = "Game", Executable = "game.exe" };
        profile.Macros.IsEnabled = true;
        profile.Macros.Definitions = [new MacroDefinition
        {
            IsEnabled = true, ShortcutKey = Key.F6,
            Steps = [new() { Kind = MacroStepKind.KeyDown, Key = heldKey },
                new() { Kind = MacroStepKind.Wait, DurationMs = 30000 },
                new() { Kind = MacroStepKind.KeyUp, Key = heldKey }]
        }];
        service.ConfigureActiveProfileForTesting(profile, 1, false);
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        return service;
    }

    private static void SwitchSession(InputHookService service, SessionSwitchReason reason) =>
        typeof(InputHookService).GetMethod("OnSessionSwitch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [service, new SessionSwitchEventArgs(reason)]);

    private static void WaitUntilIdle(InputHookService service)
    {
        var macros = (MacroStateMachine)typeof(InputHookService)
            .GetField("_macros", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle && !macros.IsBusy);
    }
}
