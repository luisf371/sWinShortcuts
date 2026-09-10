using Xunit;
using Key = System.Windows.Input.Key;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class InputTriggerTests
{
    [Theory]
    [InlineData("Wheel:Up", "Wheel:Up")]
    [InlineData(" wheel : down ", "Wheel:Down")]
    public void SerializeDeserialize_WheelNames_RoundTrips(string input, string expected)
    {
        Assert.Equal(expected, InputTriggerSerializer.Serialize(InputTriggerSerializer.Deserialize(input)));
    }

    [Theory]
    [InlineData("Wheel:0")]
    [InlineData("Wheel:1")]
    [InlineData("Wheel:")]
    [InlineData("Wheel:Sideways")]
    [InlineData("Wheel:Up:Down")]
    public void Deserialize_InvalidWheel_ReturnsNone(string value)
    {
        Assert.Equal(InputTrigger.None, InputTriggerSerializer.Deserialize(value));
    }

    [Fact]
    public void FromWheel_ValidDirectionIsCanonical_UndefinedDirectionIsRejected()
    {
        var trigger = InputTrigger.FromWheel(MouseWheelDirection.Up);
        Assert.Equal(InputTriggerKind.MouseWheel, trigger.Kind);
        Assert.Equal(Key.None, trigger.Key);
        Assert.Equal("Wheel:Up", InputTriggerSerializer.Serialize(trigger));
        Assert.Equal(InputTrigger.None, InputTrigger.FromWheel((MouseWheelDirection)2));
        Assert.Equal("None", InputTriggerSerializer.Serialize(
            new InputTrigger(InputTriggerKind.MouseWheel, Key.None, default, (MouseWheelDirection)2)));
    }

    [Fact]
    public void RightClickHoldBreath_EarlyCancelDefaultsEnabled()
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
