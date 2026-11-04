using System.IO;
using System.Windows.Input;
using sWinShortcuts.Configuration;
using sWinShortcuts.Factories;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using sWinShortcuts.ViewModels;
using Tests.Fakes;
using Xunit;
using MouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

// [AltShortcut]/[AltShortcutBindings] format, conversion of split Alt + Mouse/Alt + Keyboard files, and the
// first-save backup (Alt_Shortcuts_Migration_v2.md §5).
public sealed class AltShortcutPersistenceTests : IDisposable
{
    private const string META = "[AltShortcut]\nVersion=1\nEnabled=True\nHoldThreshold=90\n[AltShortcutBindings]\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "sWinShortcutsTests", Guid.NewGuid().ToString("N"));
    private readonly NullLoggerService _logger = new() { IsEnabled = true };
    private readonly IniProfileStore _store;

    public AltShortcutPersistenceTests() => _store = new IniProfileStore(_root, _logger);

    private string GamePath => Path.Combine(_root, "Profiles", "Game.ini");
    private string BackupPath => GamePath + ".pre-alt-shortcut.bak";

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_NewFormat_RoundTripsEveryRowKindInFixedOrder()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        profile.AltMouse.IsEnabled = profile.AltKeyboard.IsEnabled = true;
        profile.AltMouse.HoldThresholdMilliseconds = profile.AltKeyboard.HoldThresholdMilliseconds = 75;
        profile.AltMouse.Bindings = new()
        {
            [MouseButton.Middle] = new() { HoldKey = Key.J },
            [MouseButton.XButton1] = new(),
            [MouseButton.Left] = new() { TapKey = Key.F, HoldKey = Key.G },
            [MouseButton.Right] = new() { TapKey = Key.H }
        };
        profile.AltMouse.WheelUpKey = Key.E;
        profile.AltMouse.WheelDownKey = Key.Q;
        profile.AltKeyboard.Bindings = new()
        {
            [Key.T] = new(),
            [Key.Left] = new() { TapKey = Key.A },
            [Key.Q] = new() { TapKey = Key.Z, HoldKey = Key.X }
        };

        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var firstBytes = File.ReadAllBytes(profile.SourcePath);
        var lines = File.ReadAllLines(profile.SourcePath);
        var meta = Array.IndexOf(lines, "[AltShortcut]");
        Assert.Equal(new[] { "Version=1", "Enabled=True", "HoldThreshold=75" }, lines[(meta + 1)..(meta + 4)]);
        var rows = Array.IndexOf(lines, "[AltShortcutBindings]");
        Assert.Equal(
            new[] { "Mouse:Left=F|G", "Mouse:Right=H|", "Mouse:Middle=|J", "Mouse:XButton1=|", "Wheel:Up=E|", "Wheel:Down=Q|",
             "Key:Q=Z|X", "Key:T=|", "Key:Left=A|" },
            lines[(rows + 1)..(rows + 10)]);
        Assert.DoesNotContain(lines, line => line.StartsWith("[AltMouse", StringComparison.Ordinal) ||
            line.StartsWith("[AltKeyboard", StringComparison.Ordinal));
        Assert.False(File.Exists(profile.SourcePath + ".pre-alt-shortcut.bak"));

