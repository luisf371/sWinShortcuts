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
