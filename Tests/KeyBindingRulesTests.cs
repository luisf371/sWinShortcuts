using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using sWinShortcuts.Converters;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class KeyBindingRulesTests
{
    private const ModifierKeys AllModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows;

    [Theory]
    [InlineData(Key.F13)]
    [InlineData(Key.F24)]
    [InlineData(Key.Pause)]
    [InlineData(Key.LWin)]
    [InlineData(Key.A)]
    public void AltShortcutSource_RegisteredKey_IsAccepted(Key key)
    {
        Assert.Contains(key, KeyCatalog.AltShortcutKeys);
        Assert.Null(KeyBindingRules.AltShortcutSource(KeyChord.FromKey(key), []));
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.XButton2)]
    public void AltShortcutSource_MouseButton_IsAccepted(MouseButton button) =>
        Assert.Null(KeyBindingRules.AltShortcutSource(KeyChord.FromTrigger(InputTrigger.FromMouseButton(button)), []));

    [Fact]
    public void AltShortcutSource_Wheel_IsAccepted() =>
        Assert.Null(KeyBindingRules.AltShortcutSource(KeyChord.FromTrigger(InputTrigger.FromWheel(MouseWheelDirection.Down)), []));

    [Theory]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.RightAlt)]
    public void AltShortcutSource_AltKey_IsRejectedAsTheModifier(Key key) =>
        Assert.Equal(KeyBindingRules.AltIsModifierMessage, KeyBindingRules.AltShortcutSource(KeyChord.FromKey(key), []));

    [Fact]
    public void AltShortcutSource_UsedByAnotherRow_IsRejected()
    {
        InputTrigger[] others = [InputTrigger.FromMouseButton(MouseButton.Middle), InputTrigger.FromKey(Key.F5)];

        Assert.Equal("Another row already uses F5", KeyBindingRules.AltShortcutSource(KeyChord.FromKey(Key.F5), others));
        Assert.Null(KeyBindingRules.AltShortcutSource(KeyChord.FromKey(Key.F6), others));
    }

    [Fact]
    public void AltShortcutSource_None_IsRejected() =>
        Assert.NotNull(KeyBindingRules.AltShortcutSource(KeyChord.None, []));

    [Fact]
    public void CombinedSource_KeysAndWheel_AreAccepted()
    {
        Assert.Null(KeyBindingRules.CombinedSource(KeyChord.FromKey(Key.F13), []));
        Assert.Null(KeyBindingRules.CombinedSource(KeyChord.FromTrigger(InputTrigger.FromWheel(MouseWheelDirection.Up)), []));
    }

    [Fact]
    public void CombinedSource_MouseButtonOrDuplicate_IsRejected()
    {
        Assert.NotNull(KeyBindingRules.CombinedSource(KeyChord.FromTrigger(InputTrigger.FromMouseButton(MouseButton.Middle)), []));
        Assert.Equal("Another row already uses Wheel Up", KeyBindingRules.CombinedSource(
            KeyChord.FromTrigger(InputTrigger.FromWheel(MouseWheelDirection.Up)), [InputTrigger.FromWheel(MouseWheelDirection.Up)]));
    }

    [Theory]
    [InlineData(MouseButton.Middle, true)]
    [InlineData(MouseButton.XButton1, true)]
    [InlineData(MouseButton.XButton2, true)]
    [InlineData(MouseButton.Left, false)]
    [InlineData(MouseButton.Right, false)]
    public void HoldBreathPanic_MouseButtons_OnlyMiddleAndSideButtons(MouseButton button, bool accepted)
    {
        var message = KeyBindingRules.HoldBreathPanic(KeyChord.FromTrigger(InputTrigger.FromMouseButton(button)));

        Assert.Equal(accepted, message is null);
    }

    [Fact]
    public void HoldBreathPanic_NoneAndKeys_AreAccepted()
    {
        Assert.Null(KeyBindingRules.HoldBreathPanic(KeyChord.None));
        Assert.Null(KeyBindingRules.HoldBreathPanic(KeyChord.FromKey(Key.F)));
        Assert.NotNull(KeyBindingRules.HoldBreathPanic(KeyChord.FromTrigger(InputTrigger.FromWheel(MouseWheelDirection.Up))));
    }

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.LWin)]
    public void AppToggle_ModifierKey_IsRejected(Key key) =>
        Assert.Equal(KeyBindingRules.AppToggleModifierMessage, KeyBindingRules.AppToggle(KeyChord.FromKey(key)));

    [Fact]
    public void AppToggle_PlainKeyOrNone_IsAccepted()
    {
        Assert.Null(KeyBindingRules.AppToggle(KeyChord.FromKey(Key.F8)));
        Assert.Null(KeyBindingRules.AppToggle(KeyChord.None));
        Assert.NotNull(KeyBindingRules.AppToggle(KeyChord.FromTrigger(InputTrigger.FromMouseButton(MouseButton.Middle))));
    }

    [Fact]
    public void MacroShortcut_AllModifiersWithKeyOrMouse_IsAccepted()
    {
        Assert.Null(KeyBindingRules.MacroShortcut(KeyChord.FromKey(Key.F5, AllModifiers)));
        Assert.Null(KeyBindingRules.MacroShortcut(KeyChord.FromTrigger(InputTrigger.FromMouseButton(MouseButton.Left))));
        Assert.Null(KeyBindingRules.MacroShortcut(KeyChord.None));
    }

    [Fact]
    public void MacroShortcut_Wheel_IsRejected() =>
        Assert.NotNull(KeyBindingRules.MacroShortcut(KeyChord.FromTrigger(InputTrigger.FromWheel(MouseWheelDirection.Up))));

    [Fact]
    public void AutoRunTrigger_NoneOrMouse_IsRejected()
    {
        Assert.NotNull(KeyBindingRules.AutoRunTrigger(KeyChord.None));
        Assert.NotNull(KeyBindingRules.AutoRunTrigger(KeyChord.FromTrigger(InputTrigger.FromMouseButton(MouseButton.Middle))));
        Assert.Null(KeyBindingRules.AutoRunTrigger(KeyChord.FromKey(Key.R, ModifierKeys.Control | ModifierKeys.Alt)));
        Assert.Null(KeyBindingRules.AutoRunTrigger(KeyChord.FromKey(Key.R, AllModifiers)));
    }

    [Fact]
    public void AssignableKey_KeyOrNone_IsAccepted()
    {
        Assert.Null(KeyBindingRules.AssignableKey(KeyChord.FromKey(Key.F13)));
        Assert.Null(KeyBindingRules.AssignableKey(KeyChord.None));
        Assert.NotNull(KeyBindingRules.AssignableKey(KeyChord.FromTrigger(InputTrigger.FromMouseButton(MouseButton.Middle))));
    }

    [Fact]
    public void MacroKey_SupportedKey_IsAccepted()
    {
        Assert.Null(KeyBindingRules.MacroKey(KeyChord.FromKey(Key.PrintScreen)));
        Assert.NotNull(KeyBindingRules.MacroKey(KeyChord.None));
    }

    [Fact]
    public void KeyChordConverter_Key_RoundTrips()
    {
        var converter = new KeyChordConverter();
        var chord = Assert.IsType<KeyChord>(converter.Convert(Key.F5, typeof(KeyChord), null, CultureInfo.InvariantCulture));

        Assert.Equal(KeyChord.FromKey(Key.F5), chord);
        Assert.Equal((object)Key.F5, converter.ConvertBack(chord, typeof(Key), null, CultureInfo.InvariantCulture));
        Assert.Equal((object)Key.None, converter.ConvertBack(KeyChord.None, typeof(Key), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void KeyChordConverter_NullableKey_MapsNullToNone()
    {
        var converter = new KeyChordConverter();

        Assert.Equal((object)KeyChord.None, converter.Convert(null, typeof(KeyChord), null, CultureInfo.InvariantCulture));
        Assert.Null(converter.ConvertBack(KeyChord.None, typeof(Key?), null, CultureInfo.InvariantCulture));
        Assert.Equal((object)Key.F8, converter.ConvertBack(KeyChord.FromKey(Key.F8), typeof(Key?), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void KeyChordConverter_InputTrigger_RoundTrips()
    {
        var converter = new KeyChordConverter();
        var trigger = InputTrigger.FromMouseButton(MouseButton.XButton1);
        var chord = Assert.IsType<KeyChord>(converter.Convert(trigger, typeof(KeyChord), null, CultureInfo.InvariantCulture));

        Assert.Equal((object)trigger, converter.ConvertBack(chord, typeof(InputTrigger), null, CultureInfo.InvariantCulture));
        Assert.Equal((object)InputTrigger.None, converter.ConvertBack(KeyChord.None, typeof(InputTrigger), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void KeyChordConverter_UnknownTargetType_DoesNothing() =>
        Assert.Same(Binding.DoNothing,
            new KeyChordConverter().ConvertBack(KeyChord.FromKey(Key.A), typeof(string), null, CultureInfo.InvariantCulture));
}