        var loaded = Assert.Single(await _store.LoadProfilesAsync(CancellationToken.None), p => !p.IsWindowsProfile);
        Assert.False(loaded.IsPersistenceSuspended);
        Assert.True(loaded.AltMouse.IsEnabled && loaded.AltKeyboard.IsEnabled);
        Assert.Equal(75, loaded.AltMouse.HoldThresholdMilliseconds);
        Assert.Equal(75, loaded.AltKeyboard.HoldThresholdMilliseconds);
        Assert.Equal<(Key?, Key?)>((Key.F, Key.G), (loaded.AltMouse.Bindings[MouseButton.Left].TapKey, loaded.AltMouse.Bindings[MouseButton.Left].HoldKey));
        Assert.Equal<(Key?, Key?)>((null, Key.J), (loaded.AltMouse.Bindings[MouseButton.Middle].TapKey, loaded.AltMouse.Bindings[MouseButton.Middle].HoldKey));
        Assert.Equal<(Key?, Key?)>((Key.H, null), (loaded.AltMouse.Bindings[MouseButton.Right].TapKey, loaded.AltMouse.Bindings[MouseButton.Right].HoldKey));
        Assert.False(loaded.AltMouse.Bindings[MouseButton.XButton1].SuppressOriginalWhileAltIsHeld);
        Assert.Equal<(Key?, Key?)>((Key.E, Key.Q), (loaded.AltMouse.WheelUpKey, loaded.AltMouse.WheelDownKey));
        Assert.Equal<(Key?, Key?)>((Key.Z, Key.X), (loaded.AltKeyboard.Bindings[Key.Q].TapKey, loaded.AltKeyboard.Bindings[Key.Q].HoldKey));
        Assert.Equal(Key.A, loaded.AltKeyboard.Bindings[Key.Left].TapKey);
        Assert.False(loaded.AltKeyboard.Bindings[Key.T].SuppressOriginalWhileAltIsHeld);

