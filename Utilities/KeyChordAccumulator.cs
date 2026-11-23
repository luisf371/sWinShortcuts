using System;
using System.Windows.Input;
using sWinShortcuts.Models;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace sWinShortcuts.Utilities;

public enum KeyCaptureStatus
{
    Pending,
    Finished
}

public enum KeyChordRejection
{
    None,
    MultipleKeys,
    UnsupportedKey
}

/// <summary>Outcome of one finished capture: a chord, or the reason it was rejected.</summary>
public readonly record struct KeyChordResult(KeyChord Chord, KeyChordRejection Rejection)
{
    public bool IsCompleted => Rejection == KeyChordRejection.None;

    public static KeyChordResult Completed(KeyChord chord) => new(chord, KeyChordRejection.None);

    public static KeyChordResult Rejected(KeyChordRejection rejection) => new(KeyChord.None, rejection);
}

/// <summary>
/// Turns the physical edges of one capture into a <see cref="KeyChord"/>. The capture starts on the first
/// press and finishes when every input pressed during it is released. Modifiers become side-agnostic
/// prefixes; exactly one non-modifier key, mouse button, or wheel direction is the primary input. When only
/// modifiers are pressed, the last one pressed becomes the (sided) primary key. One instance is reused across
/// captures, and the edge methods do not allocate.
/// </summary>
public sealed class KeyChordAccumulator
{
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    private const int PRIMARY_NONE = 0;
    private const int PRIMARY_KEY = 1;
    private const int PRIMARY_MOUSE = 2;
    private const int PRIMARY_WHEEL = 3;

    private static readonly int[] ModifierVks =
    [
        VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN,
        VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU
    ];

    private readonly bool[] _preheld = new bool[256];
    private readonly bool[] _keyDown = new bool[256];
    // Indexed by the MouseButton value (1..5).
    private readonly bool[] _mouseDown = new bool[6];
    private int _downCount;
    private bool _started;
    private bool _finished;
    private bool _multiple;
    private ModifierKeys _modifiers;
    // One bit per ModifierVks entry pressed during the capture.
    private int _modifierVkMask;
    private int _lastModifierVk;
    private int _primaryKind;
    private int _primaryVk;
    private MouseButton _primaryButton;
    private MouseWheelDirection _primaryWheel;

    public bool HasStarted => _started;

    public KeyChordResult Result { get; private set; }

    /// <summary>Starts a new capture. Keys already held are ignored until they are released.</summary>
    public void Begin(ReadOnlySpan<int> preheldVks)
    {
        Array.Clear(_preheld);
        Array.Clear(_keyDown);
        Array.Clear(_mouseDown);
        _downCount = 0;
        _started = false;
        _finished = false;
        _multiple = false;
        _modifiers = ModifierKeys.None;
        _modifierVkMask = 0;
        _lastModifierVk = 0;
        _primaryKind = PRIMARY_NONE;
        _primaryVk = 0;
        _primaryButton = default;
        _primaryWheel = default;
        Result = default;
        foreach (var vk in preheldVks)
        {
            if ((uint)vk < 256)
            {
                _preheld[vk] = true;
            }
        }
    }

    public KeyCaptureStatus OnKeyDown(int vk)
    {
        if (_finished)
        {
            return KeyCaptureStatus.Finished;
        }

        if ((uint)vk >= 256 || vk == 0 || _preheld[vk] || _keyDown[vk])
        {
            // Out of range, still held from before the capture, or an auto-repeat.
            return KeyCaptureStatus.Pending;
        }

        _keyDown[vk] = true;
        _downCount++;
        _started = true;
        var flag = ModifierForVirtualKey(vk);
        if (flag != ModifierKeys.None)
        {
            _modifiers |= flag;
            _modifierVkMask |= 1 << Array.IndexOf(ModifierVks, vk);
            _lastModifierVk = vk;
        }
        else if (_primaryKind == PRIMARY_NONE)
        {
            _primaryKind = PRIMARY_KEY;
            _primaryVk = vk;
        }
        else if (_primaryKind != PRIMARY_KEY || _primaryVk != vk)
        {
            _multiple = true;
        }

        return KeyCaptureStatus.Pending;
    }

    public KeyCaptureStatus OnKeyUp(int vk)
    {
        if (_finished)
        {
            return KeyCaptureStatus.Finished;
        }

        if ((uint)vk >= 256)
        {
            return KeyCaptureStatus.Pending;
        }

        if (_preheld[vk])
        {
            // A key held before the capture: its release neither starts nor ends the chord.
            _preheld[vk] = false;
            return KeyCaptureStatus.Pending;
        }

        if (!_keyDown[vk])
        {
            return KeyCaptureStatus.Pending;
        }

        _keyDown[vk] = false;
        _downCount--;
        return TryFinish();
    }

