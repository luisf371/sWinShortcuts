using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using sWinShortcuts.ViewModels;
using Xunit;

namespace Tests;

public sealed class MacroDuplicateNamingTests
{
    [Fact]
    public void DuplicateMacro_OriginalAndCopy_UseNextAvailableNumber()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition { Label = "Ammo" }];
        using var editor = new MacrosViewModel(profile, () => { });
        var original = editor.SelectedMacro!;

        editor.DuplicateMacroCommand.Execute(null);
        Assert.Equal("Ammo 1", editor.SelectedMacro!.Label);
        editor.DuplicateMacroCommand.Execute(null);
        Assert.Equal("Ammo 2", editor.SelectedMacro!.Label);
        editor.SelectedMacro = original;
        editor.DuplicateMacroCommand.Execute(null);

        Assert.Equal("Ammo 3", editor.SelectedMacro!.Label);
        Assert.Equal(new[] { "Ammo", "Ammo 1", "Ammo 2", "Ammo 3" },
            profile.Macros.Definitions.Select(macro => macro.Label));
        Assert.Equal("Ammo", original.Label);
    }

    [Theory]
    [InlineData("  Ammo 9  ", "Ammo 10")]
    [InlineData("Ammo 009", "Ammo 10")]
    [InlineData("Ammo 2147483647", "Ammo 2147483648")]
    [InlineData("Ammo 9223372036854775807", "Ammo 9223372036854775808")]
    [InlineData("Ammo v2", "Ammo v2 1")]
    [InlineData("Ammo -1", "Ammo -1 1")]
    public void DuplicateMacro_TrailingNumber_IncrementsWithoutOverflow(string label, string expected)
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition { Label = label }];
        using var editor = new MacrosViewModel(profile, () => { });
        var original = editor.SelectedMacro!;

        editor.DuplicateMacroCommand.Execute(null);

        Assert.Equal(expected, editor.SelectedMacro!.Label);
        Assert.Null(MacroValidation.GetFormatError(profile.Macros));
        Assert.Equal(label, original.Label);
    }

    [Fact]
    public void DuplicateMacro_SavedAndUnsavedNames_ReservesBothIgnoringCaseAndWhitespace()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions =
        [
            new MacroDefinition { Label = "Ammo" },
            new MacroDefinition { Label = "ammo 1", Steps = [new MacroStep { Kind = MacroStepKind.Wait }] }
        ];
        using var editor = new MacrosViewModel(profile, () => { });
        editor.Definitions[1].Steps[0].DurationText = "invalid";
        editor.Definitions[1].Label = "  AMMO 2  ";

        editor.DuplicateMacroCommand.Execute(null);

        Assert.Equal("Ammo 3", editor.SelectedMacro!.Label);
        Assert.Equal("ammo 1", profile.Macros.Definitions[1].Label);
        Assert.Equal("  AMMO 2  ", editor.Definitions[1].Label);
        Assert.Null(MacroValidation.GetFormatError(profile.Macros));
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(97, true)]
    public void DuplicateMacro_MaxLengthLabel_TruncatesOnlyCopyWithoutSplittingSurrogate(int length, bool addSurrogate)
    {
        var label = new string('x', length) + (addSurrogate ? "\U0001F600x" : string.Empty);
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition { Label = label }];
        using var editor = new MacrosViewModel(profile, () => { });
        var original = editor.SelectedMacro!;

        editor.DuplicateMacroCommand.Execute(null);

        Assert.Equal(new string('x', addSurrogate ? 97 : 98) + " 1", editor.SelectedMacro!.Label);
        Assert.Equal(label, original.Label);
        Assert.Null(MacroValidation.GetFormatError(profile.Macros));
    }

    [Fact]
    public void DuplicateMacro_SuffixUsesEntireLabel_RemainsValid()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition { Label = "A " + new string('9', 98) }];
        using var editor = new MacrosViewModel(profile, () => { });

        editor.DuplicateMacroCommand.Execute(null);

        Assert.Equal("1" + new string('0', 98), editor.SelectedMacro!.Label);
        Assert.Null(MacroValidation.GetFormatError(profile.Macros));
        Assert.Equal("A " + new string('9', 98), profile.Macros.Definitions[0].Label);
    }
}