        await _store.SaveProfileAsync(loaded, CancellationToken.None);
        Assert.Equal(firstBytes, File.ReadAllBytes(profile.SourcePath));
    }

    [Fact]
    public async Task Load_NoAltSections_IsOffWith200MsAndNoRows()
    {
        var profile = await LoadAsync("");

        Assert.False(profile.AltMouse.IsEnabled || profile.AltKeyboard.IsEnabled);
        Assert.Equal(200, profile.AltMouse.HoldThresholdMilliseconds);
        Assert.Equal(200, profile.AltKeyboard.HoldThresholdMilliseconds);
        Assert.Empty(profile.AltMouse.Bindings);
        Assert.Empty(profile.AltKeyboard.Bindings);
        Assert.Equal(200, ProfileFactory.CreateCustomProfile("New", "new.exe").AltKeyboard.HoldThresholdMilliseconds);
    }

    // §5.3 toggle table. A family is live when a row has a target; "kept" means its draft or bound row survives.
    [Theory]
    [InlineData(true, false, false, false, true, true, true)]   // neither live: on if either was on
    [InlineData(false, false, false, false, false, true, true)]
    [InlineData(false, true, true, false, false, true, true)]   // only mouse live: its value
    [InlineData(true, false, false, true, false, true, true)]   // only keyboard live: its value
    [InlineData(true, true, true, true, true, true, true)]      // both live, same value
    [InlineData(false, true, false, true, false, true, true)]
    [InlineData(true, true, false, true, true, true, false)]    // both live, different: on, the off family's rows go
    [InlineData(false, true, true, true, true, false, true)]
    public async Task Load_SplitFile_MergesToggles(
        bool mouseOn, bool mouseLive, bool keyOn, bool keyLive, bool expectedOn, bool mouseKept, bool keyKept)
    {
        var profile = await LoadAsync(
            $"[AltMouse]\nEnabled={mouseOn}\nHoldThreshold=150\n[AltMouseBindings]\nLeft={(mouseLive ? "F|" : "|")}\n" +
            $"[AltKeyboard]\nEnabled={keyOn}\nHoldThreshold=150\n[AltKeyboardBindings]\nQ={(keyLive ? "Z|" : "|")}\n");

        Assert.Equal(expectedOn, profile.AltMouse.IsEnabled);
        Assert.Equal(expectedOn, profile.AltKeyboard.IsEnabled);
        Assert.Equal(mouseKept, profile.AltMouse.Bindings.ContainsKey(MouseButton.Left));
        Assert.Equal(keyKept, profile.AltKeyboard.Bindings.ContainsKey(Key.Q));
        Assert.False(profile.IsPersistenceSuspended);
        Assert.Equal(!keyKept, _logger.Messages.Any(m => m.Contains("removed 1 Alt + Keyboard rows that were switched off")));
        Assert.Equal(!mouseKept, _logger.Messages.Any(m => m.Contains("removed 1 Alt + Mouse rows and the wheel targets")));
    }

    [Fact]
    public async Task Load_SplitFile_WheelTargetsMakeMouseLiveAndAreRemovedWhenSwitchedOff()
    {
        var profile = await LoadAsync(
            "[AltMouse]\nEnabled=False\nWheelUp=E\nWheelDown=F\n[AltKeyboard]\nEnabled=True\n[AltKeyboardBindings]\nQ=Z|\n");

        Assert.True(profile.AltMouse.IsEnabled && profile.AltKeyboard.IsEnabled);
        Assert.Null(profile.AltMouse.WheelUpKey);
        Assert.Null(profile.AltMouse.WheelDownKey);
        Assert.Equal(Key.Z, profile.AltKeyboard.Bindings[Key.Q].TapKey);
        Assert.Contains(_logger.Messages, m => m.Contains("removed 0 Alt + Mouse rows and the wheel targets that were switched off"));
    }

    // §5.3 threshold table, with both families on so no rows are removed.
    [Theory]
    [InlineData(90, true, 90, true, 90)]     // equal
    [InlineData(90, false, 120, true, 120)]  // only the keyboard has a Hold row
    [InlineData(90, true, 120, false, 90)]   // only the mouse has a Hold row
    [InlineData(90, true, 120, true, 90)]    // real conflict: Alt + Mouse wins, logged
    [InlineData(90, false, 120, false, 90)]  // no Hold rows: Alt + Mouse
    public async Task Load_SplitFile_MergesThresholds(int mouseMs, bool mouseHold, int keyMs, bool keyHold, int expected)
    {
        var profile = await LoadAsync(
            $"[AltMouse]\nEnabled=True\nHoldThreshold={mouseMs}\n[AltMouseBindings]\nLeft=F|{(mouseHold ? "G" : "")}\n" +
            $"[AltKeyboard]\nEnabled=True\nHoldThreshold={keyMs}\n[AltKeyboardBindings]\nQ=Z|{(keyHold ? "X" : "")}\n");

        Assert.Equal(expected, profile.AltMouse.HoldThresholdMilliseconds);
        Assert.Equal(expected, profile.AltKeyboard.HoldThresholdMilliseconds);
        Assert.Equal(mouseHold && keyHold && mouseMs != keyMs,
            _logger.Messages.Any(m => m.Contains($"Alt + Keyboard hold threshold {keyMs} ms replaced by {mouseMs} ms")));
    }

    [Fact]
    public async Task Load_SplitFile_MissingOrUnreadableThreshold_KeepsHistorical150Ms()
    {
        var profile = await LoadAsync("[AltMouse]\nEnabled=True\n[AltKeyboard]\nEnabled=True\nHoldThreshold=abc\n");

        Assert.Equal(150, profile.AltMouse.HoldThresholdMilliseconds);
        Assert.Equal(150, profile.AltKeyboard.HoldThresholdMilliseconds);
    }

    [Fact]
    public async Task Load_SplitFile_DropsUndefinedButtonsAndKeepsLegacyCommaMeaning()
    {
        var profile = await LoadAsync("[AltMouse]\nEnabled=True\n[AltMouseBindings]\n99=F|G\nLeft, Right=H|J\n");

        Assert.False(profile.IsPersistenceSuspended);
        Assert.Equal(MouseButton.Middle, Assert.Single(profile.AltMouse.Bindings).Key); // Enum.TryParse ORs Left|Right
        Assert.Contains(_logger.Messages, m => m.Contains("removed undefined mouse button 99"));
        await _store.SaveProfileAsync(profile, CancellationToken.None);
        Assert.Equal("H|J", IniDocument.Load(GamePath).GetValue("AltShortcutBindings", "Mouse:Middle"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("[AltShortcutBindings]\n")]
    public async Task Load_NewSections_WinOverStaleSplitSections(string emptyBindings)
    {
        var profile = await LoadAsync(
            "[AltMouse]\nEnabled=True\nHoldThreshold=60\n[AltMouseBindings]\nLeft=F|G\n" +
            "[AltKeyboard]\nEnabled=True\n[AltKeyboardBindings]\nQ=Z|\n" +
            "[AltShortcut]\nVersion=1\nEnabled=False\nHoldThreshold=120\n" + emptyBindings);

        Assert.False(profile.IsPersistenceSuspended);
        Assert.False(profile.AltMouse.IsEnabled || profile.AltKeyboard.IsEnabled);
        Assert.Equal(120, profile.AltMouse.HoldThresholdMilliseconds);
        Assert.Equal(120, profile.AltKeyboard.HoldThresholdMilliseconds);
        Assert.Empty(profile.AltMouse.Bindings);
        Assert.Empty(profile.AltKeyboard.Bindings);
    }

    [Fact]
    public async Task Load_NewFormat_AcceptsAliasesDraftsAndKeysOutsideTheEditorList()
    {
        var profile = await LoadAsync(
            "[AltShortcut]\nversion=1\nENABLED=true\nHoldThreshold=5\n[altshortcutbindings]\n" +
            "mouse:4=f|\nkey:q=None|x\nKey:F13=A|\nKey:T=|\nKey:Y=\nWheel:Up=|\nWheel:Down=E|None\n");

        Assert.False(profile.IsPersistenceSuspended);
        Assert.True(profile.AltMouse.IsEnabled);
        Assert.Equal(10, profile.AltMouse.HoldThresholdMilliseconds);
        Assert.Equal(Key.F, profile.AltMouse.Bindings[MouseButton.XButton1].TapKey);
        Assert.Equal<(Key?, Key?)>((null, Key.X), (profile.AltKeyboard.Bindings[Key.Q].TapKey, profile.AltKeyboard.Bindings[Key.Q].HoldKey));
        Assert.Equal(Key.A, profile.AltKeyboard.Bindings[Key.F13].TapKey);
        Assert.True(profile.AltKeyboard.Bindings.ContainsKey(Key.T));
        Assert.False(profile.AltKeyboard.Bindings.ContainsKey(Key.Y)); // a blank value deletes the row
        Assert.Null(profile.AltMouse.WheelUpKey);
        Assert.Equal(Key.E, profile.AltMouse.WheelDownKey);
    }

    [Theory]
    [InlineData("[AltShortcutBindings]\nKey:Q=Z|X\n")]
    [InlineData("[AltShortcutBindings]\n")]
    [InlineData("[AltShortcut]\nVersion=2\nEnabled=True\nHoldThreshold=90\n")]
    [InlineData("[AltShortcut]\nEnabled=True\nHoldThreshold=90\n")]
    [InlineData("[AltShortcut]\nVersion=1\nEnabled=yes\nHoldThreshold=90\n")]
    [InlineData("[AltShortcut]\nVersion=1\nHoldThreshold=90\n")]
    [InlineData("[AltShortcut]\nVersion=1\nEnabled=True\n")]
    [InlineData("[AltShortcut]\nVersion=1\nEnabled=True\nHoldThreshold=9x\n")]
    [InlineData(META + "Mouse:Left, Right=F|G\n")]
    [InlineData(META + "Key:Q=A, B|\n")]
    [InlineData(META + "Key:None=F|\n")]
    [InlineData(META + "Key:LeftAlt=F|\n")]
    [InlineData(META + "Key:RightAlt=F|\n")]
    [InlineData(META + "Mouse:9=F|\n")]
    [InlineData(META + "Mouse:Side=F|\n")]
    [InlineData(META + "Wheel:Sideways=F|\n")]
    [InlineData(META + "Q=F|\n")]
    [InlineData(META + "Key:Q=F\n")]
    [InlineData(META + "Key:Q=F|G|H\n")]
    [InlineData(META + "Key:Q=NotAKey|\n")]
    [InlineData(META + "Wheel:Up=E|F\n")]
    [InlineData(META + "Key:Enter=F|\nKey:Return=G|\n")]
    [InlineData(META + "Key:65=F|\nKey:V=G|\n")]
    [InlineData(META + "Mouse:4=F|\nMouse:XButton1=G|\n")]
    public async Task Load_InvalidNewFormat_SuspendsPersistenceAndKeepsOtherFeatures(string altSections)
    {
        var profile = await LoadAsync("[RapidFire]\nEnabled=True\n" + altSections);
        var original = File.ReadAllBytes(GamePath);

        Assert.True(profile.RapidFire.IsEnabled);
        Assert.True(profile.IsPersistenceSuspended);
        Assert.False(string.IsNullOrEmpty(profile.AltShortcutLoadError));
        Assert.False(profile.AltMouse.IsEnabled || profile.AltKeyboard.IsEnabled);
        Assert.Empty(profile.AltMouse.Bindings);
        Assert.Empty(profile.AltKeyboard.Bindings);
        Assert.Null(profile.AltMouse.WheelUpKey ?? profile.AltMouse.WheelDownKey);
        Assert.Contains(_logger.Messages, m => m.Contains("Invalid Alt + Shortcut settings"));
        using (var editor = new ProfileViewModel(profile, new FakeDisplayService(), new RecordingColorControlService()))
        {
            Assert.StartsWith("Alt + Shortcut settings could not be loaded: ", editor.PersistenceWarning);
        }

        await Assert.ThrowsAsync<PersistenceSuspendedException>(() => _store.SaveProfileAsync(profile, CancellationToken.None));
        Assert.Equal(original, File.ReadAllBytes(GamePath));
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public async Task Load_AltAndMacroErrors_WarningNamesBoth()
    {
        var profile = await LoadAsync(META + "Key:Q=F\n[Macros]\nVersion=2\n");

        using var editor = new ProfileViewModel(profile, new FakeDisplayService(), new RecordingColorControlService());
        Assert.Contains("Alt + Shortcut settings could not be loaded", editor.PersistenceWarning);
        Assert.Contains("Macros could not be loaded", editor.PersistenceWarning);
    }

    [Fact]
    public async Task FirstSave_SplitFile_BacksUpOriginalOnceAndWritesOnlyNewSections()
    {
        var profile = await LoadAsync(
            "[AltMouse]\nEnabled=True\nHoldThreshold=90\nWheelUp=E\n[AltMouseBindings]\nLeft=F|G\n" +
            "[AltKeyboard]\nEnabled=True\nHoldThreshold=90\n[AltKeyboardBindings]\nQ=Z|X\n");
        var original = File.ReadAllBytes(GamePath);
        Assert.False(File.Exists(BackupPath)); // loading never writes

        await _store.SaveProfileAsync(profile, CancellationToken.None);

        Assert.Equal(original, File.ReadAllBytes(BackupPath));
        var saved = IniDocument.Load(GamePath);
        Assert.True(saved.ContainsSection("AltShortcut"));
        Assert.False(saved.ContainsSection("AltMouse") || saved.ContainsSection("AltMouseBindings") ||
            saved.ContainsSection("AltKeyboard") || saved.ContainsSection("AltKeyboardBindings"));

        profile.RapidFire.IsEnabled = true;
        await _store.SaveProfileAsync(profile, CancellationToken.None);
        Assert.Equal(original, File.ReadAllBytes(BackupPath));

        var reloaded = Assert.Single(await _store.LoadProfilesAsync(CancellationToken.None), p => !p.IsWindowsProfile);
        Assert.True(reloaded.AltMouse.IsEnabled);
        Assert.Equal(90, reloaded.AltKeyboard.HoldThresholdMilliseconds);
        Assert.Equal(Key.E, reloaded.AltMouse.WheelUpKey);
        Assert.Equal(Key.X, reloaded.AltKeyboard.Bindings[Key.Q].HoldKey);
    }

    [Fact]
    public async Task Save_NewProfileOrNewFormatFile_CreatesNoBackup()
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        await _store.SaveProfileAsync(profile, CancellationToken.None);
        await _store.SaveProfileAsync(profile, CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "Profiles"), "*.bak"));
    }

    [Fact]
    public async Task FirstSave_BackupCopyFails_FailsSaveAndLeavesIniUnchanged()
    {
        var profile = await LoadAsync("[AltMouse]\nEnabled=True\n[AltMouseBindings]\nLeft=F|G\n");
        var original = File.ReadAllBytes(GamePath);
        Directory.CreateDirectory(BackupPath); // a directory in the way makes File.Copy throw

        await Assert.ThrowsAnyAsync<SystemException>(() => _store.SaveProfileAsync(profile, CancellationToken.None));

        Assert.Equal(original, File.ReadAllBytes(GamePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Save_StateThatWouldNotReadBack_ThrowsAndWritesNothing(int mutation)
    {
        var profile = ProfileFactory.CreateCustomProfile("Game", "game.exe");
        await _store.SaveProfileAsync(profile, CancellationToken.None);
        var original = File.ReadAllBytes(profile.SourcePath);
        switch (mutation)
        {
            case 0: profile.AltMouse.IsEnabled = true; break;
            case 1: profile.AltKeyboard.HoldThresholdMilliseconds = 90; break;
            case 2: profile.AltMouse.Bindings = new() { [(MouseButton)99] = new() { TapKey = Key.F } }; break;
            case 3: profile.AltKeyboard.Bindings = new() { [Key.LeftAlt] = new() { TapKey = Key.F } }; break;
            case 4: profile.AltKeyboard.Bindings = new() { [Key.None] = new() { TapKey = Key.F } }; break;
            case 5: profile.AltKeyboard.Bindings = new() { [Key.System] = new() { TapKey = Key.F } }; break;
            case 6: profile.AltKeyboard.Bindings = new() { [Key.Q] = new() { HoldKey = Key.System } }; break;
            case 7: profile.AltMouse.Bindings = new() { [MouseButton.Left] = new() { TapKey = Key.None } }; break;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.SaveProfileAsync(profile, CancellationToken.None));

        Assert.Equal(original, File.ReadAllBytes(profile.SourcePath));
    }

    // A converted split profile and its new-format reload drive identical input for the same fake-clock script.
    [Fact]
    public async Task SplitLoadAndNewFormatReload_ProduceSameRuntimeOutput()
    {
        var split = await LoadAsync(
            "[AltMouse]\nEnabled=True\nHoldThreshold=120\nWheelUp=E\nWheelDown=F\n[AltMouseBindings]\nLeft=C|D\n" +
            "[AltKeyboard]\nEnabled=True\nHoldThreshold=120\n[AltKeyboardBindings]\nQ=A|B\n");
        var splitOutput = AltShortcutRuntimeTests.RunScriptedAltSequence(split);

        await _store.SaveProfileAsync(split, CancellationToken.None);
        var reloaded = Assert.Single(await _store.LoadProfilesAsync(CancellationToken.None), p => !p.IsWindowsProfile);
        Assert.True(IniDocument.Load(GamePath).ContainsSection("AltShortcut"));

        Assert.Contains("sent B down", splitOutput);
        Assert.Contains("sent F up", splitOutput);
        Assert.Equal(splitOutput, AltShortcutRuntimeTests.RunScriptedAltSequence(reloaded));
    }

    private async Task<Profile> LoadAsync(string altSections)
    {
        File.WriteAllText(GamePath, "[Profile]\nName=Game\nExecutable=game.exe\n" + altSections);
        return Assert.Single(await _store.LoadProfilesAsync(CancellationToken.None), p => !p.IsWindowsProfile);
    }
}
