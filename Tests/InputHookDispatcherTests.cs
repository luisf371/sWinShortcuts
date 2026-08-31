using System.Reflection;
using System.Windows.Input;
using Microsoft.Win32;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class InputHookDispatcherTests
{
    [Fact]
    public void DecodedEvents_NoActiveProfile_PassThrough()
    {
        using var service = new InputHookService(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();

        Assert.False(service.DispatchDecodedKeyboardEvent(
            KeyInterop.VirtualKeyFromKey(Key.A),
            isKeyDown: true,
            isKeyUp: false));
        Assert.False(service.DispatchDecodedMouseEvent(NativeMethods.WM_LBUTTONDOWN, 0));

        service.StopInputExecutorForTesting();
    }

    [Fact]
    public void DecodedMouseEvent_AltBindingConsumesDown()
    {
        using var service = new InputHookService(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();
        var profile = new Profile
        {
            Name = "Game",
            Executable = "game.exe",
            AltMouse =
            {
                IsEnabled = true,
                Bindings = new Dictionary<sWinShortcuts.Models.MouseButton, MouseButtonBinding>
                {
                    [sWinShortcuts.Models.MouseButton.Left] = new() { TapKey = Key.A }
                }
            }
        };
        service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);

        Assert.True(service.DispatchDecodedMouseEvent(NativeMethods.WM_LBUTTONDOWN, 0));
        Assert.True(service.DispatchDecodedMouseEvent(NativeMethods.WM_LBUTTONUP, 0));

        service.StopInputExecutorForTesting();
    }

    [Fact]
    public async Task AutoRunOwnedWUp_ReleasesCombinedAndLauncherBeforeReturning()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureCombinedOverrideForTesting(Key.W, Key.F, suppressOriginal: true);
            service.ConfigureLauncherLatchForTesting(new Profile { Name = "Windows" }, Key.W);
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            service.ConfigureForegroundAutoRunHandoffForTesting(
                new Profile { Name = "Game", Executable = "game.exe" });

            Assert.True(service.DispatchDecodedKeyboardEvent(
                KeyInterop.VirtualKeyFromKey(Key.W),
                isKeyDown: false,
                isKeyUp: true));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(
                new[] { (Key.F, true), (Key.W, true), (Key.F, false) },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
            Assert.False(service.HandleLauncherForTesting(Key.W, isDown: false));
        }
        finally
        {
            service.ReleaseForegroundAutoRun();
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task PanicWinsAltKeyboardAndOwnsMatchingUp()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = new Profile
            {
                Name = "Game",
                Executable = "game.exe",
                AltKeyboard =
                {
                    IsEnabled = true,
                    HoldThresholdMilliseconds = 60_000,
                    Bindings = new Dictionary<Key, AltKeyboardBinding>
                    {
                        [Key.Q] = new() { TapKey = Key.A, HoldKey = Key.B }
                    }
                },
                RightClickHoldBreath =
                {
                    IsEnabled = true,
                    DelayMilliseconds = 60_000,
                    PanicTrigger = InputTrigger.FromKey(Key.Q)
                }
            };
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);
            service.AdvancedModeEnabled = true;
            Assert.False(service.DispatchDecodedMouseEvent(NativeMethods.WM_RBUTTONDOWN, 0));
            var q = KeyInterop.VirtualKeyFromKey(Key.Q);

            Assert.True(service.DispatchDecodedKeyboardEvent(q, isKeyDown: true, isKeyUp: false));
            Assert.True(service.DispatchDecodedKeyboardEvent(q, isKeyDown: true, isKeyUp: false));
            Assert.True(service.DispatchDecodedKeyboardEvent(q, isKeyDown: false, isKeyUp: true));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Empty(sender.Transitions);
        }
        finally
        {
            service.DispatchDecodedMouseEvent(NativeMethods.WM_RBUTTONUP, 0);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void ConsumedAltLeftPreventsRapidFireUntilFreshUnconsumedPress()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateRapidFireProfile();
            profile.AltMouse.IsEnabled = true;
            profile.AltMouse.HoldThresholdMilliseconds = 60_000;
            profile.AltMouse.Bindings = new Dictionary<sWinShortcuts.Models.MouseButton, MouseButtonBinding>
            {
                [sWinShortcuts.Models.MouseButton.Left] = new() { TapKey = Key.A }
            };
            service.ConfigureRapidFireForTesting(profile, foregroundGeneration: 1);
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: true);

            Assert.True(service.DispatchDecodedMouseEvent(NativeMethods.WM_LBUTTONDOWN, 0));
            service.FireRapidFireTimerForTesting();
            Assert.True(service.DispatchDecodedMouseEvent(NativeMethods.WM_LBUTTONUP, 0));
            Assert.Empty(sender.MouseClickThreadIds);

            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: false);
            Assert.False(service.DispatchDecodedMouseEvent(NativeMethods.WM_LBUTTONDOWN, 0));
            service.FireRapidFireTimerForTesting();
            Assert.False(service.DispatchDecodedMouseEvent(NativeMethods.WM_LBUTTONUP, 0));
            Assert.Single(sender.MouseClickThreadIds);
        }
        finally
        {
            service.DispatchDecodedMouseEvent(NativeMethods.WM_LBUTTONUP, 0);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public async Task RightButtonUp_ReleasesRightClickRemapBeforeHoldBreath()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = new Profile
            {
                Name = "Game",
                Executable = "game.exe",
                CombinedMappings =
                {
                    IsEnabled = true,
                    Mappings =
                    [
                        new CombinedMappingEntry
                        {
                            SourceKey = Key.E,
                            TargetKey = Key.F,
                            SuppressOriginalKey = true,
                            RightClickOnly = true
                        }
                    ]
                },
                RightClickHoldBreath =
                {
                    IsEnabled = true,
                    DelayMilliseconds = 0,
                    HoldBreathKey = Key.LeftShift,
                    Mode = HoldBreathMode.Hold
                }
            };
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: false);
            service.AdvancedModeEnabled = true;

            Assert.False(service.DispatchDecodedMouseEvent(NativeMethods.WM_RBUTTONDOWN, 0));
            Assert.True(service.DispatchDecodedKeyboardEvent(
                KeyInterop.VirtualKeyFromKey(Key.E),
                isKeyDown: true,
                isKeyUp: false));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(service.DispatchDecodedMouseEvent(NativeMethods.WM_RBUTTONUP, 0));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(
                new[]
                {
                    (Key.LeftShift, true),
                    (Key.F, true),
                    (Key.F, false),
                    (Key.LeftShift, false)
                },
                sender.Transitions.Select(item => (item.Key, item.IsDown)).ToArray());
        }
        finally
        {
            service.DispatchDecodedMouseEvent(NativeMethods.WM_RBUTTONUP, 0);
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void ReleaseForegroundState_PreservesRapidFireArmAndBackgroundAutoRun()
    {
        var sender = new RecordingInputSender();
        using var service = new InputHookService(new NullLoggerService(), sender);
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateRapidFireProfile();
            service.ConfigureRapidFireForTesting(profile, foregroundGeneration: 1);
            service.ConfigureForegroundAutoRunForTesting(profile, sprintInjected: false, sprintKey: Key.None);
            var autoRun = GetAutoRun(service);
            typeof(AutoRunStateMachine)
                .GetField("_isBackground", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(autoRun, true);
            service.DispatchDecodedMouseEvent(NativeMethods.WM_LBUTTONDOWN, 0);

            service.ReleaseForegroundState();
            service.FireRapidFireTimerForTesting();

            Assert.Equal(RapidFireArmStatus.Ready, service.GetRapidFireArmStatus());
            Assert.True(autoRun.IsBackground);
            Assert.Empty(sender.MouseClickThreadIds);
        }
        finally
        {
            service.DispatchDecodedMouseEvent(NativeMethods.WM_LBUTTONUP, 0);
            service.AdvancedModeEnabled = false;
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void RapidFireEvents_UseServiceSenderAndObserveSynchronousPublicationOrder()
    {
        using var service = new InputHookService(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();

        try
        {
            var profile = CreateRapidFireProfile();
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: false);
            service.AdvancedModeEnabled = true;
            service.SetRapidFireToggleKey(Key.F8);
            var events = new List<(object? Sender, RapidFireArmStatus Status)>();
            service.RapidFireArmChanged += (sender, _) =>
                events.Add((sender, service.GetRapidFireArmStatus()));
            var toggle = KeyInterop.VirtualKeyFromKey(Key.F8);

            Assert.False(service.DispatchDecodedKeyboardEvent(toggle, isKeyDown: true, isKeyUp: false));
            Assert.False(service.DispatchDecodedKeyboardEvent(toggle, isKeyDown: false, isKeyUp: true));
            Assert.Collection(
                events,
                item =>
                {
                    Assert.Same(service, item.Sender);
                    Assert.Equal(RapidFireArmStatus.Ready, item.Status);
                });

            service.SetForegroundIdentity(new IntPtr(0x101), 42, "game.exe", 2);
            Assert.Equal(RapidFireArmStatus.ArmedNotReady, service.GetRapidFireArmStatus());
            service.ActivateProfile(profile, 2);

            Assert.Equal(3, events.Count);
            Assert.All(events, item => Assert.Same(service, item.Sender));
            Assert.Equal(
                new[]
                {
                    RapidFireArmStatus.Ready,
                    RapidFireArmStatus.ArmedNotReady,
                    RapidFireArmStatus.Ready
                },
                events.Select(item => item.Status).ToArray());
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void SessionUnlock_ActiveProfileStillPublished_RecapturesAntiAfkTarget()
    {
        using var service = new InputHookService(new NullLoggerService(), new RecordingInputSender());
        service.StartInputExecutorForTesting();
        var profile = new Profile
        {
            Name = "Game",
            Executable = "game.exe",
            AntiAfk =
            {
                IsEnabled = true,
                SendMode = AntiAfkSendMode.Background
            }
        };

        try
        {
            service.SetForegroundIdentity((IntPtr)100, 7, profile.NormalizedExecutable, 1);
            service.ActivateProfile(profile, 1);
            Assert.NotNull(GetRetainedAntiAfkTarget(service));

            RaiseSessionSwitch(service, SessionSwitchReason.SessionLock);
            Assert.Null(GetRetainedAntiAfkTarget(service));

            RaiseSessionSwitch(service, SessionSwitchReason.SessionUnlock);
            Assert.NotNull(GetRetainedAntiAfkTarget(service));
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void ReleaseForegroundState_LogsArmPreservedReleaseRequest()
    {
        var logger = new NullLoggerService { IsEnabled = true };
        using var service = new InputHookService(logger, new RecordingInputSender());
        service.StartInputExecutorForTesting();

        try
        {
            service.ConfigureActiveProfileForTesting(
                CreateRapidFireProfile(), foregroundGeneration: 1, altPressed: false);
            service.ReleaseForegroundState();
            Assert.Contains("All state release requested (rapidFireArmPreserved=True)", logger.Messages);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void StopInputExecutorForTesting_LogsArmReleasedReleaseRequest()
    {
        var logger = new NullLoggerService { IsEnabled = true };
        using var service = new InputHookService(logger, new RecordingInputSender());
        service.StartInputExecutorForTesting();

        // Drives the private ReleaseAllState through the reflection extension (its three-argument
        // invocation — the arity contract every StopInputExecutorForTesting cleanup relies on).
        service.StopInputExecutorForTesting();
        Assert.Contains("All state release requested (rapidFireArmPreserved=False)", logger.Messages);
    }

    [Fact]
    public void ReconcileProfileSettings_HardDeactivation_IsLogged()
    {
        var logger = new NullLoggerService { IsEnabled = true };
        using var service = new InputHookService(logger, new RecordingInputSender());
        service.StartInputExecutorForTesting();

        try
        {
            var profile = new Profile { Name = "Game", Executable = "game.exe" };
            service.ConfigureActiveProfileForTesting(profile, foregroundGeneration: 1, altPressed: false);
            service.ReconcileProfileSettings(profile, ProfileChangeKind.Removed);
            Assert.Contains("Profile hard-deactivated: 'Game' (changeKind=Removed)", logger.Messages);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    [Fact]
    public void SessionSwitchAway_RequestsReleaseOfAllInjectedState()
    {
        var logger = new NullLoggerService { IsEnabled = true };
        using var service = new InputHookService(logger, new RecordingInputSender());
        service.StartInputExecutorForTesting();

        try
        {
            RaiseSessionSwitch(service, SessionSwitchReason.SessionLock);
            Assert.Contains(
                "Session switch (SessionLock): release requested for all injected state",
                logger.Messages);
            Assert.Contains("All state release requested (rapidFireArmPreserved=False)", logger.Messages);
        }
        finally
        {
            service.StopInputExecutorForTesting();
        }
    }

    private static AutoRunStateMachine GetAutoRun(InputHookService service) =>
        (AutoRunStateMachine)typeof(InputHookService)
            .GetField("_autoRun", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;

    private static object? GetRetainedAntiAfkTarget(InputHookService service)
    {
        var antiAfk = typeof(InputHookService)
            .GetField("_antiAfk", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        return typeof(AntiAfkStateMachine)
            .GetField("_retainedTarget", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(antiAfk);
    }

    private static void RaiseSessionSwitch(InputHookService service, SessionSwitchReason reason) =>
        typeof(InputHookService)
            .GetMethod("OnSessionSwitch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [service, new SessionSwitchEventArgs(reason)]);

    private static Profile CreateRapidFireProfile() => new()
    {
        Name = "Game",
        Executable = "game.exe",
        RapidFire =
        {
            IsEnabled = true,
            IntervalMilliseconds = RapidFireSettings.MaxIntervalMilliseconds,
            JitterMilliseconds = 0
        }
    };
}
