using sWinShortcuts.Services.Input;
using Xunit;

namespace Tests;

public sealed class CapsHoldWinKeyBlockerTests
{
    private const int LWIN = 0x5B;
    private const int RWIN = 0x5C;

    [Theory]
    [InlineData(LWIN)]
    [InlineData(RWIN)]
    public void Handle_WinPressedDuringHold_SwallowsDownRepeatsAndUp(int win)
    {
        var blocker = new CapsHoldWinKeyBlocker();

        Assert.True(Down(blocker, win, 1_000, blockActive: true));
        Assert.True(Down(blocker, win, 1_500, blockActive: true));
        Assert.True(Up(blocker, win, blockActive: true));

        // The pair is fully retired: a later press outside a hold passes through.
        Assert.False(Down(blocker, win, 9_000, blockActive: false));
        Assert.False(Up(blocker, win, blockActive: false));
    }

    [Fact]
    public void Handle_CapsReleasedBeforeWin_OwnsTimelyRepeatsAndUp()
    {
        var blocker = new CapsHoldWinKeyBlocker();

        Assert.True(Down(blocker, LWIN, 1_000, blockActive: true));
        Assert.True(Down(blocker, LWIN, 1_500, blockActive: false));
        Assert.True(Down(blocker, LWIN, 1_533, blockActive: false));
        Assert.True(Up(blocker, LWIN, blockActive: false));
    }

    [Fact]
    public void Handle_WinPreheldBeforeHold_PassesThroughUntouched()
    {
        var blocker = new CapsHoldWinKeyBlocker();

        Assert.False(Down(blocker, LWIN, 1_000, blockActive: false));
        Assert.False(Down(blocker, LWIN, 1_500, blockActive: true));
        Assert.False(Up(blocker, LWIN, blockActive: true));
    }

    [Fact]
    public void Handle_SidesAreIndependent()
    {
        var blocker = new CapsHoldWinKeyBlocker();

        Assert.False(Down(blocker, RWIN, 1_000, blockActive: false));
        Assert.True(Down(blocker, LWIN, 1_100, blockActive: true));
        Assert.False(Up(blocker, RWIN, blockActive: true));
        Assert.True(Up(blocker, LWIN, blockActive: true));
    }

    [Theory]
    [InlineData(1_000u, 2_600u)]
    [InlineData(uint.MaxValue - 100, 1_500u)]
    public void Handle_SwallowedUpMissed_LaterPressPassesAndKeepsItsUp(uint swallowedAt, uint nextPressAt)
    {
        var blocker = new CapsHoldWinKeyBlocker();

        Assert.True(Down(blocker, LWIN, swallowedAt, blockActive: true));
        // The UP never reached the hook; a press past the typematic gap is a new, real press.
        Assert.False(Down(blocker, LWIN, nextPressAt, blockActive: false));
        Assert.False(Up(blocker, LWIN, blockActive: false));
    }

    [Fact]
    public void Handle_UnknownEventTimeAfterHold_FailsOpen()
    {
        var blocker = new CapsHoldWinKeyBlocker();

        Assert.True(Down(blocker, LWIN, 1_000, blockActive: true));
        Assert.False(Down(blocker, LWIN, 0, blockActive: false));
        Assert.False(Up(blocker, LWIN, blockActive: false));
    }

    [Fact]
    public void Handle_TypematicRepeatAcrossTimerWrap_StaysOwned()
    {
        var blocker = new CapsHoldWinKeyBlocker();

        Assert.True(Down(blocker, LWIN, uint.MaxValue - 10, blockActive: true));
        Assert.True(Down(blocker, LWIN, 20, blockActive: false));
        Assert.True(Up(blocker, LWIN, blockActive: false));
    }

    [Fact]
    public void Handle_NonWinKeys_AreIgnored()
    {
        var blocker = new CapsHoldWinKeyBlocker();

        Assert.False(Down(blocker, 0x4D, 1_000, blockActive: true));
        Assert.False(Up(blocker, 0x4D, blockActive: true));
    }

    [Fact]
    public void ObserveUnfiltered_WinEventBypassedChain_RetiresSwallowDebt()
    {
        var blocker = new CapsHoldWinKeyBlocker();

        Assert.True(Down(blocker, LWIN, 1_000, blockActive: true));
        // A real press reached the system while features were inactive; its UP must pass.
        blocker.ObserveUnfiltered(LWIN, isKeyDown: true, isKeyUp: false);
        Assert.False(Up(blocker, LWIN, blockActive: false));
    }

    [Fact]
    public void Reset_ClearsDebtAndSeedsPreheldFromNativeState()
    {
        var blocker = new CapsHoldWinKeyBlocker();

        Assert.True(Down(blocker, LWIN, 1_000, blockActive: true));
        blocker.Reset(vk => vk == RWIN);

        Assert.False(Up(blocker, LWIN, blockActive: true));
        // RWIN was already down natively, so its repeats pass even during a hold.
        Assert.False(Down(blocker, RWIN, 1_100, blockActive: true));
    }

    private static bool Down(CapsHoldWinKeyBlocker blocker, int vk, uint time, bool blockActive) =>
        blocker.Handle(vk, isKeyDown: true, isKeyUp: false, time, blockActive);

    private static bool Up(CapsHoldWinKeyBlocker blocker, int vk, bool blockActive) =>
        blocker.Handle(vk, isKeyDown: false, isKeyUp: true, 0, blockActive);
}