    public KeyCaptureStatus OnMouseDown(MouseButton button)
    {
        if (_finished)
        {
            return KeyCaptureStatus.Finished;
        }

        var index = (int)button;
        if ((uint)index >= (uint)_mouseDown.Length || index == 0 || _mouseDown[index])
        {
            return KeyCaptureStatus.Pending;
        }

        _mouseDown[index] = true;
        _downCount++;
        _started = true;
        if (_primaryKind == PRIMARY_NONE)
        {
            _primaryKind = PRIMARY_MOUSE;
            _primaryButton = button;
        }
        else if (_primaryKind != PRIMARY_MOUSE || _primaryButton != button)
        {
            _multiple = true;
        }

        return KeyCaptureStatus.Pending;
    }

    public KeyCaptureStatus OnMouseUp(MouseButton button)
    {
        if (_finished)
        {
            return KeyCaptureStatus.Finished;
        }

        var index = (int)button;
        if ((uint)index >= (uint)_mouseDown.Length || !_mouseDown[index])
        {
            return KeyCaptureStatus.Pending;
        }

        _mouseDown[index] = false;
        _downCount--;
        return TryFinish();
    }

    /// <summary>A wheel notch is a press and release in one; it finishes the chord when nothing else is held.</summary>
    public KeyCaptureStatus OnWheel(MouseWheelDirection direction)
    {
        if (_finished)
        {
            return KeyCaptureStatus.Finished;
        }

        if (!Enum.IsDefined(direction))
        {
            return KeyCaptureStatus.Pending;
        }

        _started = true;
        if (_primaryKind == PRIMARY_NONE)
        {
            _primaryKind = PRIMARY_WHEEL;
            _primaryWheel = direction;
        }
        else if (_primaryKind != PRIMARY_WHEEL || _primaryWheel != direction)
        {
            _multiple = true;
        }

        return TryFinish();
    }

    /// <summary>Side-agnostic modifier flag for a virtual key, or None for a non-modifier key.</summary>
    public static ModifierKeys ModifierForVirtualKey(int vk) => vk switch
    {
        VK_SHIFT or VK_LSHIFT or VK_RSHIFT => ModifierKeys.Shift,
        VK_CONTROL or VK_LCONTROL or VK_RCONTROL => ModifierKeys.Control,
        VK_MENU or VK_LMENU or VK_RMENU => ModifierKeys.Alt,
        VK_LWIN or VK_RWIN => ModifierKeys.Windows,
        _ => ModifierKeys.None
    };

    private KeyCaptureStatus TryFinish()
    {
        if (!_started || _downCount > 0)
        {
            return KeyCaptureStatus.Pending;
        }

        _finished = true;
        Result = BuildResult();
        return KeyCaptureStatus.Finished;
    }

    private KeyChordResult BuildResult()
    {
        if (_multiple)
        {
            return KeyChordResult.Rejected(KeyChordRejection.MultipleKeys);
        }

        switch (_primaryKind)
        {
            case PRIMARY_KEY:
                return KeyInteropUtilities.FromVirtualKey(_primaryVk) is { } key && KeyCatalog.IsAssignable(key)
                    ? KeyChordResult.Completed(KeyChord.FromKey(key, _modifiers))
                    : KeyChordResult.Rejected(KeyChordRejection.UnsupportedKey);
            case PRIMARY_MOUSE:
                return KeyChordResult.Completed(KeyChord.FromTrigger(InputTrigger.FromMouseButton(_primaryButton), _modifiers));
            case PRIMARY_WHEEL:
                return KeyChordResult.Completed(KeyChord.FromTrigger(InputTrigger.FromWheel(_primaryWheel), _modifiers));
        }

        // Only modifiers: the last one pressed is the primary key, with its own side; the rest are prefixes.
        if (KeyInteropUtilities.FromVirtualKey(_lastModifierVk) is not { } modifierKey || !KeyCatalog.IsAssignable(modifierKey))
        {
            return KeyChordResult.Rejected(KeyChordRejection.UnsupportedKey);
        }

        // Rebuild the prefixes from every other modifier key pressed, so Left Shift + Right Shift stays Shift+RightShift.
        var prefixes = ModifierKeys.None;
        for (var bit = 0; bit < ModifierVks.Length; bit++)
        {
            if ((_modifierVkMask & (1 << bit)) != 0 && ModifierVks[bit] != _lastModifierVk)
            {
                prefixes |= ModifierForVirtualKey(ModifierVks[bit]);
            }
        }

        return KeyChordResult.Completed(KeyChord.FromKey(modifierKey, prefixes));
    }
}
