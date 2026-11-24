using System.Windows.Input;
using sWinShortcuts.Converters;
using Xunit;

namespace Tests;

public sealed class KeyDisplayConverterTests
{
    [Theory]
    [InlineData(Key.Enter, "Enter")]
    [InlineData(Key.Back, "Backspace")]
    [InlineData(Key.CapsLock, "Caps Lock")]
    [InlineData(Key.PageDown, "Page Down")]
    [InlineData(Key.PageUp, "Page Up")]
    [InlineData(Key.PrintScreen, "Print Screen")]
    [InlineData(Key.LeftShift, "LShift")]
    [InlineData(Key.RightCtrl, "RCtrl")]
    [InlineData(Key.RightAlt, "RAlt")]
    [InlineData(Key.LWin, "LWin")]
    [InlineData(Key.NumPad7, "Num 7")]
    [InlineData(Key.Multiply, "Num *")]
    [InlineData(Key.MediaPlayPause, "Media Play Pause")]
    [InlineData(Key.F13, "F13")]
    [InlineData(Key.D5, "5")]
    [InlineData(Key.OemComma, ",")]
    [InlineData(Key.A, "A")]
    [InlineData(Key.None, "None")]
    public void ToDisplayText_Key_UsesKeycapName(Key key, string expected) =>
        Assert.Equal(expected, KeyDisplayConverter.ToDisplayText(key));
}
