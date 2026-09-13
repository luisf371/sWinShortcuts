using System.Globalization;
using System.IO;
using System.Windows.Input;
using sWinShortcuts.Configuration;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using Xunit;
using AppMouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class MacroPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sWinShortcutsTests", Guid.NewGuid().ToString("N"));
    private readonly IniProfileStore _store;

    public MacroPersistenceTests()
    {
        _store = new IniProfileStore(_root, new Fakes.NullLoggerService());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("[Macros]\n")]
    [InlineData("[Macros]\nVersion=2\nEnabled=True\nCount=0\n")]
    [InlineData("[Macros]\nVersion=1\nEnabled=True\nCount=-1\n")]
    [InlineData("[Macros]\nVersion=1\nEnabled=True\nCount=2147483648\n")]
    [InlineData("[Macros]\nVersion=1\nEnabled=\nCount=0\n")]
    [InlineData("[Macros]\nVersion=1\nEnabled=True\nCount=1\n")]
    [InlineData("[Macro0]\nLabel=Orphan\n")]
    [InlineData("[Macro0.Step0]\nKind=Wait\nDurationMs=1\n")]
    public async Task LoadMacros_MalformedSection_PreservesSourceAndOtherFeatures(string macroSections)
    {
        var path = Path.Combine(_root, "Profiles", "Broken.ini");
        var original = "[Profile]\nName=Broken\nExecutable=broken.exe\n[AltMouse]\nEnabled=True\nWheelUp=Q\n" + macroSections;
        File.WriteAllText(path, original);
        await _store.SaveProfileAsync(ProfileFactory.CreateCustomProfile("Healthy", "healthy.exe"), CancellationToken.None);

        var profiles = await _store.LoadProfilesAsync(CancellationToken.None);
        var profile = profiles.Single(p => p.Name == "Broken");

        Assert.True(profile.IsPersistenceSuspended);
        Assert.True(profile.AltMouse.IsEnabled);
        Assert.Equal(Key.Q, profile.AltMouse.WheelUpKey);
        Assert.Contains(profiles, p => p.Name == "Healthy" && !p.IsPersistenceSuspended);
        profile.AltMouse.WheelUpKey = Key.W;
        await Assert.ThrowsAsync<PersistenceSuspendedException>(() => _store.SaveProfileAsync(profile, CancellationToken.None));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public async Task SaveAndLoad_MixedStepsAndDraftUnderFrenchCulture_PreservesOrderedValues()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        try
        {
            var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
            profile.Macros.IsEnabled = true;
            profile.Macros.Definitions =
            [
                new()
                {
                    Label = "  Inventaire 日本語  ",
                    IsEnabled = true,
                    ShortcutKey = Key.F6,
                    ShortcutModifiers = ModifierKeys.Control | ModifierKeys.Alt,
                    Steps =
                    [
                        new() { Kind = MacroStepKind.KeyDown, Key = Key.LeftCtrl },
                        new() { Kind = MacroStepKind.KeyPress, Key = Key.C },
                        new() { Kind = MacroStepKind.Wait, DurationMs = 120 },
                        new() { Kind = MacroStepKind.KeyUp, Key = Key.LeftCtrl },
                        new() { Kind = MacroStepKind.MouseClick, MouseButton = AppMouseButton.XButton1, X = -123, Y = 456, DurationMs = 17 },
                        new() { Kind = MacroStepKind.MouseDown, MouseButton = AppMouseButton.Left },
                        new() { Kind = MacroStepKind.MoveTo, X = 2147483647, Y = -2147483648 },
                        new() { Kind = MacroStepKind.MouseUp, MouseButton = AppMouseButton.Left },
                        new() { Kind = MacroStepKind.MouseWheel, WheelDelta = -120, HorizontalWheel = true }
                    ]
                },
                new() { Label = "Draft", Steps = [new() { Kind = MacroStepKind.KeyUp, Key = Key.Q }] }
            ];

            await _store.SaveProfileAsync(profile, CancellationToken.None);
            var loaded = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Game");

            Assert.True(loaded.Macros.IsEnabled);
            Assert.False(loaded.IsPersistenceSuspended);
            Assert.Equal(2, loaded.Macros.Definitions.Length);
            Assert.Equal(profile.Macros.Definitions[0].Id, loaded.Macros.Definitions[0].Id);
            Assert.Equal("Inventaire 日本語", loaded.Macros.Definitions[0].Label);
            Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, loaded.Macros.Definitions[0].ShortcutModifiers);
            Assert.Equal(profile.Macros.Definitions[0].Steps, loaded.Macros.Definitions[0].Steps);
            Assert.Equal(Key.None, loaded.Macros.Definitions[1].ShortcutKey);
            Assert.Equal(profile.Macros.Definitions[1].Steps, loaded.Macros.Definitions[1].Steps);
            Assert.Null(MacroValidation.GetPlaybackError(loaded.Macros.Definitions[0]));
            Assert.NotNull(MacroValidation.GetPlaybackError(loaded.Macros.Definitions[1]));
            var ini = IniDocument.Load(profile.SourcePath);
            Assert.Equal("3", ini.GetValue("Macro0", "ShortcutModifiers"));
            Assert.Null(ini.GetValue("Macro0.Step0", "DurationMs"));
            Assert.Null(ini.GetValue("Macro0.Step2", "Key"));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Theory]
    [InlineData("DurationMs=\n")]
    [InlineData("DurationMs=broken\n")]
    [InlineData("DurationMs=1.0\n")]
    [InlineData("DurationMs=-1\n")]
    [InlineData("DurationMs=3600001\n")]
    [InlineData("DurationMs=2147483648\n")]
    [InlineData("Key=\n")]
    [InlineData("Key=System\n")]
    [InlineData("Kind=Unknown\n")]
    [InlineData("Kind=\n")]
    [InlineData("Kind=MouseWheel\nWheelDelta=0\nX=0\nY=0\n")]
    [InlineData("Kind=MoveTo\nX=oops\nY=0\n")]
    [InlineData("Kind=MoveTo\nX=0\nY=\n")]
    [InlineData("Kind=MouseDown\nMouseButton=99\nX=0\nY=0\n")]
    [InlineData("Kind=MouseWheel\nWheelDelta=-120\nX=0\nY=0\nHorizontalWheel=\n")]
    public async Task LoadMacros_MalformedStepField_SuspendsUnrelatedSaves(string invalidFields)
    {
        var path = Path.Combine(_root, "Profiles", "Broken.ini");
        var original = ValidMacroIni + "[Macro0.Step0]\nKind=KeyPress\nKey=A\n" + invalidFields;
        File.WriteAllText(path, original);

        var profile = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Broken");

        Assert.True(profile.IsPersistenceSuspended);
        Assert.False(profile.Macros.IsEnabled);
        Assert.Empty(profile.Macros.Definitions);
        Assert.NotNull(profile.Macros.LoadError);
        profile.Name = "Unrelated edit";
        await Assert.ThrowsAsync<PersistenceSuspendedException>(() => _store.SaveProfileAsync(profile, CancellationToken.None));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("[Macro0]\nStepCount=-1\n")]
    [InlineData("[Macro0]\nStepCount=10001\n")]
    [InlineData("[Macro0]\nShortcutModifiers=16\n")]
    [InlineData("[Macro0]\nShortcutModifiers=\n")]
    [InlineData("[Macro0]\nShortcutKey=\n")]
    [InlineData("[Macro0]\nId=00000000000000000000000000000000\n")]
    [InlineData("[Macro0]\nLabel=\n")]
    [InlineData("[Macro0]\nLabel=\tUnsafe label\t\n")]
    [InlineData("[Macros]\nCount=65\n")]
    [InlineData("[Macros]\nCount=2\n[Macro1]\nId=14c0ad00d57449ed9f3663a0e665c057\nLabel=Duplicate identity\nEnabled=False\nShortcutKey=None\nShortcutModifiers=0\nStepCount=0\n")]
    [InlineData("[Macro0.Step2]\nKind=Wait\nDurationMs=0\n")]
    [InlineData("[Macro1]\nLabel=Undeclared\n")]
    [InlineData("[Macro00]\nLabel=Noncanonical\n")]
    public async Task LoadMacros_InvalidDeclaration_SuspendsAndPreservesSource(string invalidDeclaration)
    {
        var path = Path.Combine(_root, "Profiles", "Broken.ini");
        var original = ValidMacroIni + "[Macro0.Step0]\nKind=Wait\nDurationMs=0\n" + invalidDeclaration;
        File.WriteAllText(path, original);

        var profile = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Broken");

        Assert.True(profile.IsPersistenceSuspended);
        await Assert.ThrowsAsync<PersistenceSuspendedException>(() => _store.SaveProfileAsync(profile, CancellationToken.None));
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public async Task LoadMacros_MissingOptionalTapDuration_UsesAutomaticDuration()
    {
        var path = Path.Combine(_root, "Profiles", "Broken.ini");
        File.WriteAllText(path, ValidMacroIni + "[Macro0.Step0]\nKind=KeyPress\nKey=A\n");

        var profile = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Broken");

        Assert.False(profile.IsPersistenceSuspended);
        Assert.Equal(0, Assert.Single(Assert.Single(profile.Macros.Definitions).Steps).DurationMs);
    }

    [Fact]
    public async Task SaveMacros_RemovedDefinitionsAndLegacyProfile_UsesCurrentEmptyFeature()
    {
        var path = Path.Combine(_root, "Profiles", "Legacy.ini");
        File.WriteAllText(path, "[Profile]\nName=Legacy\nExecutable=legacy.exe\n");
        var profile = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Legacy");
        Assert.Empty(profile.Macros.Definitions);
        Assert.False(profile.Macros.IsEnabled);
        Assert.False(profile.IsPersistenceSuspended);

        profile.Macros.Definitions = [new() { Steps = [new() { Kind = MacroStepKind.Wait }] }];
        await _store.SaveProfileAsync(profile, CancellationToken.None);
        Assert.NotNull(IniDocument.Load(path).GetValue("Macro0.Step0", "Kind"));
        profile.Macros.Definitions = [];
        await _store.SaveProfileAsync(profile, CancellationToken.None);

        var saved = File.ReadAllText(path);
        Assert.DoesNotContain("[Macro0]", saved);
        Assert.DoesNotContain("[Macro0.Step0]", saved);
    }

    [Fact]
    public async Task SaveMacros_ProgrammaticallyMalformedStep_DoesNotReplaceExistingFile()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var original = File.ReadAllText(profile.SourcePath);
        profile.Macros.Definitions = [new() { Steps = [new() { Kind = MacroStepKind.Wait, DurationMs = -1 }] }];

        await Assert.ThrowsAsync<FormatException>(() => _store.SaveProfileAsync(profile, CancellationToken.None));

        Assert.Equal(original, File.ReadAllText(profile.SourcePath));
    }

    private const string ValidMacroIni = "[Profile]\nName=Broken\nExecutable=broken.exe\n" +
        "[Macros]\nVersion=1\nEnabled=True\nCount=1\n" +
        "[Macro0]\nId=14c0ad00d57449ed9f3663a0e665c057\nLabel=Valid\nEnabled=True\nShortcutKey=F6\nShortcutModifiers=0\nStepCount=1\n";
}
