using System.Reflection;
using System.Windows.Input;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using sWinShortcuts.Services.Input;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class MacroCapsRecordingTests
{
    [Theory]
    [InlineData(false, Key.CapsLock)]
    [InlineData(true, Key.CapsLock)]
    [InlineData(false, Key.LeftCtrl)]
    [InlineData(true, Key.LeftCtrl)]
    public async Task RecordMacroAsync_AcknowledgedDoubleNormalCaps_CompletesSecondTapBeforeCapture(
        bool record, Key target)
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var destination);
        ConfigureGlobalCaps(service, target);
        Assert.True(service.DispatchDecodedKeyboardEvent(0x14, true, false));
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(new[] { true, false }, sender.Transitions.Select(edge => edge.IsDown));

        Task<MacroRecordingResult>? recording = null;
        if (record)
        {
            recording = service.RecordMacroAsync(destination, destination.Macros.Definitions[0].Id, 30);
            MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Recording);
            Assert.False(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        else
        {
            service.ResetInputStateForTesting();
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        }

        Assert.Equal(new[] { (target, true), (target, false), (target, true), (target, false) },
            sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
        Assert.True(service.DispatchDecodedKeyboardEvent(0x14, false, true));
        if (recording is not null)
        {
            service.StopMacroRecording();
            var result = await recording.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(MacroRecordingEndReason.Stopped, result.EndReason);
            Assert.Empty(result.Steps);
        }
        Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(4, sender.Transitions.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordMacroAsync_InitialCapsTapNotAcknowledged_DoesNotInventSecondTap(bool failedDown)
    {
        var sender = new RecordingInputSender(blockDummy: !failedDown, failFirstDown: failedDown);
        using var service = MacroPlaybackTests.Create(sender, out var destination);
        ConfigureGlobalCaps(service, Key.CapsLock);
        var runtime = Runtime(service);
        try
        {
            Task<bool>? blocker = null;
            if (!failedDown)
            {
                blocker = service.EnqueueDummyForTesting();
                Assert.True(sender.DummyEntered.Wait(TimeSpan.FromSeconds(2)));
            }
            Assert.True(service.DispatchDecodedKeyboardEvent(0x14, true, false));
            if (failedDown)
                Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));

            var recording = service.RecordMacroAsync(destination, destination.Macros.Definitions[0].Id, 30);
            MacroPlaybackTests.WaitUntil(() => runtime.RecordingPaused);
            sender.ReleaseDummy.Set();
            if (blocker is not null) Assert.True(await blocker.WaitAsync(TimeSpan.FromSeconds(3)));
            MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Recording);
            Assert.True(service.DispatchDecodedKeyboardEvent(0x14, false, true));
            service.StopMacroRecording();
            var result = await recording.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(MacroRecordingEndReason.Stopped, result.EndReason);
            Assert.Empty(result.Steps);
            Assert.Equal(failedDown ? new[] { true, false } : [],
                sender.Transitions.Select(edge => edge.IsDown));
        }
        finally
        {
            sender.ReleaseDummy.Set();
        }
    }

    [Theory]
    [InlineData(Key.CapsLock)]
    [InlineData(Key.LeftCtrl)]
    public async Task RecordMacroAsync_InitialCapsDownInFlight_DrainsBothTapsBeforeCapture(Key target)
    {
        var sender = new RecordingInputSender(blockFirstDown: true);
        using var service = MacroPlaybackTests.Create(sender, out var destination);
        ConfigureGlobalCaps(service, target);
        var runtime = Runtime(service);
        try
        {
            Assert.True(service.DispatchDecodedKeyboardEvent(0x14, true, false));
            Assert.True(sender.DownEntered.Wait(TimeSpan.FromSeconds(2)));
            var recording = service.RecordMacroAsync(destination, destination.Macros.Definitions[0].Id, 30);
            MacroPlaybackTests.WaitUntil(() => runtime.RecordingPaused);
            Assert.NotEqual(MacroSessionMode.Recording, service.GetMacroSession().Mode);
            sender.ReleaseDown.Set();
            MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Recording);

            Assert.Equal(new[] { (target, true), (target, false), (target, true), (target, false) },
                sender.Transitions.Select(edge => (edge.Key, edge.IsDown)));
            Assert.True(service.DispatchDecodedKeyboardEvent(0x14, false, true));
            service.StopMacroRecording();
            Assert.Empty((await recording.WaitAsync(TimeSpan.FromSeconds(3))).Steps);
        }
        finally
        {
            sender.ReleaseDown.Set();
        }
    }

    [Fact]
    public async Task RecordMacroAsync_CapsUpStillPending_RejectsCompensationAndCapture()
    {
        var sender = new RecordingInputSender { KeyResult = (_, down, _) => down };
        using var service = MacroPlaybackTests.Create(sender, out var destination);
        ConfigureGlobalCaps(service, Key.CapsLock);
        try
        {
            Assert.True(service.DispatchDecodedKeyboardEvent(0x14, true, false));
            Assert.True(await service.EnqueueDummyForTesting().WaitAsync(TimeSpan.FromSeconds(3)));
            var result = await service.RecordMacroAsync(destination, destination.Macros.Definitions[0].Id, 30)
                .WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(MacroRecordingEndReason.Faulted, result.EndReason);
            Assert.NotNull(result.FailureReason);
            Assert.Empty(result.Steps);
            Assert.Single(sender.Transitions, edge => edge.IsDown);
        }
        finally
        {
            sender.KeyResult = null;
        }
    }

    private static void ConfigureGlobalCaps(InputHookService service, Key target)
    {
        var windows = ProfileFactory.CreateWindowsProfile();
        windows.CapsLock.IsEnabled = true;
        windows.CapsLock.Mode = CapsLockMode.DoubleNormal;
        windows.CapsLock.IsRemapEnabled = target != Key.CapsLock;
        windows.CapsLock.RemapTarget = target;
        service.SetWindowsProfile(windows);
        service.DeactivateProfile(1);
    }

    private static InputRuntimeState Runtime(InputHookService service) => (InputRuntimeState)typeof(InputHookService)
        .GetField("_runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
}
