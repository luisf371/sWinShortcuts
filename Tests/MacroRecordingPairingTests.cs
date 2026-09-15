using System.Diagnostics;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class MacroRecordingPairingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordMacroAsync_PreheldMappedKeyReleased_AdmitsNextMapping(bool suppressOriginal)
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile);
        ConfigureMapping(service, profile, suppressOriginal);
        Assert.Equal(suppressOriginal, service.DispatchDecodedKeyboardEvent(0x41, true, false));
        await Drain(service);
        Assert.Equal(new[] { (Key.B, true) }, sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));

        var recording = BeginRecording(service, profile);
        Assert.Equal(suppressOriginal, service.DispatchDecodedKeyboardEvent(0x41, false, true));
        service.StopMacroRecording();
        await recording.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new[] { (Key.B, true), (Key.B, false) }, sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
        sender.Transitions.Clear();

        Assert.Equal(suppressOriginal, service.DispatchDecodedKeyboardEvent(0x41, true, false));
        Assert.Equal(suppressOriginal, service.DispatchDecodedKeyboardEvent(0x41, false, true));
        await Drain(service);
        Assert.Equal(new[] { (Key.B, true), (Key.B, false) }, sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
    }

    [Fact]
    public async Task RecordMacroAsync_PreheldAltTriggerReleased_AdmitsNextAltGesture()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile);
        profile.AltKeyboard.IsEnabled = true;
        profile.AltKeyboard.Bindings[Key.Q] = new() { TapKey = Key.B };
        service.ReconcileProfileSettings(profile, ProfileChangeKind.AltKeyboard);
        Assert.False(service.DispatchDecodedKeyboardEvent(0x51, true, false));

        var recording = BeginRecording(service, profile);
        Assert.False(service.DispatchDecodedKeyboardEvent(0x51, false, true));
        service.StopMacroRecording();
        await recording.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(service.DispatchDecodedKeyboardEvent(0xA4, true, false));
        Assert.True(service.DispatchDecodedKeyboardEvent(0x51, true, false));
        Assert.True(service.DispatchDecodedKeyboardEvent(0x51, false, true));
        await Drain(service);
        Assert.Equal(new[] { (Key.B, true), (Key.B, false) }, sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA4, false, true));
    }

    [Fact]
    public async Task RecordMacroAsync_PreheldPanicTriggerReleased_AdmitsNextEarlyCancel()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile);
        profile.RightClickHoldBreath.IsEnabled = true;
        profile.RightClickHoldBreath.HoldBreathKey = Key.B;
        profile.RightClickHoldBreath.DelayMilliseconds = 60_000;
        profile.RightClickHoldBreath.PanicTrigger = InputTrigger.FromKey(Key.Q);
        service.ReconcileProfileSettings(profile, ProfileChangeKind.HoldBreath);
        Assert.False(service.DispatchDecodedKeyboardEvent(0x51, true, false));

        var recording = BeginRecording(service, profile);
        Assert.False(service.DispatchDecodedKeyboardEvent(0x51, false, true));
        service.StopMacroRecording();
        await recording.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(service.DispatchDecodedMouseEvent(NativeMethods.WM_RBUTTONDOWN, 0));
        Assert.True(service.DispatchDecodedKeyboardEvent(0x51, true, false));
        Assert.True(service.DispatchDecodedKeyboardEvent(0x51, false, true));
        service.GetGesturesForTesting().FireHoldBreathTimerForTesting();
        Assert.False(service.DispatchDecodedMouseEvent(NativeMethods.WM_RBUTTONUP, 0));
        await Drain(service);
        Assert.Empty(sender.Transitions);
    }

    [Fact]
    public async Task RecordMacroAsync_HeldSprintReleasedAfterAutoRunStarts_PreservesOriginalPassThrough()
    {
        var sender = new RecordingInputSender();
        var transport = FakeAutoRunTransport.MatchingForeground();
        using var service = new InputHookService(new NullLoggerService(), sender, Stopwatch.GetTimestamp,
            vk => (transport.GetAsyncKeyState(vk) & 0x8000) != 0, transport);
        service.StartInputExecutorForTesting();
        service.AdvancedModeEnabled = true;
        var profile = new Profile { Name = "Game", Executable = "game.exe" };
        profile.Macros.IsEnabled = true;
        profile.Macros.Definitions = [new MacroDefinition { IsEnabled = true, ShortcutKey = Key.F6 }];
        profile.AutoRun.IsEnabled = true;
        profile.AutoRun.TriggerKey = Key.F8;
        profile.AutoRun.TriggerModifier = ModifierKeys.None;
        profile.AutoRun.SprintEnabled = true;
        profile.AutoRun.SprintKey = Key.LeftShift;
        profile.AutoRun.SprintMode = SprintActivation.Hold;
        service.ConfigureActiveProfileForTesting(profile, 1, false);
        service.ReconcileProfileSettings(profile, ProfileChangeKind.Macros | ProfileChangeKind.AutoRun);

        var recording = BeginRecording(service, profile);
        transport.KeyStates[0xA0] = unchecked((short)0x8000);
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA0, true, false));
        service.StopMacroRecording();
        await recording.WaitAsync(TimeSpan.FromSeconds(3));

        MacroPlaybackTests.Press(service, 0x77);
        await Drain(service);
        Assert.Contains(sender.Transitions, edge => edge.Key == Key.W && edge.IsDown);
        transport.KeyStates[0xA0] = 0;
        Assert.False(service.DispatchDecodedKeyboardEvent(0xA0, false, true));
        MacroPlaybackTests.Press(service, 0x77);
        await Drain(service);
    }

    [Fact]
    public async Task RecordMacroAsync_RightButtonHeldAcrossStop_PreservesRightClickOnlyMapping()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var profile);
        ConfigureMapping(service, profile, suppressOriginal: true, rightClickOnly: true);
        Assert.False(service.DispatchDecodedMouseEvent(NativeMethods.WM_RBUTTONDOWN, 0));

        var recording = BeginRecording(service, profile);
        service.StopMacroRecording();
        await recording.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(service.DispatchDecodedKeyboardEvent(0x41, true, false));
        Assert.True(service.DispatchDecodedKeyboardEvent(0x41, false, true));
        await Drain(service);
        Assert.Equal(new[] { (Key.B, true), (Key.B, false) }, sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
        Assert.False(service.DispatchDecodedMouseEvent(NativeMethods.WM_RBUTTONUP, 0));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x41, true, false));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x41, false, true));
        await Drain(service);
        Assert.Equal(2, sender.Transitions.Count);
    }

    private static Task<MacroRecordingResult> BeginRecording(InputHookService service, Profile profile)
    {
        var recording = service.RecordMacroAsync(profile, profile.Macros.Definitions[0].Id, 30);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Recording);
        return recording;
    }

    private static void ConfigureMapping(InputHookService service, Profile profile, bool suppressOriginal, bool rightClickOnly = false)
    {
        profile.CombinedMappings.IsEnabled = true;
        profile.CombinedMappings.Mappings =
        [
            new() { Source = InputTrigger.FromKey(Key.A), TargetKey = Key.B,
                SuppressOriginalKey = suppressOriginal, RightClickOnly = rightClickOnly }
        ];
        service.ReconcileProfileSettings(profile, ProfileChangeKind.CombinedMappings);
    }

    private static async Task Drain(InputHookService service) =>
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
}
