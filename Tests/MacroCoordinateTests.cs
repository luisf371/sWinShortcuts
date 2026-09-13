using System.Drawing;
using System.Windows.Input;
using sWinShortcuts.Interop;
using sWinShortcuts.Services;
using Tests.Fakes;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class MacroCoordinateTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-2, 0)]
    [InlineData(-1, 1)]
    [InlineData(-2, 1)]
    [InlineData(-1, 2)]
    [InlineData(-2, 2)]
    public void MoveMouseTo_NativeSendAndGuard_UsePhysicalDpiContextAndRestoreCaller(int callerAwareness, int outcome)
    {
        var original = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            var physicalContext = ReadDpiContext();
            NativeMethods.SetThreadDpiAwarenessContext(new IntPtr(callerAwareness));
            var callerContext = ReadDpiContext();
            var sender = new WindowsInputSender(new NullLoggerService(), inputs =>
            {
                Assert.Equal(physicalContext, ReadDpiContext());
                if (outcome == 1) throw new InvalidOperationException("Native send failed.");
                return (uint)inputs.Length;
            }, () => [new Rectangle(0, 0, 6000, 1440)]);
            bool CanSend()
            {
                Assert.Equal(physicalContext, ReadDpiContext());
                return outcome != 2;
            }
            try
            {
                if (outcome == 1) Assert.Throws<InvalidOperationException>(() => sender.MoveMouseTo(4737, 855, CanSend));
                else Assert.Equal(outcome == 0, sender.MoveMouseTo(4737, 855, CanSend));
            }
            finally { Assert.Equal(callerContext, ReadDpiContext()); }
        }
        finally { NativeMethods.SetThreadDpiAwarenessContext(original); }
    }

    private static IntPtr ReadDpiContext()
    {
        var context = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        NativeMethods.SetThreadDpiAwarenessContext(context);
        return context;
    }

    [Theory]
    [InlineData(-1920, -1080, 8, 15)]
    [InlineData(0, 0, 32776, 32783)]
    [InlineData(1919, 1079, 65527, 65520)]
    public void MoveMouseTo_NegativeVirtualOrigin_UsesAbsolutePhysicalPixelCenters(int x, int y, int expectedX, int expectedY)
    {
        var inputs = new List<NativeMethods.INPUT>();
        var sender = Create(inputs, () => [new Rectangle(-1920, -1080, 3840, 2160)]);
        Assert.True(sender.MoveMouseTo(x, y));
        var input = Assert.Single(inputs);
        Assert.Equal(expectedX, input.U.mi.dx);
        Assert.Equal(expectedY, input.U.mi.dy);
        Assert.Equal(NativeMethods.MouseEventFlags.MOUSEEVENTF_MOVE |
            NativeMethods.MouseEventFlags.MOUSEEVENTF_ABSOLUTE |
            NativeMethods.MouseEventFlags.MOUSEEVENTF_VIRTUALDESK, input.U.mi.dwFlags);
        Assert.Equal(NativeMethods.INPUT_IGNORE, input.U.mi.dwExtraInfo);
    }

    [Fact]
    public void MoveMouseTo_MonitorGapAndRemovedDisplay_RejectsWithoutClamping()
    {
        var inputs = new List<NativeMethods.INPUT>();
        Rectangle[] monitors = [new(-100, 0, 100, 100), new(100, 0, 100, 100)];
        var sender = Create(inputs, () => monitors);
        Assert.False(sender.MoveMouseTo(50, 50));
        Assert.True(sender.MoveMouseTo(150, 50));
        monitors = [new(-100, 0, 100, 100)];
        Assert.False(sender.MoveMouseTo(150, 50));
        Assert.False(sender.MoveMouseTo(-101, 50));
        Assert.Single(inputs);
    }

    [Theory]
    [InlineData(MouseButton.XButton1, 1u)]
    [InlineData(MouseButton.XButton2, 2u)]
    public void SendMouseButton_XButtons_PreservesIdentityAndTagsOnlyMacroUp(MouseButton button, uint data)
    {
        var inputs = new List<NativeMethods.INPUT>();
        var sender = Create(inputs, () => []);
        Assert.True(sender.SendMouseButton(button, true));
        Assert.True(sender.SendMouseButton(button, false, macroRelease: true));
        Assert.Equal(NativeMethods.MouseEventFlags.MOUSEEVENTF_XDOWN, inputs[0].U.mi.dwFlags);
        Assert.Equal(NativeMethods.MouseEventFlags.MOUSEEVENTF_XUP, inputs[1].U.mi.dwFlags);
        Assert.Equal(data, inputs[0].U.mi.mouseData);
        Assert.Equal(data, inputs[1].U.mi.mouseData);
        Assert.Equal(NativeMethods.INPUT_IGNORE, inputs[0].U.mi.dwExtraInfo);
        Assert.Equal(NativeMethods.INPUT_MACRO_RELEASE, inputs[1].U.mi.dwExtraInfo);
    }

    [Theory]
    [InlineData(-60, false)]
    [InlineData(-32768, true)]
    [InlineData(32767, true)]
    public void SendMouseWheel_SignedDelta_PreservesDirectionAndSubDetentAmount(int delta, bool horizontal)
    {
        var inputs = new List<NativeMethods.INPUT>();
        var sender = Create(inputs, () => []);
        Assert.True(sender.SendMouseWheel(delta, horizontal));
        var input = Assert.Single(inputs);
        Assert.Equal(delta, unchecked((int)input.U.mi.mouseData));
        Assert.Equal(horizontal ? NativeMethods.MouseEventFlags.MOUSEEVENTF_HWHEEL :
            NativeMethods.MouseEventFlags.MOUSEEVENTF_WHEEL, input.U.mi.dwFlags);
    }

    [Fact]
    public void NativeBoundary_InvalidPayload_SendsNothing()
    {
        var inputs = new List<NativeMethods.INPUT>();
        var sender = Create(inputs, () => []);
        Assert.False(sender.SendMouseButton((MouseButton)99, true));
        Assert.False(sender.SendMouseWheel(0, false));
        Assert.False(sender.SendMouseWheel(32768, false));
        Assert.False(sender.MoveMouseTo(0, 0));
        Assert.Empty(inputs);
    }

    [Fact]
    public void SendKey_MacroUp_PreservesExistingExtendedKeyTransportWithSeparateTag()
    {
        var inputs = new List<NativeMethods.INPUT>();
        var sender = Create(inputs, () => []);
        Assert.True(sender.SendKey(Key.RightCtrl, true));
        Assert.True(sender.SendKey(Key.RightCtrl, false, macroRelease: true));
        Assert.Equal((ushort)0xA3, inputs[1].U.ki.wVk);
        Assert.Equal(NativeMethods.KeyEventFlags.KEYEVENTF_EXTENDEDKEY |
            NativeMethods.KeyEventFlags.KEYEVENTF_KEYUP, inputs[1].U.ki.dwFlags);
        Assert.Equal(NativeMethods.INPUT_IGNORE, inputs[0].U.ki.dwExtraInfo);
        Assert.Equal(NativeMethods.INPUT_MACRO_RELEASE, inputs[1].U.ki.dwExtraInfo);
    }

    private static WindowsInputSender Create(List<NativeMethods.INPUT> inputs, Func<Rectangle[]> monitors) =>
        new(new NullLoggerService(), sent =>
        {
            inputs.AddRange(sent);
            return (uint)sent.Length;
        }, monitors);
}
