using System;
using System.Collections.Generic;
using sWinShortcuts.Models;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;

namespace Tests;

// Headless (no Application.Current): the window layer is skipped and the applied-status
// bookkeeping — subscription wiring, read-at-execution, dedup, dispose fence — is what the
// internal test seam (enqueue) makes observable. No WPF window is ever created.
public sealed class RapidFireStatusServiceTests
{
    [Fact]
    public void Handlers_EnqueueOnly_NeverApplyInline()
    {
        var input = new FakeInputHookService();
        var pending = new Queue<Action>();
        using var service = new RapidFireStatusService(new NullLoggerService(), input, enqueue: pending.Enqueue);

        input.RapidFireArmStatus = RapidFireArmStatus.Ready;
        input.RaiseRapidFireArmChanged();

        // The handler only SCHEDULED the apply — nothing ran on the raiser's thread. This is the
        // contract that makes raising from the keyboard hook / watcher publication lock safe.
        Assert.Equal(RapidFireArmStatus.Off, service.AppliedStatus);

        Assert.Single(pending)();
        Assert.Equal(RapidFireArmStatus.Ready, service.AppliedStatus);
    }

    [Fact]
    public void AppliesStatusFromArmAndProfileEvents_AndDedupsDuplicates()
    {
        var input = new FakeInputHookService();
        using var service = new RapidFireStatusService(new NullLoggerService(), input, enqueue: a => a());

        Assert.Equal(RapidFireArmStatus.Off, service.AppliedStatus);
        Assert.Equal(0, service.AppliedCount);

        input.RapidFireArmStatus = RapidFireArmStatus.Ready;
        input.RaiseRapidFireArmChanged();
        Assert.Equal(RapidFireArmStatus.Ready, service.AppliedStatus);
        Assert.Equal(1, service.AppliedCount);

        // Contractual spurious duplicate: re-queried at execution time, found equal, deduped.
        input.RaiseRapidFireArmChanged();
        Assert.Equal(RapidFireArmStatus.Ready, service.AppliedStatus);
        Assert.Equal(1, service.AppliedCount);

        // ActiveProfileChanged also refreshes (covers the activation catch-up raise path).
        input.RapidFireArmStatus = RapidFireArmStatus.ArmedNotReady;
        input.RaiseActiveProfileChanged(new Profile { Name = "Game", Executable = "game.exe" });
        Assert.Equal(RapidFireArmStatus.ArmedNotReady, service.AppliedStatus);
        Assert.Equal(2, service.AppliedCount);

        input.RapidFireArmStatus = RapidFireArmStatus.Off;
        input.RaiseRapidFireArmChanged();
        Assert.Equal(RapidFireArmStatus.Off, service.AppliedStatus);
        Assert.Equal(3, service.AppliedCount);
    }

    [Fact]
    public void Dispose_FencesSubsequentEvents()
    {
        var input = new FakeInputHookService();
        var service = new RapidFireStatusService(new NullLoggerService(), input, enqueue: a => a());

        input.RapidFireArmStatus = RapidFireArmStatus.Ready;
        input.RaiseRapidFireArmChanged();
        Assert.Equal(RapidFireArmStatus.Ready, service.AppliedStatus);

        service.Dispose();

        // Unsubscribed + fenced: a late raise can neither apply nor throw.
        input.RapidFireArmStatus = RapidFireArmStatus.Off;
        input.RaiseRapidFireArmChanged();
        input.RaiseActiveProfileChanged(null);
        Assert.Equal(RapidFireArmStatus.Ready, service.AppliedStatus);
    }

    [Fact]
    public void QueuedApplyAfterDispose_NoOps()
    {
        var input = new FakeInputHookService();
        var pending = new Queue<Action>();
        var service = new RapidFireStatusService(new NullLoggerService(), input, enqueue: pending.Enqueue);

        input.RapidFireArmStatus = RapidFireArmStatus.Ready;
        input.RaiseRapidFireArmChanged();
        Assert.Single(pending);

        service.Dispose();

        // A callback queued before Dispose but not yet run must not touch anything post-fence.
        pending.Dequeue()();
        Assert.Equal(RapidFireArmStatus.Off, service.AppliedStatus);
        Assert.Equal(0, service.AppliedCount);
    }
}
