using Xunit;
using Key = System.Windows.Input.Key;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class InputTriggerTests
{
    [Fact]
    public void RightClickHoldBreath_DefaultsToSuppressingEarlyCancelInput()
    {
        Assert.True(new RightClickHoldBreathSettings().SuppressEarlyCancelInput);
    }
    [Fact]
    public void SerializeDeserialize_KeyboardKey_RoundTrips()
    {
        var trigger = InputTrigger.FromKey(Key.F13);
        var serialized = InputTriggerSerializer.Serialize(trigger);
        var deserialized = InputTriggerSerializer.Deserialize(serialized);

        Assert.Equal("Key:F13", serialized);
        Assert.Equal(trigger, deserialized);
    }

    [Theory]
    [InlineData(MouseButton.Middle)]
    [InlineData(MouseButton.XButton1)]
    [InlineData(MouseButton.XButton2)]
    public void SerializeDeserialize_MouseButton_RoundTrips(MouseButton button)
    {
        var trigger = InputTrigger.FromMouseButton(button);
        var serialized = InputTriggerSerializer.Serialize(trigger);
        var deserialized = InputTriggerSerializer.Deserialize(serialized);

        Assert.Equal($"Mouse:{button}", serialized);
        Assert.Equal(trigger, deserialized);
    }

    [Fact]
    public void Deserialize_InvalidValue_ReturnsNone()
    {
        Assert.Equal(InputTrigger.None, InputTriggerSerializer.Deserialize("Mouse:NotAButton"));
        Assert.Equal(InputTrigger.None, InputTriggerSerializer.Deserialize("Key:NotAKey"));
        Assert.Equal(InputTrigger.None, InputTriggerSerializer.Deserialize("None"));
    }
}
