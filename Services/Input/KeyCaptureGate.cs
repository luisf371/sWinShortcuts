using System.Diagnostics;
using System.Threading;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.Services.Input;

/// <summary>
/// Hook-thread, allocation-free owner of a running keybind capture. While capturing, every physical key down
/// is swallowed and forwarded to the sink; only keys whose down was swallowed have their up swallowed. Keys
/// already held when the capture begins are snapshotted: Windows saw their down, so their auto-repeat downs are
/// swallowed without being owned or forwarded, and their up passes through. After the sink finishes, the gate
/// drains: new downs pass through and it keeps swallowing only the ups it owns. A capture always ends: an idle
/// limit (nothing pressed) and a hard limit fail open to <see cref="IsActive"/> false.
/// </summary>
internal sealed class KeyCaptureGate
{
    private const int STATE_INACTIVE = 0;
    private const int STATE_CAPTURING = 1;
    private const int STATE_DRAINING = 2;

    internal static readonly long IdleLimitTicks = Stopwatch.Frequency * 10;
    internal static readonly long HardLimitTicks = Stopwatch.Frequency * 30;

    private readonly bool[] _ownedDown = new bool[256];
    // Down before Begin: Windows already has their down, so the gate must never take ownership of their up.
    private readonly bool[] _preheldDown = new bool[256];
    private volatile int _state;
    private int _ownedCount;
    private bool _anyPressed;
    private long _idleDeadline;
    private long _hardDeadline;
    private IKeyCaptureSink? _sink;

    /// <summary>The one volatile read the keyboard hot path pays while no capture runs.</summary>
    public bool IsActive => _state != STATE_INACTIVE;

    internal bool IsDraining => _state == STATE_DRAINING;

    /// <summary>
    /// Starts a capture. <paramref name="isKeyDown"/> reports the keys already held (asked once per virtual key,
    /// off the hot path); their releases pass through to Windows.
    /// </summary>
    public void Begin(IKeyCaptureSink sink, long now, Func<int, bool>? isKeyDown = null)
    {
        Array.Clear(_ownedDown);
        Array.Clear(_preheldDown);
        if (isKeyDown is not null)
        {
            for (var vk = 1; vk < 256; vk++)
            {
                _preheldDown[vk] = isKeyDown(vk);
            }
        }

        _ownedCount = 0;
        _anyPressed = false;
        _idleDeadline = now + IdleLimitTicks;
        _hardDeadline = now + HardLimitTicks;
        Volatile.Write(ref _sink, sink);
        _state = STATE_CAPTURING;
    }

    /// <summary>Ends the capture unconditionally. A still-held owned key's up later passes through as a lone up.</summary>
    public void End()
    {
        _state = STATE_INACTIVE;
        Volatile.Write(ref _sink, null);
        Array.Clear(_ownedDown);
        Array.Clear(_preheldDown);
        _ownedCount = 0;
    }

    /// <summary>Ends the capture only if <paramref name="sink"/> still owns it.</summary>
    public void End(IKeyCaptureSink sink)
    {
        if (ReferenceEquals(Volatile.Read(ref _sink), sink))
        {
            End();
        }
    }

    /// <summary>Returns true when the physical event must be consumed.</summary>
    public bool TryHandle(int vk, bool isDown, bool isUp, long now)
    {
        var state = _state;
        if (state == STATE_INACTIVE || (uint)vk >= 256 || (!isDown && !isUp))
        {
            return false;
        }

        if (now - _hardDeadline > 0 || (!_anyPressed && now - _idleDeadline > 0))
        {
            End();
            return false;
        }

        if (state == STATE_DRAINING)
        {
            if (!_ownedDown[vk])
            {
                return false;
            }

            // An owned key's auto-repeat stays swallowed; its up releases ownership.
            if (isUp)
            {
                ReleaseOwned(vk);
            }

            return true;
        }

        var sink = Volatile.Read(ref _sink);
        if (sink is null)
        {
            End();
            return false;
        }

        var passThrough = false;
        if (isDown)
        {
            if (_preheldDown[vk])
            {
                // Auto-repeat of a key held before the capture: keep it from the features, but never own it, or
                // its up would be swallowed after Windows saw its down (a stuck key).
                return true;
            }

            if (!_ownedDown[vk])
            {
                _ownedDown[vk] = true;
                _ownedCount++;
            }

            _anyPressed = true;
        }
        else if (!_ownedDown[vk])
        {
            // Held before the capture: Windows saw its down, so it must see its up.
            if (!_preheldDown[vk])
            {
                return false;
            }

            // Forward the release so the sink clears its own held-before flag; a later fresh press then counts.
            _preheldDown[vk] = false;
            passThrough = true;
        }
        else
        {
            _ownedDown[vk] = false;
            _ownedCount--;
        }

        if (sink.OnKey(vk, isDown) == KeyCaptureStatus.Finished && _state == STATE_CAPTURING)
        {
            // Switch before returning: keys pressed before the deferred apply runs reach the features normally.
            _state = _ownedCount == 0 ? STATE_INACTIVE : STATE_DRAINING;
            if (_ownedCount == 0)
            {
                Volatile.Write(ref _sink, null);
            }
        }

        return !passThrough;
    }

    private void ReleaseOwned(int vk)
    {
        _ownedDown[vk] = false;
        if (--_ownedCount <= 0)
        {
            End();
        }
    }
}
