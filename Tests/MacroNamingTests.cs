using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.ViewModels;
using Xunit;

namespace Tests;

// The view supplies the modal naming dialog through ConfigureNaming; these requests stand in for it.
public sealed class MacroNamingTests
{
    [Fact]
    public void NewMacro_Cancelled_CreatesSelectsAndPublishesNothing()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition { Label = "Ammo" }];
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        var selected = editor.SelectedMacro;
        var published = profile.Macros.Definitions;
        var requests = new List<string?>();
        editor.ConfigureNaming(label =>
        {
            requests.Add(label);
            return null;
        });

        editor.NewMacroCommand.Execute(null);

        Assert.Equal(new string?[] { null }, requests);
        Assert.Single(editor.Definitions);
        Assert.Same(selected, editor.SelectedMacro);
        Assert.Same(published, profile.Macros.Definitions);
        Assert.Equal(0, edits);
    }

    [Fact]
    public void NewMacro_Accepted_PublishesTrimmedNameOnceWithoutUniquenessRule()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition { Label = "Ammo" }];
        var published = new List<string[]>();
        using var editor = new MacrosViewModel(profile, () => published.Add(profile.Macros.Definitions.Select(macro => macro.Label).ToArray()));
        var existing = editor.SelectedMacro;
        editor.ConfigureNaming(_ => "  Ammo  ");

        editor.NewMacroCommand.Execute(null);

        var created = Assert.IsType<MacroViewModel>(editor.SelectedMacro);
        Assert.NotSame(existing, created);
        Assert.Equal("Ammo", created.Label);
        Assert.Equal(new[] { "Ammo", "Ammo" }, Assert.Single(published));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Tab\tinside")]
    public void NewMacro_InvalidName_CreatesNothing(string name)
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        editor.ConfigureNaming(_ => name);

        editor.NewMacroCommand.Execute(null);

        Assert.Empty(editor.Definitions);
        Assert.Empty(profile.Macros.Definitions);
        Assert.Equal(0, edits);
    }

    [Fact]
    public void GetLabelError_MatchesSavedLabelRules()
    {
        Assert.Null(MacrosViewModel.GetLabelError(new string('x', 100)));
        Assert.Null(MacrosViewModel.GetLabelError($"  {new string('x', 100)}\t"));
        Assert.NotNull(MacrosViewModel.GetLabelError(new string('x', 101)));
        Assert.NotNull(MacrosViewModel.GetLabelError(" \t "));
        Assert.NotNull(MacrosViewModel.GetLabelError("Bell"));
    }

    [Fact]
    public void RenameMacro_CancelledOrInvalid_LeavesLabelAndPersistenceUntouched()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition { Label = "Ammo" }];
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        var macro = editor.SelectedMacro!;
        var published = profile.Macros.Definitions;
        var requests = new List<string?>();
        foreach (var answer in new[] { null, "   ", new string('x', 101) })
        {
            editor.ConfigureNaming(label =>
            {
                requests.Add(label);
                return answer;
            });
            editor.RenameMacroCommand.Execute(null);
        }

        Assert.Equal(new string?[] { "Ammo", "Ammo", "Ammo" }, requests);
        Assert.Equal("Ammo", macro.Label);
        Assert.Same(published, profile.Macros.Definitions);
        Assert.Equal(0, edits);
    }

    [Fact]
    public void RenameMacro_Accepted_KeepsInvalidStepDraftAndSavesNameOnceCorrected()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition { Label = "Ammo", Steps = [new MacroStep { Kind = MacroStepKind.Wait }] }];
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        var macro = editor.SelectedMacro!;
        macro.Steps[0].DurationText = "invalid";
        edits = 0;
        editor.ConfigureNaming(_ => " Reload ");

        editor.RenameMacroCommand.Execute(null);

        Assert.Equal("Reload", macro.Label);
        Assert.Equal("invalid", macro.Steps[0].DurationText);
        Assert.True(macro.HasFormatError);
        Assert.Equal(1, edits);
        macro.Steps[0].DurationText = "5";
        Assert.Equal("Reload", profile.Macros.Definitions[0].Label);
    }

    [Fact]
    public void RenameMacro_TargetChangesWhileOpen_RenamesOnlyTheIntendedAttachedMacro()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition { Label = "Ammo" }, new MacroDefinition { Label = "Heal" }];
        using var editor = new MacrosViewModel(profile, () => { });
        var ammo = editor.Definitions[0];
        var heal = editor.Definitions[1];

        editor.ConfigureNaming(_ =>
        {
            editor.SelectedMacro = heal;
            return "Reload";
        });
        editor.RenameMacroCommand.Execute(null);

        Assert.Equal("Reload", ammo.Label);
        Assert.Equal("Heal", heal.Label);

        editor.ConfigureNaming(_ =>
        {
            editor.DeleteMacroCommand.Execute(null);
            return "Gone";
        });
        editor.RenameMacroCommand.Execute(null);

        Assert.DoesNotContain(heal, editor.Definitions);
        Assert.Equal("Heal", heal.Label);
        Assert.Equal(new[] { "Reload" }, profile.Macros.Definitions.Select(macro => macro.Label));
    }

    [Fact]
    public void NamingDialog_EditingUnavailableAfterItCloses_CreatesAndRenamesNothing()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new MacroDefinition { Label = "Ammo" }];
        var edits = 0;
        using var editor = new MacrosViewModel(profile, () => edits++);
        var macro = editor.SelectedMacro!;
        foreach (var interruption in new Action[]
        {
            () => profile.IsEnabled = false,
            () => profile.IsPersistenceSuspended = true,
            () => editor.SetRecordingDestination(macro)
        })
        {
            foreach (var command in new[] { editor.NewMacroCommand, editor.RenameMacroCommand })
            {
                editor.ConfigureNaming(_ =>
                {
                    interruption();
                    return "Late";
                });
                command.Execute(null);
                editor.SetRecordingDestination(null);
                profile.IsEnabled = true;
                profile.IsPersistenceSuspended = false;
            }
        }

        editor.ConfigureNaming(_ =>
        {
            editor.Dispose();
            return "Late";
        });
        editor.NewMacroCommand.Execute(null);

        Assert.Same(macro, Assert.Single(editor.Definitions));
        Assert.Equal("Ammo", macro.Label);
        Assert.Equal(new[] { "Ammo" }, profile.Macros.Definitions.Select(definition => definition.Label));
        Assert.Equal(0, edits);
    }
}
