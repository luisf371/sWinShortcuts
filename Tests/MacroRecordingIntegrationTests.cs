using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class MacroRecordingIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RecordMacroAsync_StoppedWithKeyHeld_PassesRepeatsAndReleaseBeforeRemapping(bool emergencyStop, bool reseed)
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile);
        ConfigureRemap(service, profile);
        var recording = service.RecordMacroAsync(profile, profile.Macros.Definitions[0].Id, 30);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Recording);
        Assert.False(service.DispatchDecodedKeyboardEvent(0x41, true, false));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x41, true, false));
        if (emergencyStop) MacroPlaybackTests.Press(service, 0x7B);
        else service.StopMacroRecording();
        var result = await recording.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.AppendedBalancingReleases);
        Assert.Equal(new[] { MacroStepKind.KeyDown, MacroStepKind.KeyDown, MacroStepKind.KeyUp },
            result.Steps.Where(step => step.Kind != MacroStepKind.Wait).Select(step => step.Kind));

        if (reseed)
        {
            // Hook/session recovery can resample physical state before this held key is released.
            var physical = (MacroPhysicalState)typeof(InputHookService)
                .GetField("_macroPhysical", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            physical.Seed(vk => vk == 0x41);
        }
        Assert.False(service.DispatchDecodedKeyboardEvent(0x41, true, false));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x41, false, true));
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(sender.Transitions);

        await RemapA(service);
        Assert.Equal(new[] { (Key.B, true), (Key.B, false) }, sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
    }

    [Fact]
    public async Task RecordMacroAsync_StoppedWithWHeld_PreservesSubsequentAutoRunHandoff()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile);
        ConfigureAutoRun(service, profile);
        var recording = service.RecordMacroAsync(profile, profile.Macros.Definitions[0].Id, 30);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Recording);
        Assert.False(service.DispatchDecodedKeyboardEvent(0x57, true, false));
        service.StopMacroRecording();
        await recording.WaitAsync(TimeSpan.FromSeconds(3));

        MacroPlaybackTests.Press(service, 0x77);
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(sender.Transitions);
        Assert.False(service.DispatchDecodedKeyboardEvent(0x57, true, false));
        Assert.True(service.DispatchDecodedKeyboardEvent(0x57, false, true));
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Single(sender.Transitions, edge => edge.Key == Key.W && edge.IsDown);

        Assert.False(service.DispatchDecodedKeyboardEvent(0x57, true, false));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x57, false, true));
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(new[] { true, false }, sender.Transitions.Where(edge => edge.Key == Key.W).Select(edge => edge.IsDown));
    }

    [Fact]
    public async Task RecordMacroAsync_PreviouslySuppressedKeyHeld_KeepsOriginalReleaseObligation()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile);
        ConfigureRemap(service, profile);
        Assert.True(service.DispatchDecodedKeyboardEvent(0x41, true, false));
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        var recording = service.RecordMacroAsync(profile, profile.Macros.Definitions[0].Id, 30);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Recording);
        Assert.True(service.DispatchDecodedKeyboardEvent(0x41, true, false));
        service.StopMacroRecording();
        await recording.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(service.DispatchDecodedKeyboardEvent(0x41, false, true));
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(new[] { (Key.B, true), (Key.B, false) }, sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));

        sender.Transitions.Clear();
        await RemapA(service);
        Assert.Equal(new[] { (Key.B, true), (Key.B, false) }, sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
    }

    [Theory]
    [InlineData(Key.W, 0x57)]
    [InlineData(Key.S, 0x53)]
    public async Task RecordMacroAsync_PreheldMovementReleased_AutoRunStartsAndFreshMovementCancels(Key movementKey, int movementVk)
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile);
        ConfigureAutoRun(service, profile);
        Assert.False(service.DispatchDecodedKeyboardEvent(movementVk, true, false));
        var recording = service.RecordMacroAsync(profile, profile.Macros.Definitions[0].Id, 30);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Recording);

        Assert.False(service.DispatchDecodedKeyboardEvent(movementVk, false, true));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x77, true, false));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x77, false, true));
        Assert.Empty(sender.Transitions);
        service.StopMacroRecording();
        var result = await recording.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(result.Steps, step => step.Key == movementKey);

        MacroPlaybackTests.Press(service, 0x77);
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Single(sender.Transitions, edge => edge.Key == Key.W && edge.IsDown);

        Assert.False(service.DispatchDecodedKeyboardEvent(movementVk, true, false));
        Assert.False(service.DispatchDecodedKeyboardEvent(movementVk, false, true));
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(new[] { true, false }, sender.Transitions.Where(edge => edge.Key == Key.W).Select(edge => edge.IsDown));
    }

    [Theory]
    [InlineData(Key.W, 0x57)]
    [InlineData(Key.S, 0x53)]
    public async Task MacroShortcut_MovementPairConsumed_AutoRunObservesHeldKeyAndFreshPress(Key movementKey, int movementVk)
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 30000 });
        ConfigureAutoRun(service, profile);
        profile.Macros.Definitions = [profile.Macros.Definitions[0] with { ShortcutKey = movementKey }];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        MacroPlaybackTests.Press(service, movementVk);
        MacroPlaybackTests.WaitUntil(() => sender.Transitions.Any(edge => edge.Key == Key.A && !edge.IsDown));
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession() is { Mode: MacroSessionMode.Playing, RowCount: 2 });
        Assert.Single(sender.Transitions, edge => edge.Key == Key.A && edge.IsDown);
        Assert.True(service.DispatchDecodedKeyboardEvent(movementVk, true, false));
        MacroPlaybackTests.Press(service, 0x7B);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);

        MacroPlaybackTests.Press(service, 0x77);
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(movementKey == Key.S ? 1 : 0, sender.Transitions.Count(edge => edge.Key == Key.W && edge.IsDown));
        Assert.True(service.DispatchDecodedKeyboardEvent(movementVk, false, true));
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Single(sender.Transitions, edge => edge.Key == Key.W && edge.IsDown);

        // Remove the shortcut so the next physical movement press reaches Auto-Run cancellation.
        profile.Macros.IsEnabled = false;
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros);
        Assert.False(service.DispatchDecodedKeyboardEvent(movementVk, true, false));
        Assert.False(service.DispatchDecodedKeyboardEvent(movementVk, false, true));
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(new[] { true, false }, sender.Transitions.Where(edge => edge.Key == Key.W).Select(edge => edge.IsDown));
    }

    [Theory]
    [InlineData("Color")]
    [InlineData("Crosshair")]
    [InlineData("RapidFire")]
    public async Task RecordMacroAsync_PreheldToggleReleased_ObservesPairWithoutActivatingAndReadmitsFreshPress(string toggle)
    {
        using var service = MacroPlaybackTests.Create(new RecordingInputSender(), out var profile);
        var requests = 0;
        switch (toggle)
        {
            case "Color":
                service.SetColorToggleKey(Key.F8);
                service.ColorVariantToggleRequested += (_, _) => requests++;
                break;
            case "Crosshair":
                service.SetCrosshairOffsetToggleKey(Key.F8);
                service.CrosshairOffsetToggleRequested += (_, _) => requests++;
                break;
            case "RapidFire":
                profile.RapidFire.IsEnabled = true;
                service.ReconcileProfileSettings(profile, ProfileChangeKind.RapidFire);
                service.SetRapidFireToggleKey(Key.F8);
                service.RapidFireArmChanged += (_, _) => requests++;
                break;
        }
        Assert.False(service.DispatchDecodedKeyboardEvent(0x77, true, false));
        Assert.Equal(1, requests);
        var recording = service.RecordMacroAsync(profile, profile.Macros.Definitions[0].Id, 30);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Recording);

        Assert.False(service.DispatchDecodedKeyboardEvent(0x77, false, true));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x77, true, false));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x77, false, true));
        Assert.Equal(1, requests);
        service.StopMacroRecording();
        await recording.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(service.DispatchDecodedKeyboardEvent(0x77, true, false));
        Assert.Equal(2, requests);
        Assert.False(service.DispatchDecodedKeyboardEvent(0x77, true, false));
        Assert.Equal(2, requests);
        Assert.False(service.DispatchDecodedKeyboardEvent(0x77, false, true));
    }

    [Fact]
    public async Task RecordMacroAsync_PhysicalCallbacks_PausesRemapsAndRestoresThemAfterStop()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile);
        ConfigureRemap(service, profile);
        await RemapA(service);
        Assert.Equal(new[] { (Key.B, true), (Key.B, false) }, sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
        sender.Transitions.Clear();

        var recording = service.RecordMacroAsync(profile, profile.Macros.Definitions[0].Id, 30);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Recording);
        Assert.Equal(IntPtr.Zero, Keyboard(service, NativeMethods.WM_KEYDOWN, 0x41));
        Assert.Equal(IntPtr.Zero, Keyboard(service, NativeMethods.WM_KEYUP, 0x41));
        Assert.Equal(IntPtr.Zero, Keyboard(service, NativeMethods.WM_KEYDOWN, 0x43, NativeMethods.KbdLlFlags.LLKHF_INJECTED));
        Assert.Equal(IntPtr.Zero, Keyboard(service, NativeMethods.WM_KEYUP, 0x43, NativeMethods.KbdLlFlags.LLKHF_INJECTED));
        Assert.Equal(IntPtr.Zero, Mouse(service, NativeMethods.WM_MOUSEHWHEEL, new NativeMethods.MSLLHOOKSTRUCT
        {
            pt = new NativeMethods.POINT { X = -123, Y = 456 },
            mouseData = unchecked((uint)(ushort)(short)-120 << 16)
        }));
        Assert.False(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(sender.Transitions);
        Assert.Empty(sender.MouseWheels);
        service.StopMacroRecording();

        var result = await recording.WaitAsync(TimeSpan.FromSeconds(3));
        var actions = result.Steps.Where(step => step.Kind != MacroStepKind.Wait).ToArray();
        Assert.Same(profile, result.OwnerProfile);
        Assert.Equal(profile.Macros.Definitions[0].Id, result.MacroId);
        Assert.False(result.AppendedBalancingReleases);
        Assert.Equal(MacroRecordingEndReason.Stopped, result.EndReason);
        Assert.Collection(actions,
            down => { Assert.Equal(MacroStepKind.KeyDown, down.Kind); Assert.Equal(Key.A, down.Key); },
            up => { Assert.Equal(MacroStepKind.KeyUp, up.Kind); Assert.Equal(Key.A, up.Key); },
            move => { Assert.Equal(MacroStepKind.MoveTo, move.Kind); Assert.Equal(-123, move.X); Assert.Equal(456, move.Y); },
            wheel => { Assert.Equal(MacroStepKind.MouseWheel, wheel.Kind); Assert.Equal(-120, wheel.WheelDelta); Assert.True(wheel.HorizontalWheel); });
        Assert.Equal(MacroSessionMode.Idle, service.GetMacroSession().Mode);

        await RemapA(service);
        Assert.Equal(new[] { (Key.B, true), (Key.B, false) }, sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
    }

    [Fact]
    public async Task RecordMacroAsync_PreheldKeyAndPartialTake_ExcludesOrphanUpBalancesAndReadmits()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile);
        Assert.False(service.DispatchDecodedKeyboardEvent(0x41, true, false));
        var recording = service.RecordMacroAsync(profile, profile.Macros.Definitions[0].Id, 20);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Recording);
        Assert.False(service.DispatchDecodedKeyboardEvent(0x41, false, true));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x42, true, false));
        service.StopMacroRecording();

        var result = await recording.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(result.AppendedBalancingReleases);
        Assert.Collection(result.Steps,
            down => { Assert.Equal(MacroStepKind.KeyDown, down.Kind); Assert.Equal(Key.B, down.Key); },
            up => { Assert.Equal(MacroStepKind.KeyUp, up.Kind); Assert.Equal(Key.B, up.Key); });
        Assert.Empty(sender.Transitions);
        Assert.False(service.DispatchDecodedKeyboardEvent(0x42, false, true));

        var second = service.RecordMacroAsync(profile, profile.Macros.Definitions[0].Id, 20);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Recording);
        service.StopMacroRecording();
        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(secondResult.SessionId > result.SessionId);
        Assert.Empty(secondResult.Steps);
        Assert.Equal(MacroSessionMode.Idle, service.GetMacroSession().Mode);
    }

    [Fact]
    public async Task RecordMacroAsync_AlreadyCancelled_CompletesAndReleasesAdmission()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.RecordMacroAsync(profile, profile.Macros.Definitions[0].Id, 20, cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Empty(result.Steps);
        Assert.NotNull(result.FailureReason);
        Assert.Equal(MacroSessionMode.Idle, service.GetMacroSession().Mode);
        var next = service.RecordMacroAsync(profile, profile.Macros.Definitions[0].Id, 20);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Recording);
        service.StopMacroRecording();
        Assert.Equal(MacroRecordingEndReason.Stopped, (await next.WaitAsync(TimeSpan.FromSeconds(3))).EndReason);
    }

    [Fact]
    public async Task RecordMacroAsync_CancelledWhilePriorDownIsBlocked_CompletesWithoutAbandoningOwedUp()
    {
        using var downEntered = new ManualResetEventSlim();
        using var releaseDown = new ManualResetEventSlim();
        var sender = new RecordingInputSender
        {
            KeyResult = (key, down, _) =>
            {
                if (key == Key.B && down)
                {
                    downEntered.Set();
                    releaseDown.Wait();
                }
                return true;
            }
        };
        using var service = MacroPlaybackTests.Create(sender, out var profile);
        using var cancellation = new CancellationTokenSource();
        ConfigureRemap(service, profile);
        try
        {
            Assert.True(service.DispatchDecodedKeyboardEvent(0x41, true, false));
            Assert.True(downEntered.Wait(TimeSpan.FromSeconds(3)));
            var recording = service.RecordMacroAsync(profile, profile.Macros.Definitions[0].Id, 20, cancellation.Token);
            MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.PreparingRecording);
            MacroPlaybackTests.WaitUntil(() => HasQueuedKeyUp(service, Key.B));
            cancellation.Cancel();

            var result = await recording.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Empty(result.Steps);
            Assert.NotNull(result.FailureReason);
        }
        finally
        {
            releaseDown.Set();
        }

        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains(sender.Transitions, edge => edge.Key == Key.B && !edge.IsDown);
    }

    private static void ConfigureAutoRun(InputHookService service, Profile profile)
    {
        profile.AutoRun.IsEnabled = true;
        profile.AutoRun.TriggerKey = Key.F8;
        profile.AutoRun.TriggerModifier = ModifierKeys.None;
        profile.AutoRun.SendMode = AutoRunSendMode.Foreground;
        profile.AutoRun.SprintEnabled = false;
        service.ReconcileProfileSettings(profile, ProfileChangeKind.AutoRun);
    }

    private static void ConfigureRemap(InputHookService service, Profile profile)
    {
        profile.CombinedMappings.IsEnabled = true;
        profile.CombinedMappings.Mappings =
        [
            new() { Source = InputTrigger.FromKey(Key.A), TargetKey = Key.B, SuppressOriginalKey = true }
        ];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.CombinedMappings);
    }

    private static bool HasQueuedKeyUp(InputHookService service, Key key)
    {
        var executor = (InputExecutor)typeof(InputHookService)
            .GetField("_inputExecutor", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var queue = (BlockingCollection<InputCommand>)typeof(InputExecutor)
            .GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(executor)!;
        return queue.Any(command => command.Key == key && !command.IsDown);
    }

    private static async Task RemapA(InputHookService service)
    {
        Assert.True(service.DispatchDecodedKeyboardEvent(0x41, true, false));
        Assert.True(service.DispatchDecodedKeyboardEvent(0x41, false, true));
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
    }

    private static IntPtr Keyboard(InputHookService service, int message, uint virtualKey, NativeMethods.KbdLlFlags flags = 0) =>
        Callback(service, "KeyboardCallback", message, new NativeMethods.KBDLLHOOKSTRUCT { vkCode = virtualKey, scanCode = 30, flags = flags });

    private static IntPtr Mouse(InputHookService service, int message, NativeMethods.MSLLHOOKSTRUCT data) =>
        Callback(service, "MouseCallback", message, data);

    private static IntPtr Callback<T>(InputHookService service, string name, int message, T data) where T : struct
    {
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        try
        {
            Marshal.StructureToPtr(data, pointer, false);
            return (IntPtr)typeof(InputHookService).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [0, (IntPtr)message, pointer])!;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}
