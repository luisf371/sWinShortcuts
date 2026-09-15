using System.Windows.Input;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using Xunit;
using AppMouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class MacroModelTests
{
    [Theory]
    [InlineData(Key.LeftCtrl, ModifierKeys.Control)]
    [InlineData(Key.RightAlt, ModifierKeys.Alt)]
    [InlineData(Key.LeftShift, ModifierKeys.Shift)]
    [InlineData(Key.RWin, ModifierKeys.Windows)]
    public void Playback_ShortcutRepeatsItsBaseModifier_RejectsUnreachableChord(Key key, ModifierKeys modifier)
    {
        var macro = new MacroDefinition
        {
            ShortcutKey = key, ShortcutModifiers = modifier,
            Steps = [new MacroStep { Kind = MacroStepKind.KeyPress, Key = Key.A }]
        };
        Assert.Null(MacroValidation.GetFormatError(macro));
        Assert.Contains("modifiers", MacroValidation.GetPlaybackError(macro));
        Assert.Null(MacroValidation.GetPlaybackError(macro with { ShortcutModifiers = ModifierKeys.None }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Duplicate_EnabledAssignedMacro_CreatesDetachedDisabledDraft(bool mouseShortcut)
    {
        var original = new MacroDefinition
        {
            Label = "Copy",
            IsEnabled = true,
            ShortcutKey = mouseShortcut ? Key.None : Key.F6,
            ShortcutMouseButton = mouseShortcut ? AppMouseButton.Middle : null,
            ShortcutModifiers = ModifierKeys.Control,
            Steps = [new() { Kind = MacroStepKind.KeyPress, Key = Key.C }]
        };

        var copy = original.Duplicate();

        Assert.NotEqual(Guid.Empty, copy.Id);
        Assert.NotEqual(original.Id, copy.Id);
        Assert.Equal("Copy", copy.Label);
        Assert.False(copy.IsEnabled);
        Assert.Equal(Key.None, copy.ShortcutKey);
        Assert.Null(copy.ShortcutMouseButton);
        Assert.Equal(InputTrigger.None, copy.ShortcutTrigger);
        Assert.Equal(ModifierKeys.None, copy.ShortcutModifiers);
        Assert.NotSame(original.Steps, copy.Steps);
        Assert.Equal(original.Steps, copy.Steps);
    }

    [Theory]
    [InlineData(AppMouseButton.Left)]
    [InlineData(AppMouseButton.Right)]
    [InlineData(AppMouseButton.Middle)]
    [InlineData(AppMouseButton.XButton1)]
    [InlineData(AppMouseButton.XButton2)]
    public void GetPlaybackError_MouseShortcutWithModifiers_AcceptsAllFiveButtons(AppMouseButton button)
    {
        var macro = new MacroDefinition
        {
            ShortcutMouseButton = button,
            ShortcutModifiers = ModifierKeys.Control | ModifierKeys.Alt,
            Steps = [new() { Kind = MacroStepKind.Wait }]
        };

        Assert.Equal(InputTrigger.FromMouseButton(button), macro.ShortcutTrigger);
        Assert.Null(MacroValidation.GetFormatError(macro));
        Assert.Null(MacroValidation.GetPlaybackError(macro));
        Assert.NotNull(MacroValidation.GetFormatError(macro with { ShortcutKey = Key.F6 }));
        Assert.NotNull(MacroValidation.GetFormatError(macro with { ShortcutMouseButton = (AppMouseButton)99 }));
    }

    [Fact]
    public void GetPlaybackError_OverlappingControlCAndRepeatedKeyDown_IsPlayable()
    {
        var macro = Assigned(
            new() { Kind = MacroStepKind.KeyDown, Key = Key.LeftCtrl },
            new() { Kind = MacroStepKind.KeyDown, Key = Key.C },
            new() { Kind = MacroStepKind.KeyDown, Key = Key.C },
            new() { Kind = MacroStepKind.KeyUp, Key = Key.LeftCtrl },
            new() { Kind = MacroStepKind.KeyUp, Key = Key.C });

        Assert.Null(MacroValidation.GetFormatError(macro));
        Assert.Null(MacroValidation.GetPlaybackError(macro));
    }

    [Theory]
    [InlineData(MacroStepKind.KeyUp, Key.C)]
    [InlineData(MacroStepKind.KeyDown, Key.C)]
    public void GetPlaybackError_UnbalancedDraft_IsSaveableButNotPlayable(MacroStepKind kind, Key key)
    {
        var macro = Assigned(new MacroStep { Kind = kind, Key = key });

        Assert.Null(MacroValidation.GetFormatError(macro));
        Assert.StartsWith("Step 1:", MacroValidation.GetPlaybackError(macro));
    }

    [Fact]
    public void GetPlaybackError_KeyReheldAfterBalancedPair_ReportsUnreleasedDownRow()
    {
        var macro = Assigned(
            new() { Kind = MacroStepKind.KeyDown, Key = Key.A },
            new() { Kind = MacroStepKind.KeyUp, Key = Key.A },
            new() { Kind = MacroStepKind.KeyDown, Key = Key.A });

        Assert.StartsWith("Step 3:", MacroValidation.GetPlaybackError(macro));
    }

    [Fact]
    public void GetPlaybackError_TapAlreadyHeldKey_IsNotPlayable()
    {
        var macro = Assigned(
            new() { Kind = MacroStepKind.KeyDown, Key = Key.C },
            new() { Kind = MacroStepKind.KeyPress, Key = Key.C },
            new() { Kind = MacroStepKind.KeyUp, Key = Key.C });

        Assert.Null(MacroValidation.GetFormatError(macro));
        Assert.NotNull(MacroValidation.GetPlaybackError(macro));
    }

    [Theory]
    [InlineData(MacroStepKind.MouseDown)]
    [InlineData(MacroStepKind.MouseClick)]
    public void GetPlaybackError_RepeatedHeldMouseInput_IsNotPlayable(MacroStepKind secondKind)
    {
        var macro = Assigned(
            new() { Kind = MacroStepKind.MouseDown, MouseButton = AppMouseButton.Left },
            new() { Kind = secondKind, MouseButton = AppMouseButton.Left },
            new() { Kind = MacroStepKind.MouseUp, MouseButton = AppMouseButton.Left });

        Assert.Null(MacroValidation.GetFormatError(macro));
        Assert.NotNull(MacroValidation.GetPlaybackError(macro));
    }

    [Fact]
    public void GetPlaybackError_UnassignedOrEmptyMacro_IsSaveableDraft()
    {
        var macro = new MacroDefinition();

        Assert.Equal("New macro", macro.Label);
        Assert.Null(MacroValidation.GetFormatError(macro));
        Assert.NotNull(MacroValidation.GetPlaybackError(macro));
        Assert.NotNull(MacroValidation.GetPlaybackError(macro with { ShortcutKey = Key.F6 }));
        Assert.NotNull(MacroValidation.GetPlaybackError(Assigned(new MacroStep { Kind = MacroStepKind.Wait }) with { ShortcutKey = Key.F12 }));
    }

    [Theory]
    [InlineData((Key)999)]
    [InlineData(Key.System)]
    [InlineData(Key.None)]
    public void GetFormatError_UnsupportedStepKey_RejectsKey(Key key)
    {
        Assert.NotNull(MacroValidation.GetFormatError(Assigned(new MacroStep { Kind = MacroStepKind.KeyPress, Key = key })));
    }

    [Fact]
    public void GetFormatError_InvalidDefinitionsAndFlags_RejectsMalformedValues()
    {
        var macro = Assigned(new MacroStep { Kind = MacroStepKind.Wait });
        Assert.Null(MacroValidation.GetFormatError(macro with { ShortcutModifiers = ModifierKeys.Control | ModifierKeys.Alt }));
        Assert.NotNull(MacroValidation.GetFormatError(macro with { ShortcutModifiers = (ModifierKeys)16 }));
        Assert.NotNull(MacroValidation.GetFormatError(macro with { Id = Guid.Empty }));
        Assert.NotNull(MacroValidation.GetFormatError(macro with { Label = " \t " }));
        Assert.NotNull(MacroValidation.GetFormatError(macro with { Label = "unsafe\nlabel" }));
        Assert.NotNull(MacroValidation.GetFormatError(macro with { Label = new string('x', 101) }));
        Assert.NotNull(MacroValidation.GetFormatError(new MacroSettings { Definitions = [macro, macro] }));
        Assert.Null(MacroValidation.GetFormatError(new MacroSettings { Definitions = [macro, macro with { Id = Guid.NewGuid() }] }));
        Assert.NotNull(MacroValidation.GetFormatError(Assigned(new MacroStep { Kind = (MacroStepKind)999 })));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(3600000, true)]
    [InlineData(-1, false)]
    [InlineData(3600001, false)]
    public void GetStepError_TapOrWaitDuration_EnforcesWholeMillisecondBounds(int duration, bool valid)
    {
        Assert.Equal(valid, MacroValidation.GetStepError(new() { Kind = MacroStepKind.KeyPress, Key = Key.A, DurationMs = duration }) is null);
        Assert.Equal(valid, MacroValidation.GetStepError(new() { Kind = MacroStepKind.Wait, DurationMs = duration }) is null);
        Assert.Equal(valid, MacroValidation.GetStepError(new() { Kind = MacroStepKind.MouseClick, MouseButton = AppMouseButton.Left, DurationMs = duration }) is null);
    }

    [Theory]
    [InlineData(-32768, true)]
    [InlineData(32767, true)]
    [InlineData(0, false)]
    [InlineData(-32769, false)]
    [InlineData(32768, false)]
    public void GetStepError_WheelDelta_PreservesSignedHighWord(int delta, bool valid)
    {
        Assert.Equal(valid, MacroValidation.GetStepError(new() { Kind = MacroStepKind.MouseWheel, WheelDelta = delta }) is null);
    }

    [Fact]
    public void GetTotalDurationMs_LongSequence_DoesNotOverflowInt32()
    {
        var macro = Assigned(Enumerable.Repeat(new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 3_600_000 }, 1000).ToArray());

        Assert.Null(MacroValidation.GetFormatError(macro));
        Assert.Equal(3_600_000_000L, MacroValidation.GetTotalDurationMs(macro));
        Assert.NotNull(MacroValidation.GetFormatError(macro with { Steps = [.. macro.Steps, new() { Kind = MacroStepKind.Wait }] }));
    }

    private static MacroDefinition Assigned(params MacroStep[] steps) => new() { ShortcutKey = Key.F6, Steps = steps };
}
