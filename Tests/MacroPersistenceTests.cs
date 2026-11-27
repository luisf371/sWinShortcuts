using System.Globalization;
using System.IO;
using System.Windows.Input;
using sWinShortcuts.Configuration;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using Tests.Fakes;
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
                    ToggleMode = true,
                    StartOnPress = true,
                    CancelOnMouseMovement = true,
                    ShowPlayingSteps = true,
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
            Assert.True(loaded.Macros.Definitions[0].ToggleMode);
            Assert.True(loaded.Macros.Definitions[0].StartOnPress);
            Assert.True(loaded.Macros.Definitions[0].CancelOnMouseMovement);
            Assert.True(loaded.Macros.Definitions[0].ShowPlayingSteps);
            Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, loaded.Macros.Definitions[0].ShortcutModifiers);
            // Old Key press and Mouse click steps load as Down, Wait, Up groups.
            Assert.Equal(IniProfileStore.ExpandCompactPresses(profile.Macros.Definitions[0].Steps), loaded.Macros.Definitions[0].Steps);
            Assert.Equal(Key.None, loaded.Macros.Definitions[1].ShortcutKey);
            Assert.False(loaded.Macros.Definitions[1].ToggleMode);
            Assert.False(loaded.Macros.Definitions[1].StartOnPress);
            Assert.False(loaded.Macros.Definitions[1].CancelOnMouseMovement);
            Assert.False(loaded.Macros.Definitions[1].ShowPlayingSteps);
            Assert.Equal(profile.Macros.Definitions[1].Steps, loaded.Macros.Definitions[1].Steps);
            Assert.Null(MacroValidation.GetPlaybackError(loaded.Macros.Definitions[0]));
            Assert.NotNull(MacroValidation.GetPlaybackError(loaded.Macros.Definitions[1]));
            var ini = IniDocument.Load(profile.SourcePath);
            Assert.Equal("3", ini.GetValue("Macro0", "ShortcutModifiers"));
            Assert.Equal("True", ini.GetValue("Macro0", "ToggleMode"));
            Assert.Equal("False", ini.GetValue("Macro1", "ToggleMode"));
            Assert.Equal("True", ini.GetValue("Macro0", "StartOnPress"));
            Assert.Equal("False", ini.GetValue("Macro1", "StartOnPress"));
            Assert.Equal("True", ini.GetValue("Macro0", "CancelOnMouseMovement"));
            Assert.Equal("False", ini.GetValue("Macro1", "CancelOnMouseMovement"));
            Assert.Equal("True", ini.GetValue("Macro0", "ShowPlayingSteps"));
            Assert.Equal("False", ini.GetValue("Macro1", "ShowPlayingSteps"));
            Assert.Null(ini.GetValue("Macro0.Step0", "DurationMs"));
            Assert.Null(ini.GetValue("Macro0.Step2", "Key"));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task SaveAndLoad_MoveCursorBy_RoundTripsSignedOffsetsAsXAndY()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.IsEnabled = true;
        profile.Macros.Definitions = [new()
        {
            IsEnabled = true, ShortcutKey = Key.F6,
            Steps = [new() { Kind = MacroStepKind.MoveCursorBy, X = -25, Y = 12 }]
        }];

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var loaded = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Game");

        Assert.False(loaded.IsPersistenceSuspended);
        Assert.Equal(profile.Macros.Definitions[0].Steps, loaded.Macros.Definitions[0].Steps);
        var ini = IniDocument.Load(profile.SourcePath);
        Assert.Equal(new[] { "Kind", "X", "Y" }, ini.GetSection("Macro0.Step0").Keys);
        Assert.Equal("MoveCursorBy", ini.GetValue("Macro0.Step0", "Kind"));
        Assert.Equal("-25", ini.GetValue("Macro0.Step0", "X"));
    }

    [Fact]
    public async Task SaveAndLoad_HeldButtonOption_WritesOnlyNonDefaultChoices()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.IsEnabled = true;
        profile.Macros.Definitions = [new()
        {
            IsEnabled = true, ShortcutKey = Key.F6,
            Steps =
            [
                new() { Kind = MacroStepKind.MoveTo, X = 5, Y = 6 },
                new() { Kind = MacroStepKind.MoveCursorBy, X = 1, Y = 2, HeldButtons = MacroHeldButtonPolicy.Allow },
                new() { Kind = MacroStepKind.MoveMouseBy, X = -300, Y = 40, HeldButtons = MacroHeldButtonPolicy.Wait }
            ]
        }];

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var loaded = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Game");

        Assert.False(loaded.IsPersistenceSuspended);
        Assert.Equal(profile.Macros.Definitions[0].Steps, loaded.Macros.Definitions[0].Steps);
        var ini = IniDocument.Load(profile.SourcePath);
        Assert.Equal(new[] { "Kind", "X", "Y" }, ini.GetSection("Macro0.Step0").Keys);
        Assert.Equal("Allow", ini.GetValue("Macro0.Step1", "HeldButtons"));
        Assert.Equal("MoveMouseBy", ini.GetValue("Macro0.Step2", "Kind"));
        Assert.Equal("Wait", ini.GetValue("Macro0.Step2", "HeldButtons"));
    }

    [Fact]
    public async Task SaveAndLoad_SavedPositionSteps_RoundTripSlotsAndNonDefaultHeldButtonOptions()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new()
        {
            ShortcutKey = Key.F6,
            Steps =
            [
                new() { Kind = MacroStepKind.SavePosition, Slot = 8 },
                new() { Kind = MacroStepKind.MoveToSaved, Slot = 8 },
                new() { Kind = MacroStepKind.MoveToSaved, Slot = 8, HeldButtons = MacroHeldButtonPolicy.Wait },
                new() { Kind = MacroStepKind.MoveToSaved, Slot = 8, HeldButtons = MacroHeldButtonPolicy.Allow }
            ]
        }];

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var loaded = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Game");

        Assert.False(loaded.IsPersistenceSuspended);
        Assert.Equal(profile.Macros.Definitions[0].Steps, loaded.Macros.Definitions[0].Steps);
        var ini = IniDocument.Load(profile.SourcePath);
        Assert.Equal(new[] { "Kind", "Slot" }, ini.GetSection("Macro0.Step0").Keys);
        Assert.Equal(new[] { "Kind", "Slot" }, ini.GetSection("Macro0.Step1").Keys);
        Assert.Equal("Wait", ini.GetValue("Macro0.Step2", "HeldButtons"));
        Assert.Equal("Allow", ini.GetValue("Macro0.Step3", "HeldButtons"));
    }

    [Fact]
    public async Task SaveAndLoad_MouseFreezeSteps_RoundTripWithOnlyTheirKind()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.IsEnabled = true;
        profile.Macros.Definitions = [new()
        {
            IsEnabled = true, ShortcutKey = Key.F6,
            Steps = [new() { Kind = MacroStepKind.FreezeMouse }, new() { Kind = MacroStepKind.UnfreezeMouse }]
        }];

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var loaded = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Game");

        Assert.False(loaded.IsPersistenceSuspended);
        Assert.Equal(profile.Macros.Definitions[0].Steps, loaded.Macros.Definitions[0].Steps);
        var ini = IniDocument.Load(profile.SourcePath);
        Assert.Equal(new[] { "Kind" }, ini.GetSection("Macro0.Step0").Keys);
        Assert.Equal(new[] { "Kind" }, ini.GetSection("Macro0.Step1").Keys);
        Assert.Equal("FreezeMouse", ini.GetValue("Macro0.Step0", "Kind"));
        Assert.Equal("UnfreezeMouse", ini.GetValue("Macro0.Step1", "Kind"));
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
    [InlineData("Kind=MoveTo\nX=0\nY=0\nHeldButtons=Sometimes\n")]
    [InlineData("Kind=MoveMouseBy\nX=0\nY=0\nHeldButtons=\n")]
    [InlineData("Kind=MoveMouseBy\nX=0\nY=0\nHeldButtons=7\n")]
    [InlineData("Kind=MoveMouseBy\nX=1\n")]
    [InlineData("Kind=SavePosition\n")]
    [InlineData("Kind=SavePosition\nSlot=broken\n")]
    [InlineData("Kind=SavePosition\nSlot=0\n")]
    [InlineData("Kind=MoveToSaved\n")]
    [InlineData("Kind=MoveToSaved\nSlot=1.0\n")]
    [InlineData("Kind=MoveToSaved\nSlot=9\n")]
    [InlineData("Kind=MoveToSaved\nSlot=1\nHeldButtons=Sometimes\n")]
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
    [InlineData(AppMouseButton.Left)]
    [InlineData(AppMouseButton.Right)]
    [InlineData(AppMouseButton.Middle)]
    [InlineData(AppMouseButton.XButton1)]
    [InlineData(AppMouseButton.XButton2)]
    public async Task SaveAndLoad_MouseShortcutThenKeyboard_PreservesTargetAndRemovesInactiveField(AppMouseButton button)
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.Macros.Definitions = [new()
        {
            ShortcutMouseButton = button,
            ShortcutModifiers = ModifierKeys.Control | ModifierKeys.Alt,
            Steps = [new() { Kind = MacroStepKind.Wait }]
        }];

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var loaded = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Game");
        var macro = Assert.Single(loaded.Macros.Definitions);
        Assert.False(loaded.IsPersistenceSuspended);
        Assert.Equal(Key.None, macro.ShortcutKey);
        Assert.Equal(button, macro.ShortcutMouseButton);
        Assert.Equal(InputTrigger.FromMouseButton(button), macro.ShortcutTrigger);
        Assert.Equal(ModifierKeys.Control | ModifierKeys.Alt, macro.ShortcutModifiers);
        Assert.Equal(button.ToString(), IniDocument.Load(loaded.SourcePath).GetValue("Macro0", "ShortcutMouseButton"));

        loaded.Macros.Definitions = [macro with { ShortcutKey = Key.F6, ShortcutMouseButton = null }];
        await _store.SaveProfileAsync(loaded, CancellationToken.None);
        Assert.False(IniDocument.Load(loaded.SourcePath).TryGetSourceValue("Macro0", "ShortcutMouseButton", out _));
        var keyboard = Assert.Single((await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Game").Macros.Definitions);
        Assert.Equal(InputTrigger.FromKey(Key.F6), keyboard.ShortcutTrigger);
        Assert.Null(keyboard.ShortcutMouseButton);
    }

    [Theory]
    [InlineData("[Macro0]\nStepCount=-1\n")]
    [InlineData("[Macro0]\nStepCount=1001\n")]
    [InlineData("[Macro0]\nShortcutModifiers=16\n")]
    [InlineData("[Macro0]\nShortcutModifiers=\n")]
    [InlineData("[Macro0]\nShortcutKey=\n")]
    [InlineData("[Macro0]\nShortcutMouseButton=\n")]
    [InlineData("[Macro0]\nShortcutMouseButton=broken\n")]
    [InlineData("[Macro0]\nShortcutMouseButton=0\n")]
    [InlineData("[Macro0]\nShortcutMouseButton=99\n")]
    [InlineData("[Macro0]\nShortcutMouseButton=Middle\n")]
    [InlineData("[Macro0]\nToggleMode=\n")]
    [InlineData("[Macro0]\nToggleMode=broken\n")]
    [InlineData("[Macro0]\nStartOnPress=\n")]
    [InlineData("[Macro0]\nStartOnPress=broken\n")]
    [InlineData("[Macro0]\nCancelOnMouseMovement=\n")]
    [InlineData("[Macro0]\nCancelOnMouseMovement=broken\n")]
    [InlineData("[Macro0]\nShowPlayingSteps=\n")]
    [InlineData("[Macro0]\nShowPlayingSteps=broken\n")]
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
    public async Task LoadMacros_MissingOptionalFields_ConvertsAutomaticHoldAndKeepsOptionalModesOff()
    {
        var path = Path.Combine(_root, "Profiles", "Broken.ini");
        File.WriteAllText(path, ValidMacroIni + "[Macro0.Step0]\nKind=KeyPress\nKey=A\n");

        var profile = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Broken");

        Assert.False(profile.IsPersistenceSuspended);
        var macro = Assert.Single(profile.Macros.Definitions);
        Assert.Equal(MacroPresses.Press(Key.A), macro.Steps);
        Assert.False(macro.ToggleMode);
        Assert.False(macro.StartOnPress);
        Assert.False(macro.CancelOnMouseMovement);
        Assert.False(macro.ShowPlayingSteps);
        Assert.Null(macro.ShortcutMouseButton);
        Assert.Equal(InputTrigger.FromKey(Key.F6), macro.ShortcutTrigger);
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

    [Fact]
    public async Task SaveAndLoad_ShortcutRightModifiers_RoundTrip()
    {
        var profile = ProfileFactory.CreateCustomProfile("Sides", "sides.exe");
        profile.Macros.Definitions =
        [
            new()
            {
                ShortcutKey = Key.F6,
                ShortcutModifiers = ModifierKeys.Control | ModifierKeys.Shift,
                ShortcutRightModifiers = ModifierKeys.Control,
                Steps = [new() { Kind = MacroStepKind.KeyPress, Key = Key.A }]
            }
        ];

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var loaded = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Sides");

        Assert.Equal("2", IniDocument.Load(profile.SourcePath).GetValue("Macro0", "ShortcutRightModifiers"));
        Assert.Equal(ModifierKeys.Control, loaded.Macros.Definitions[0].ShortcutRightModifiers);
    }

    [Theory]
    [InlineData("", ModifierKeys.None)] // saved before sides existed: left
    [InlineData("ShortcutRightModifiers=2\n", ModifierKeys.Control)]
    [InlineData("ShortcutRightModifiers=1\n", ModifierKeys.None)] // Alt isn't one of the shortcut's modifiers
    public async Task LoadMacros_ShortcutRightModifiers_DefaultsToLeftAndKeepsOnlyShortcutModifiers(string line, ModifierKeys expected)
    {
        var path = Path.Combine(_root, "Profiles", "Broken.ini");
        File.WriteAllText(path, ValidMacroIni.Replace("ShortcutModifiers=0\n", "ShortcutModifiers=2\n" + line) +
            "[Macro0.Step0]\nKind=KeyPress\nKey=A\n");

        var profile = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Broken");

        Assert.False(profile.IsPersistenceSuspended);
        Assert.Equal(expected, profile.Macros.Definitions[0].ShortcutRightModifiers);
    }

    [Fact]
    public void ExpandCompactPresses_KeyPress_BecomesDownWaitUp()
    {
        var steps = IniProfileStore.ExpandCompactPresses([new() { Kind = MacroStepKind.KeyPress, Key = Key.Q, DurationMs = 70 }]);

        Assert.Equal(MacroPresses.Press(Key.Q, 70), steps);
    }

    [Fact]
    public void ExpandCompactPresses_AutoHold_Becomes50Ms()
    {
        var steps = IniProfileStore.ExpandCompactPresses([new() { Kind = MacroStepKind.KeyPress, Key = Key.Q }]);

        Assert.Equal(50, steps[1].DurationMs);
    }

    [Fact]
    public void ExpandCompactPresses_MouseClick_BecomesMoveToThenGroup()
    {
        var steps = IniProfileStore.ExpandCompactPresses(
            [new() { Kind = MacroStepKind.MouseClick, MouseButton = AppMouseButton.Right, X = -40, Y = 900 }]);

        Assert.Equal(MacroPresses.Click(AppMouseButton.Right, -40, 900), steps);
        Assert.Equal(MacroHeldButtonPolicy.Stop, steps[0].HeldButtons);
    }

    [Fact]
    public void ExpandCompactPresses_NoCompactSteps_ReturnsSameArray()
    {
        MacroStep[] steps = [.. MacroPresses.Press(Key.A), new() { Kind = MacroStepKind.MouseWheel, WheelDelta = 120 }];

        Assert.Same(steps, IniProfileStore.ExpandCompactPresses(steps));
    }

    [Fact]
    public void ExpandCompactPresses_MixedSteps_KeepsOrder()
    {
        var wheel = new MacroStep { Kind = MacroStepKind.MouseWheel, WheelDelta = -120 };
        var wait = new MacroStep { Kind = MacroStepKind.Wait, DurationMs = 300 };

        var steps = IniProfileStore.ExpandCompactPresses(
        [
            wheel,
            new() { Kind = MacroStepKind.KeyPress, Key = Key.B, DurationMs = 20 },
            wait,
            new() { Kind = MacroStepKind.MouseClick, MouseButton = AppMouseButton.Left, X = 5, Y = 6, DurationMs = 15 }
        ]);

        Assert.Equal([wheel, .. MacroPresses.Press(Key.B, 20), wait, .. MacroPresses.Click(AppMouseButton.Left, 5, 6, 15)], steps);
    }

    [Fact]
    public void ExpandCompactPresses_KeyPressOnHeldKey_ReturnsOriginal()
    {
        // Converting would turn this unplayable macro into a playable repeated Key down.
        MacroStep[] steps =
        [
            new() { Kind = MacroStepKind.KeyDown, Key = Key.A },
            new() { Kind = MacroStepKind.KeyPress, Key = Key.A },
            new() { Kind = MacroStepKind.KeyUp, Key = Key.A }
        ];

        Assert.Same(steps, IniProfileStore.ExpandCompactPresses(steps));
    }

    [Theory]
    [InlineData(MacroValidation.MaxSteps - 2, true)]
    [InlineData(MacroValidation.MaxSteps - 1, false)]
    public void ExpandCompactPresses_StepLimit_ConvertsOnlyWhenItFits(int length, bool converts)
    {
        // One Key press adds two rows: 998 steps become exactly 1,000, 999 would become 1,001.
        MacroStep[] steps = [new() { Kind = MacroStepKind.KeyPress, Key = Key.A }, .. Enumerable.Repeat(new MacroStep { Kind = MacroStepKind.Wait }, length - 1)];

        var result = IniProfileStore.ExpandCompactPresses(steps);

        Assert.Equal(converts ? MacroValidation.MaxSteps : length, result.Length);
        Assert.Equal(converts ? MacroStepKind.KeyDown : MacroStepKind.KeyPress, result[0].Kind);
    }

    [Fact]
    public async Task Load_OldKeyPressAndMouseClick_ConvertsThenSavesNewForm()
    {
        var path = Path.Combine(_root, "Profiles", "Broken.ini");
        File.WriteAllText(path, MacroIni(2) + "[Macro0.Step0]\nKind=KeyPress\nKey=A\nDurationMs=0\n" +
            "[Macro0.Step1]\nKind=MouseClick\nMouseButton=Left\nX=10\nY=20\nDurationMs=30\n");

        var profile = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Broken");
        MacroStep[] expected = [.. MacroPresses.Press(Key.A), .. MacroPresses.Click(AppMouseButton.Left, 10, 20, 30)];

        Assert.False(profile.IsPersistenceSuspended);
        Assert.Equal(expected, Assert.Single(profile.Macros.Definitions).Steps);
        Assert.Null(MacroValidation.GetPlaybackError(profile.Macros.Definitions[0]));
        await _store.SaveProfileAsync(profile, CancellationToken.None);
        Assert.Equal("KeyDown", IniDocument.Load(path).GetValue("Macro0.Step0", "Kind"));
        Assert.Equal("7", IniDocument.Load(path).GetValue("Macro0", "StepCount"));
        var reloaded = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Broken");
        Assert.Equal(expected, reloaded.Macros.Definitions[0].Steps);
    }

    [Fact]
    public async Task Load_InvalidStepAfterCompactStep_ErrorNamesOriginalStep()
    {
        // -1 parses but fails the format check, so the definition is not converted and step numbers stay as written.
        var path = Path.Combine(_root, "Profiles", "Broken.ini");
        File.WriteAllText(path, MacroIni(2) + "[Macro0.Step0]\nKind=KeyPress\nKey=A\n[Macro0.Step1]\nKind=Wait\nDurationMs=-1\n");

        var profile = (await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Broken");

        Assert.True(profile.IsPersistenceSuspended);
        Assert.Contains("Step 2:", profile.Macros.LoadError);
    }

    [Fact]
    public async Task Load_ConversionOverMaxSteps_KeepsOldStepsAndBlocksPlayback()
    {
        var path = Path.Combine(_root, "Profiles", "Broken.ini");
        var count = MacroValidation.MaxSteps - 1;
        var ini = new System.Text.StringBuilder(MacroIni(count) + "[Macro0.Step0]\nKind=KeyPress\nKey=A\n");
        for (var i = 1; i < count; i++) ini.Append(CultureInfo.InvariantCulture, $"[Macro0.Step{i}]\nKind=Wait\nDurationMs=1\n");
        File.WriteAllText(path, ini.ToString());

        var macro = Assert.Single((await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Broken").Macros.Definitions);

        Assert.Equal(count, macro.Steps.Length);
        Assert.Equal(MacroStepKind.KeyPress, macro.Steps[0].Kind);
        Assert.Equal("Step 1: replace this old Key press step.", MacroValidation.GetPlaybackError(macro));
    }

    [Fact]
    public async Task Load_KeyPressOnHeldKey_KeepsOldStepsAndBlocksPlayback()
    {
        var path = Path.Combine(_root, "Profiles", "Broken.ini");
        File.WriteAllText(path, MacroIni(3) + "[Macro0.Step0]\nKind=KeyDown\nKey=A\n[Macro0.Step1]\nKind=KeyPress\nKey=A\n" +
            "[Macro0.Step2]\nKind=KeyUp\nKey=A\n");

        var macro = Assert.Single((await _store.LoadProfilesAsync(CancellationToken.None)).Single(p => p.Name == "Broken").Macros.Definitions);

        Assert.Equal(MacroStepKind.KeyPress, macro.Steps[1].Kind);
        Assert.Equal("Step 2: replace this old Key press step.", MacroValidation.GetPlaybackError(macro));
    }

    private static string MacroIni(int stepCount) => ValidMacroIni.Replace("StepCount=1", FormattableString.Invariant($"StepCount={stepCount}"));

    private const string ValidMacroIni = "[Profile]\nName=Broken\nExecutable=broken.exe\n" +
        "[Macros]\nVersion=1\nEnabled=True\nCount=1\n" +
        "[Macro0]\nId=14c0ad00d57449ed9f3663a0e665c057\nLabel=Valid\nEnabled=True\nShortcutKey=F6\nShortcutModifiers=0\nStepCount=1\n";
}
