using System.Diagnostics;
using System.Threading;
using sWinShortcuts.Utilities;

namespace sWinShortcuts.Services.Input;

/// <summary>
/// Hook-thread, allocation-free owner of a running keybind capture. While capturing, every physical key down
/// is swallowed and forwarded to the sink; only keys whose down was swallowed have their up swallowed. Keys
/// already held when the capture begins are snapshotted: Windows saw their down, so their auto-repeat downs are
/// swallowed without being owned or forwarded, and their up passes through. The capture ends as soon as the sink
/// finishes; a still-held owned key's up then passes through as a lone up. An idle limit (nothing pressed) and
/// a hard limit fail open, so a capture always ends.
/// </summary>
internal sealed class KeyCaptureGate
{
    // Shared with KeyCaptureBox's own timer, so the UI and this fail-open safety net always agree.
    internal static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan HardLimit = TimeSpan.FromSeconds(30);
    internal static readonly long IdleLimitTicks = (long)(IdleLimit.TotalSeconds * Stopwatch.Frequency);
    internal static readonly long HardLimitTicks = (long)(HardLimit.TotalSeconds * Stopwatch.Frequency);

    private readonly bool[] _ownedDown = new bool[256];
    // Down before Begin: Windows already has their down, so the gate must never take ownership of their up.
    private readonly bool[] _preheldDown = new bool[256];
    private long _idleDeadline;
    private long _hardDeadline;
    // Non-null while a capture runs.
    private IKeyCaptureSink? _sink;

    /// <summary>The one volatile read the keyboard hot path pays while no capture runs.</summary>
    public bool IsActive => Volatile.Read(ref _sink) is not null;

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

        _idleDeadline = now + IdleLimitTicks;
        _hardDeadline = now + HardLimitTicks;
        Volatile.Write(ref _sink, sink);
    }

    /// <summary>Ends the capture unconditionally. A still-held owned key's up later passes through as a lone up.</summary>
    public void End() => Volatile.Write(ref _sink, null);

    /// <summary>Ends the capture only if <paramref name="sink"/> still owns it.</summary>
    public void End(IKeyCaptureSink sink) => Interlocked.CompareExchange(ref _sink, null, sink);

    /// <summary>Returns true when the physical event must be consumed.</summary>
    public bool TryHandle(int vk, bool isDown, bool isUp, long now)
    {
        var sink = Volatile.Read(ref _sink);
        if (sink is null || (uint)vk >= 256 || (!isDown && !isUp))
        {
            return false;
        }

        if (now - _hardDeadline > 0 || (now - _idleDeadline > 0 && !sink.HasStarted))
        {
            End();
            return false;
        }

        if (isDown)
        {
            if (_preheldDown[vk])
            {
                // Auto-repeat of a key held before the capture: keep it from the features, but never own it, or
                // its up would be swallowed after Windows saw its down (a stuck key).
                return true;
            }

            if (!KeyChordAccumulator.IsMomentaryKey(vk))
            {
                _ownedDown[vk] = true;
            }
        }
        else if (_ownedDown[vk])
        {
            _ownedDown[vk] = false;
        }
        else
        {
            // Held before the capture (or never seen): Windows saw its down, so it must see its up. A later
            // press of the same key is a fresh press.
            _preheldDown[vk] = false;
            return false;
        }

        if (sink.OnKey(vk, isDown) == KeyCaptureStatus.Finished)
        {
            // End before returning: keys pressed before the deferred apply runs reach the features normally.
            End(sink);
        }

        return true;
    }
}
