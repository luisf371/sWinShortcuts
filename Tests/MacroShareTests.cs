using System.Drawing;
using System.Windows.Input;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using sWinShortcuts.ViewModels;
using Xunit;

namespace Tests;

// Export and Import under More, with lambdas for the clipboard and the notices.
public sealed class MacroShareTests
{
    private static readonly Rectangle[] Desktop = [new(0, 0, 1920, 1080)];
    private readonly List<MacroNotice> _notices = [];
    private string? _clipboard;

    private MacrosViewModel Create(params MacroDefinition[] macros)
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = macros;
        var editor = new MacrosViewModel(profile, () => { }) { Monitors = () => Desktop };
        editor.ConfigureShare(text => { _clipboard = text; return true; }, () => _clipboard, _notices.Add);
        return editor;
    }

    private static string CodeFor(string label) => MacroShareCode.Export(new MacroDefinition
    {
        Label = label,
        Steps = [new() { Kind = MacroStepKind.KeyPress, Key = Key.E }]
    }, Desktop);

    [Fact]
    public void ExportMacro_CopiesTheCode_AndNotifies()
    {
        using var editor = Create(new MacroDefinition { Label = "Ammo", ShortcutKey = Key.F6, IsEnabled = true });

        editor.ExportMacroCommand.Execute(null);

        Assert.StartsWith(MacroShareCode.Prefix, _clipboard);
        Assert.EndsWith(":74656B6B79:", _clipboard);
        Assert.Equal("Ammo", MacroShareCode.Import(_clipboard!, Desktop).Macro!.Label);
        Assert.Equal("Macro copied", Assert.Single(_notices).Title);
    }

    [Fact]
    public void ExportMacro_FormatError_IsDisabled()
    {
        using var editor = Create(new MacroDefinition { Label = "Ammo", Steps = [new() { Kind = MacroStepKind.Wait, DurationMs = 100 }] });
        Assert.True(editor.ExportMacroCommand.CanExecute(null));

        editor.SelectedMacro!.Steps[0].DurationText = "soon";

        Assert.False(editor.ExportMacroCommand.CanExecute(null));
    }

    [Fact]
    public void ImportMacro_NameFree_KeepsTheName()
    {
        using var editor = Create(new MacroDefinition { Label = "Other" });
        _clipboard = CodeFor("Ammo");

        editor.ImportMacroCommand.Execute(null);

        Assert.Equal(["Other", "Ammo"], editor.Definitions.Select(macro => macro.Label));
        Assert.Same(editor.Definitions[1], editor.SelectedMacro);
        Assert.False(editor.SelectedMacro!.IsEnabled);
        Assert.Empty(_notices);
    }

    [Theory]
    [InlineData("Ammo", new[] { "Ammo" }, "Ammo 1")]
    [InlineData("Ammo", new[] { "ammo", "Ammo 1" }, "Ammo 2")]
    [InlineData("Ammo 2", new[] { "Ammo 2" }, "Ammo 3")]
    public void ImportMacro_NameTaken_AddsTheNextNumber(string imported, string[] existing, string expected)
    {
        using var editor = Create(existing.Select(label => new MacroDefinition { Label = label }).ToArray());
        _clipboard = CodeFor(imported);

        editor.ImportMacroCommand.Execute(null);

        Assert.Equal(expected, editor.Definitions[^1].Label);
    }

    [Fact]
    public void ImportMacro_LongNameTaken_StaysWithinTheLabelLimit()
    {
        var name = new string('a', 100);
        using var editor = Create(new MacroDefinition { Label = name });
        _clipboard = CodeFor(name);

        editor.ImportMacroCommand.Execute(null);

        Assert.Equal(new string('a', 98) + " 1", editor.Definitions[^1].Label);
    }

    [Fact]
    public void ImportMacro_ProfileFull_IsDisabled()
    {
        using var editor = Create(Enumerable.Range(0, MacroValidation.MaxDefinitions)
            .Select(index => new MacroDefinition { Label = $"M{index}" }).ToArray());

        Assert.False(editor.ImportMacroCommand.CanExecute(null));
    }

    [Fact]
    public void ImportMacro_EmptyProfile_IsAvailable()
    {
        using var editor = Create();
        _clipboard = CodeFor("Ammo");

        Assert.True(editor.ImportMacroCommand.CanExecute(null));
        editor.ImportMacroCommand.Execute(null);

        Assert.Equal("Ammo", Assert.Single(editor.Definitions).Label);
    }

    [Theory]
    [InlineData(null, "Clipboard busy")]
    [InlineData("hello", "Can't import macro")]
    public void ImportMacro_NothingToImport_ShowsANoticeAndAddsNothing(string? clipboard, string title)
    {
        using var editor = Create(new MacroDefinition { Label = "Other" });
        _clipboard = clipboard;

        editor.ImportMacroCommand.Execute(null);

        Assert.Single(editor.Definitions);
        Assert.Equal(title, Assert.Single(_notices).Title);
    }

    [Fact]
    public void ImportMacro_MultipleCodes_ShowsAnErrorAndAddsNothing()
    {
        using var editor = Create();
        _clipboard = $"{CodeFor("First")}\n{CodeFor("Second")}";

        editor.ImportMacroCommand.Execute(null);

        Assert.Empty(editor.Definitions);
        Assert.Equal("Can't import macro", Assert.Single(_notices).Title);
    }

    [Fact]
    public void ImportMacro_DifferentLayout_AddsTheMacroAndWarns()
    {
        using var editor = Create();
        _clipboard = MacroShareCode.Export(new MacroDefinition
        {
            Label = "Click",
            Steps = [new() { Kind = MacroStepKind.MouseClick, MouseButton = sWinShortcuts.Models.MouseButton.Left, X = 3000, Y = 10 }]
        }, [new(0, 0, 1920, 1080), new(1920, 0, 2560, 1440)]);

        editor.ImportMacroCommand.Execute(null);

        Assert.Single(editor.Definitions);
        var notice = Assert.Single(_notices);
        Assert.Equal("Different screen layout", notice.Title);
        Assert.Contains("1 screen position is outside your screens", notice.Message);
    }

    [Fact]
    public void ImportMacro_SameLayoutWithOffScreenPosition_Warns()
    {
        using var editor = Create();
        _clipboard = MacroShareCode.Export(new MacroDefinition
        {
            Label = "Click",
            Steps = [new() { Kind = MacroStepKind.MouseClick, MouseButton = sWinShortcuts.Models.MouseButton.Left, X = 2000, Y = 10 }]
        }, Desktop);

        editor.ImportMacroCommand.Execute(null);

        Assert.Single(editor.Definitions);
        var notice = Assert.Single(_notices);
        Assert.Equal("Screen position outside your screens", notice.Title);
        Assert.Contains("1 screen position is outside your screens", notice.Message);
    }
}
