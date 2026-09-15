using System.Windows.Input;
using sWinShortcuts.Models;
using Tests.Fakes;
using Xunit;

namespace Tests;

public sealed class MacroProfileReconciliationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconcileProfileSettings_InactiveRemovalAroundForegroundRepublish_PreservesActiveMacro(bool activateBeforeRemoval)
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var active,
            new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A });
        var removed = new Profile { Name = "Other", Executable = "other.exe" };

        // Removal queues a foreground republish before reconciliation; its worker can settle
        // the same active profile either before or after the Removed notification.
        service.SetForegroundIdentity((IntPtr)100, 42, "game.exe", 2);
        if (activateBeforeRemoval) service.ActivateProfile(active, 2);
        service.ReconcileProfileSettings(removed, ProfileChangeKind.Removed);
        if (!activateBeforeRemoval) service.ActivateProfile(active, 2);

        Assert.Same(active, service.ActiveProfile);
        MacroPlaybackTests.Press(service, 0x75);
        MacroPlaybackTests.WaitUntil(() => sender.Transitions.Any(x => x.Key == Key.A && !x.IsDown));
    }

    [Fact]
    public void ReconcileProfileSettings_ActiveOwnerRemoved_RetiresPlaybackAndShortcut()
    {
        var sender = new RecordingInputSender();
        using var service = MacroPlaybackTests.Create(sender, out var active,
            new MacroStep { Kind = MacroStepKind.KeyDown, Key = Key.A },
            new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 30000 },
            new MacroStep { Kind = MacroStepKind.KeyUp, Key = Key.A });
        MacroPlaybackTests.Press(service, 0x75);
        MacroPlaybackTests.WaitUntil(() => sender.Transitions.Any(x => x.Key == Key.A && x.IsDown));

        service.SetForegroundIdentity((IntPtr)100, 42, "game.exe", 2);
        service.ReconcileProfileSettings(active, ProfileChangeKind.Removed);

        Assert.Null(service.ActiveProfile);
        MacroPlaybackTests.WaitUntil(() => service.GetMacroSession().Mode == MacroSessionMode.Idle);
        Assert.False(service.DispatchDecodedKeyboardEvent(0x75, true, false));
        Assert.False(service.DispatchDecodedKeyboardEvent(0x75, false, true));
        Assert.Collection(sender.Transitions,
            transition => { Assert.Equal(Key.A, transition.Key); Assert.True(transition.IsDown); },
            transition => { Assert.Equal(Key.A, transition.Key); Assert.False(transition.IsDown); });
    }
}
