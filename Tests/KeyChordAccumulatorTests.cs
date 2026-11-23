using System.Windows.Input;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class KeyChordAccumulatorTests
{
    private const int VK_E = 0x45;
    private const int VK_F = 0x46;
    private const int VK_F5 = 0x74;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_LWIN = 0x5B;
    private const int VK_PROCESSKEY = 0xE5;

    [Fact]
    public void Capture_EThenCtrl_GivesCtrlE()
    {
        var accumulator = Started();
        Assert.Equal(KeyCaptureStatus.Pending, accumulator.OnKeyDown(VK_E));
        Assert.Equal(KeyCaptureStatus.Pending, accumulator.OnKeyDown(VK_LCONTROL));
        Assert.Equal(KeyCaptureStatus.Pending, accumulator.OnKeyUp(VK_E));
        Assert.Equal(KeyCaptureStatus.Finished, accumulator.OnKeyUp(VK_LCONTROL));

        Assert.Equal(KeyChordResult.Completed(KeyChord.FromKey(Key.E, ModifierKeys.Control)), accumulator.Result);
        Assert.Equal("Ctrl+E", accumulator.Result.Chord.ToDisplayText());
    }

    [Fact]
    public void Capture_CtrlThenE_GivesCtrlE()
    {
        var accumulator = Started();
        accumulator.OnKeyDown(VK_LCONTROL);
        accumulator.OnKeyDown(VK_E);
        accumulator.OnKeyUp(VK_E);
        Assert.Equal(KeyCaptureStatus.Finished, accumulator.OnKeyUp(VK_LCONTROL));

        Assert.Equal(KeyChord.FromKey(Key.E, ModifierKeys.Control), accumulator.Result.Chord);
    }

    public static TheoryData<int> ModifierSubsets()
    {
        var data = new TheoryData<int>();
        for (var mask = 1; mask < 16; mask++) data.Add(mask);
        return data;
    }

    [Theory]
    [MemberData(nameof(ModifierSubsets))]
    public void Capture_EveryModifierSubsetWithE_StoresTheFlags(int mask)
    {
        (int Vk, ModifierKeys Flag)[] modifiers =
        [
            (VK_LCONTROL, ModifierKeys.Control),
            (VK_LMENU, ModifierKeys.Alt),
            (VK_LSHIFT, ModifierKeys.Shift),
            (VK_LWIN, ModifierKeys.Windows)
        ];
        var accumulator = Started();
        var expected = ModifierKeys.None;
        for (var bit = 0; bit < modifiers.Length; bit++)
        {
            if ((mask & (1 << bit)) == 0) continue;
            accumulator.OnKeyDown(modifiers[bit].Vk);
            expected |= modifiers[bit].Flag;
        }

        accumulator.OnKeyDown(VK_E);
        var status = accumulator.OnKeyUp(VK_E);
        for (var bit = 0; bit < modifiers.Length; bit++)
        {
            if ((mask & (1 << bit)) != 0) status = accumulator.OnKeyUp(modifiers[bit].Vk);
        }

        Assert.Equal(KeyCaptureStatus.Finished, status);
        Assert.Equal(KeyChord.FromKey(Key.E, expected), accumulator.Result.Chord);
    }

    [Fact]
    public void Capture_ShiftOnly_GivesSidedLeftShift()
    {
        var accumulator = Started();
        accumulator.OnKeyDown(VK_LSHIFT);
        Assert.Equal(KeyCaptureStatus.Finished, accumulator.OnKeyUp(VK_LSHIFT));

        Assert.Equal(KeyChord.FromKey(Key.LeftShift), accumulator.Result.Chord);
    }

    [Fact]
    public void Capture_CtrlThenShift_LastModifierIsPrimary()
    {
        var accumulator = Started();
        accumulator.OnKeyDown(VK_LCONTROL);
        accumulator.OnKeyDown(VK_LSHIFT);
        accumulator.OnKeyUp(VK_LCONTROL);
        Assert.Equal(KeyCaptureStatus.Finished, accumulator.OnKeyUp(VK_LSHIFT));

        Assert.Equal(KeyChord.FromKey(Key.LeftShift, ModifierKeys.Control), accumulator.Result.Chord);
        Assert.Equal("Ctrl+LeftShift", accumulator.Result.Chord.ToDisplayText());
    }

    [Fact]
    public void Capture_RightCtrlAlone_GivesRightCtrl()
    {
        var accumulator = Started();
        accumulator.OnKeyDown(VK_RCONTROL);
        accumulator.OnKeyUp(VK_RCONTROL);

        Assert.Equal(KeyChord.FromKey(Key.RightCtrl), accumulator.Result.Chord);
    }

    [Fact]
    public void Capture_LeftThenRightShift_KeepsShiftPrefix()
    {
        var accumulator = Started();
        accumulator.OnKeyDown(VK_LSHIFT);
        accumulator.OnKeyDown(VK_RSHIFT);
        accumulator.OnKeyUp(VK_LSHIFT);
        accumulator.OnKeyUp(VK_RSHIFT);

        Assert.Equal(KeyChord.FromKey(Key.RightShift, ModifierKeys.Shift), accumulator.Result.Chord);
    }

    [Fact]
    public void Capture_TwoPrimaryKeys_IsRejected()
    {
        var accumulator = Started();
        accumulator.OnKeyDown(VK_E);
        accumulator.OnKeyDown(VK_F);
        accumulator.OnKeyUp(VK_E);
        Assert.Equal(KeyCaptureStatus.Finished, accumulator.OnKeyUp(VK_F));

        Assert.Equal(KeyChordResult.Rejected(KeyChordRejection.MultipleKeys), accumulator.Result);
        Assert.False(accumulator.Result.IsCompleted);
    }

    [Fact]
    public void Capture_KeyAndMouseButton_IsRejected()
    {
        var accumulator = Started();
        accumulator.OnKeyDown(VK_E);
        accumulator.OnMouseDown(MouseButton.Middle);
        accumulator.OnMouseUp(MouseButton.Middle);
        Assert.Equal(KeyCaptureStatus.Finished, accumulator.OnKeyUp(VK_E));

        Assert.Equal(KeyChordRejection.MultipleKeys, accumulator.Result.Rejection);
    }

    [Fact]
    public void Capture_AutoRepeatDowns_AreIgnored()
    {
        var accumulator = Started();
        accumulator.OnKeyDown(VK_E);
        accumulator.OnKeyDown(VK_E);
        accumulator.OnKeyDown(VK_E);
        Assert.Equal(KeyCaptureStatus.Finished, accumulator.OnKeyUp(VK_E));

        Assert.Equal(KeyChord.FromKey(Key.E), accumulator.Result.Chord);
    }

    [Fact]
    public void Capture_PreheldKeyUp_IsIgnoredAndDoesNotFinish()
    {
        var accumulator = new KeyChordAccumulator();
        accumulator.Begin([VK_LCONTROL]);

        Assert.Equal(KeyCaptureStatus.Pending, accumulator.OnKeyDown(VK_LCONTROL));
        Assert.Equal(KeyCaptureStatus.Pending, accumulator.OnKeyUp(VK_LCONTROL));
        Assert.False(accumulator.HasStarted);

        accumulator.OnKeyDown(VK_E);
        Assert.Equal(KeyCaptureStatus.Finished, accumulator.OnKeyUp(VK_E));
        Assert.Equal(KeyChord.FromKey(Key.E), accumulator.Result.Chord);
    }

    [Fact]
    public void Capture_CtrlWithMouseButton4_GivesCtrlMouse4()
    {
        var accumulator = Started();
        accumulator.OnKeyDown(VK_LCONTROL);
        accumulator.OnMouseDown(MouseButton.XButton1);
        accumulator.OnMouseUp(MouseButton.XButton1);
        Assert.Equal(KeyCaptureStatus.Finished, accumulator.OnKeyUp(VK_LCONTROL));

        Assert.Equal(KeyChord.FromTrigger(InputTrigger.FromMouseButton(MouseButton.XButton1), ModifierKeys.Control),
            accumulator.Result.Chord);
        Assert.Equal("Ctrl+Mouse Button 4", accumulator.Result.Chord.ToDisplayText());
    }

    [Fact]
    public void Capture_WheelNotchAlone_FinishesImmediately()
    {
        var accumulator = Started();
        Assert.Equal(KeyCaptureStatus.Finished, accumulator.OnWheel(MouseWheelDirection.Up));

        Assert.Equal(KeyChord.FromTrigger(InputTrigger.FromWheel(MouseWheelDirection.Up)), accumulator.Result.Chord);
    }

    [Fact]
    public void Capture_WheelWhileCtrlHeld_FinishesOnCtrlRelease()
    {
        var accumulator = Started();
        accumulator.OnKeyDown(VK_LCONTROL);
        Assert.Equal(KeyCaptureStatus.Pending, accumulator.OnWheel(MouseWheelDirection.Down));
        Assert.Equal(KeyCaptureStatus.Finished, accumulator.OnKeyUp(VK_LCONTROL));

        Assert.Equal("Ctrl+Wheel Down", accumulator.Result.Chord.ToDisplayText());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Capture_ReleaseOrder_DoesNotChangeResult(bool primaryFirst)
    {
        var accumulator = Started();
        accumulator.OnKeyDown(VK_LCONTROL);
        accumulator.OnKeyDown(VK_LSHIFT);
        accumulator.OnKeyDown(VK_F5);
        int[] releases = primaryFirst ? [VK_F5, VK_LSHIFT, VK_LCONTROL] : [VK_LCONTROL, VK_LSHIFT, VK_F5];
        var status = KeyCaptureStatus.Pending;
        foreach (var vk in releases) status = accumulator.OnKeyUp(vk);

        Assert.Equal(KeyCaptureStatus.Finished, status);
        Assert.Equal(KeyChord.FromKey(Key.F5, ModifierKeys.Control | ModifierKeys.Shift), accumulator.Result.Chord);
    }

    [Fact]
    public void Capture_ImeProcessedKey_IsUnsupported()
    {
        var accumulator = Started();
        accumulator.OnKeyDown(VK_PROCESSKEY);
        Assert.Equal(KeyCaptureStatus.Finished, accumulator.OnKeyUp(VK_PROCESSKEY));

        Assert.Equal(KeyChordResult.Rejected(KeyChordRejection.UnsupportedKey), accumulator.Result);
    }

    [Fact]
    public void Begin_AfterFinishedCapture_StartsFresh()
    {
        var accumulator = Started();
        accumulator.OnKeyDown(VK_E);
        accumulator.OnKeyUp(VK_E);

        accumulator.Begin(ReadOnlySpan<int>.Empty);
        Assert.False(accumulator.HasStarted);
        accumulator.OnKeyDown(VK_F5);
        Assert.Equal(KeyCaptureStatus.Finished, accumulator.OnKeyUp(VK_F5));
        Assert.Equal(KeyChord.FromKey(Key.F5), accumulator.Result.Chord);
    }

    [Fact]
    public void ToDisplayText_AnyFlagOrder_PutsModifiersFirstInCanonicalOrder()
    {
        var chord = KeyChord.FromKey(Key.F5, ModifierKeys.Windows | ModifierKeys.Shift | ModifierKeys.Alt | ModifierKeys.Control);

        Assert.Equal("Ctrl+Alt+Shift+Win+F5", chord.ToDisplayText());
        Assert.Equal("Shift+Win+F5", KeyChord.FromKey(Key.F5, ModifierKeys.Windows | ModifierKeys.Shift).ToDisplayText());
    }

    [Fact]
    public void ToDisplayText_None_UsesNoneText()
    {
        Assert.True(KeyChord.None.IsEmpty);
        Assert.Equal("None", KeyChord.None.ToDisplayText());
        Assert.Equal("No shortcut", KeyChord.None.ToDisplayText("No shortcut"));
    }

    private static KeyChordAccumulator Started()
    {
        var accumulator = new KeyChordAccumulator();
        accumulator.Begin(ReadOnlySpan<int>.Empty);
        return accumulator;
    }
}
