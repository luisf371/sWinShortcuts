using System.Threading;

namespace sWinShortcuts.Services.Input;

/// <summary>
/// Swallows a physical Win press that starts while a remapped Caps 2x pair is held. An accidental
/// Win would otherwise open Start (focus leaves the game before the closing tap) or turn the
/// closing tap into a Win+key shortcut. A swallowed press owns its repeats and its UP, even after
/// Caps is released; a Win already down before the pair passes through untouched. Hook-thread
/// state (allocation- and lock-free); Reset may run at input-stream boundaries on other threads.
/// </summary>
internal sealed class CapsHoldWinKeyBlocker
{
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    // Longest keyboard delay (1000 ms) plus margin; a longer silence means the UP was missed.
    private const uint MISSED_UP_GAP_MS = 1500;

    private readonly int[] _swallowed = new int[2];
    private readonly int[] _passedDown = new int[2];
    private readonly uint[] _lastSwallowedTime = new uint[2];

    internal static bool IsWinKey(int virtualKey) => virtualKey is VK_LWIN or VK_RWIN;

    /// <summary>Returns whether the physical Win event must be consumed.</summary>
    internal bool Handle(int virtualKey, bool isKeyDown, bool isKeyUp, uint eventTime, bool blockActive)
    {
        var side = Side(virtualKey);
        if (side < 0)
        {
            return false;
        }

        if (isKeyUp)
        {
            Volatile.Write(ref _passedDown[side], 0);
            return Interlocked.Exchange(ref _swallowed[side], 0) != 0;
        }
        if (!isKeyDown)
        {
            return false;
        }

        if (Volatile.Read(ref _swallowed[side]) != 0)
        {
            // Owned while the pair is held; afterwards only a timely repeat. An unknown or long gap
            // means the UP was missed: fail open so a later real press can never lose its UP.
            if (blockActive ||
                (eventTime != 0 && unchecked(eventTime - _lastSwallowedTime[side]) <= MISSED_UP_GAP_MS))
            {
                _lastSwallowedTime[side] = eventTime;
                return true;
            }
            Volatile.Write(ref _swallowed[side], 0);
        }
        else if (blockActive && Volatile.Read(ref _passedDown[side]) == 0)
        {
            _lastSwallowedTime[side] = eventTime;
            Volatile.Write(ref _swallowed[side], 1);
            return true;
        }

        Volatile.Write(ref _passedDown[side], 1);
        return false;
    }

    /// <summary>
    /// A Win event that bypassed the feature chain (hook replacement or stopped features) reached
    /// the system, so it retires any swallow debt for that side.
    /// </summary>
    internal void ObserveUnfiltered(int virtualKey, bool isKeyDown, bool isKeyUp)
    {
        var side = Side(virtualKey);
        if (side < 0)
        {
            return;
        }

        Volatile.Write(ref _swallowed[side], 0);
        if (isKeyDown)
        {
            Volatile.Write(ref _passedDown[side], 1);
        }
        else if (isKeyUp)
        {
            Volatile.Write(ref _passedDown[side], 0);
        }
    }

    /// <summary>
    /// Input-stream boundary: pending UPs may have been missed. Fails open (a stray Win UP is
    /// harmless) and re-seeds pass-through state; native state never reports a swallowed key.
    /// </summary>
    internal void Reset(Func<int, bool> isPhysicalKeyDown)
    {
        Volatile.Write(ref _swallowed[0], 0);
        Volatile.Write(ref _swallowed[1], 0);
        Volatile.Write(ref _passedDown[0], isPhysicalKeyDown(VK_LWIN) ? 1 : 0);
        Volatile.Write(ref _passedDown[1], isPhysicalKeyDown(VK_RWIN) ? 1 : 0);
    }

    private static int Side(int virtualKey) => virtualKey switch
    {
        VK_LWIN => 0,
        VK_RWIN => 1,
        _ => -1
    };
}
