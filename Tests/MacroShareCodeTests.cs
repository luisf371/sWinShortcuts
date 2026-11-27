using System.Buffers.Text;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Input;
using sWinShortcuts.Configuration;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using Tests.Fakes;
using Xunit;
using AppMouseButton = sWinShortcuts.Models.MouseButton;

namespace Tests;

public sealed class MacroShareCodeTests
{
    private static readonly Rectangle[] Desktop = [new(0, 0, 1920, 1080), new(1920, 0, 2560, 1440)];

    // Each step carries only its own kind's fields, as the profile writer keeps them.
    private static readonly MacroDefinition Full = new()
    {
        Label = "Ammo",
        IsEnabled = true,
        ToggleMode = true,
        StartOnPress = true,
        CancelOnMouseMovement = true,
        ShowPlayingSteps = true,
        ShortcutKey = Key.F6,
        ShortcutModifiers = ModifierKeys.Control | ModifierKeys.Shift,
        ShortcutRightModifiers = ModifierKeys.Shift,
        Steps =
        [
            new() { Kind = MacroStepKind.KeyDown, Key = Key.LeftCtrl },
            new() { Kind = MacroStepKind.KeyPress, Key = Key.C, DurationMs = 30 },
            new() { Kind = MacroStepKind.Wait, DurationMs = 120 },
            new() { Kind = MacroStepKind.KeyUp, Key = Key.LeftCtrl },
            new() { Kind = MacroStepKind.MouseClick, MouseButton = AppMouseButton.XButton1, X = -123, Y = 456, DurationMs = 17 },
            new() { Kind = MacroStepKind.MouseDown, MouseButton = AppMouseButton.Left },
            new() { Kind = MacroStepKind.MoveTo, X = 100, Y = 200, HeldButtons = MacroHeldButtonPolicy.Allow },
            new() { Kind = MacroStepKind.MouseUp, MouseButton = AppMouseButton.Left },
            new() { Kind = MacroStepKind.MouseWheel, WheelDelta = -120, HorizontalWheel = true },
            new() { Kind = MacroStepKind.MoveCursorBy, X = -25, Y = 12 },
            new() { Kind = MacroStepKind.MoveMouseBy, X = 300, Y = -40, HeldButtons = MacroHeldButtonPolicy.Wait },
            new() { Kind = MacroStepKind.SavePosition, Slot = 3 },
            new() { Kind = MacroStepKind.MoveToSaved, Slot = 3 },
            new() { Kind = MacroStepKind.FreezeMouse },
            new() { Kind = MacroStepKind.UnfreezeMouse }
        ]
    };

    [Fact]
    public void ExportThenImport_EveryStepKind_KeepsTheMacro()
    {
        var result = MacroShareCode.Import(MacroShareCode.Export(Full, Desktop), Desktop);

        Assert.Equal(MacroImportError.None, result.Error);
        var macro = result.Macro!;
        // Old Key press and Mouse click steps import as Down, Wait, Up groups.
        Assert.Equal(IniProfileStore.ExpandCompactPresses(Full.Steps), macro.Steps);
        Assert.Equal((Full.Label, Full.ToggleMode, Full.StartOnPress, Full.CancelOnMouseMovement),
            (macro.Label, macro.ToggleMode, macro.StartOnPress, macro.CancelOnMouseMovement));
        Assert.False(result.LayoutDiffers);
        // The click at (-123, 456) is left of the desktop; the code still carries it unchanged.
        Assert.Equal(1, result.OffScreenPositions);
    }

    [Fact]
    public void ExportThenImport_LabelWithALineSeparator_KeepsTheWholeName()
    {
        var macro = Full with { Label = "foo\u2028bar" };

        Assert.Equal("foo\u2028bar", MacroShareCode.Import(MacroShareCode.Export(macro, Desktop), Desktop).Macro!.Label);
    }

    [Fact]
    public void Import_SameMonitorsInAnotherOrder_IsNotADifferentLayout()
    {
        var code = MacroShareCode.Export(Full, Desktop);

        Assert.False(MacroShareCode.Import(code, [Desktop[1], Desktop[0]]).LayoutDiffers);
    }

    [Fact]
    public void Export_LeavesOutIdShortcutAndEnabledState()
    {
        var code = MacroShareCode.Export(Full, Desktop);
        var payload = Decode(code);

        foreach (var key in new[] { "Id=", "Enabled=", "ShortcutKey=", "ShortcutMouseButton=", "ShortcutModifiers=", "ShortcutRightModifiers=", "ShowPlayingSteps=" })
        {
            Assert.DoesNotContain("\n" + key, payload.ReplaceLineEndings("\n"));
        }
        var macro = MacroShareCode.Import(code, Desktop).Macro!;
        Assert.NotEqual(Full.Id, macro.Id);
        Assert.False(macro.IsEnabled);
        Assert.False(macro.ShowPlayingSteps);
        Assert.Equal(InputTrigger.FromKey(Key.None), macro.ShortcutTrigger);
        Assert.Equal((ModifierKeys.None, ModifierKeys.None), (macro.ShortcutModifiers, macro.ShortcutRightModifiers));
    }

    [Fact]
    public void Export_GoldenPayload_KeepsItsFormat()
    {
        var macro = new MacroDefinition
        {
            Label = "Ammo",
            ShortcutKey = Key.F6,
            Steps = [new() { Kind = MacroStepKind.KeyPress, Key = Key.E }, new() { Kind = MacroStepKind.MoveTo, X = 100, Y = 200 }]
        };

        const string expected = """
            [Share]
            Engine=1.0
            Desktop=0,0,1920,1080;1920,0,2560,1440

            [Macro0]
            Label=Ammo
            ToggleMode=False
            StartOnPress=False
            CancelOnMouseMovement=False
            StepCount=2

            [Macro0.Step0]
            Kind=KeyPress
            Key=E
            DurationMs=0

            [Macro0.Step1]
            Kind=MoveTo
            X=100
            Y=200

            """;
        Assert.Equal(expected.ReplaceLineEndings(), Decode(MacroShareCode.Export(macro, Desktop)).ReplaceLineEndings());
    }

    [Fact]
    public void Import_CodeInsideOtherText_IsFound()
    {
        var code = MacroShareCode.Export(Full, Desktop);

        Assert.Equal(MacroImportError.None, MacroShareCode.Import($"Try my macro: {code} thanks", Desktop).Error);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void Import_CodeSplitAcrossLines_Imports(string lineBreak)
    {
        var code = MacroShareCode.Export(Full, Desktop);
        var payload = code[MacroShareCode.Prefix.Length..^MacroShareCode.Suffix.Length];
        var split = MacroShareCode.Prefix + string.Join(lineBreak, payload.Chunk(40).Select(chunk => new string(chunk))) + MacroShareCode.Suffix;

        var result = MacroShareCode.Import(split + lineBreak + "see you", Desktop);

        Assert.Equal(MacroImportError.None, result.Error);
        Assert.Equal(IniProfileStore.ExpandCompactPresses(Full.Steps), result.Macro!.Steps);
    }

    [Fact]
    public void Import_CodeEndingWithAPeriod_Imports()
    {
        Assert.Equal(MacroImportError.None, MacroShareCode.Import(MacroShareCode.Export(Full, Desktop) + ".", Desktop).Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no code here")]
    [InlineData("SWSM")]
    public void Import_NoCode_ReportsNotFound(string text)
    {
        Assert.Equal(MacroImportError.NotFound, MacroShareCode.Import(text, Desktop).Error);
    }

    [Fact]
    public void Import_CutOffCode_ReportsDamaged()
    {
        var code = MacroShareCode.Export(Full, Desktop);

        for (var cut = 1; cut <= 40; cut++)
        {
            Assert.Equal(MacroImportError.Damaged, MacroShareCode.Import(code[..^cut], Desktop).Error);
        }
        Assert.Equal(MacroImportError.Damaged, MacroShareCode.Import(MacroShareCode.Prefix, Desktop).Error);
    }

    [Fact]
    public void Import_TextOverOneMegabyte_ReportsDamaged()
    {
        var code = MacroShareCode.Export(Full, Desktop);

        Assert.Equal(MacroImportError.Damaged, MacroShareCode.Import(code + new string(' ', 1024 * 1024), Desktop).Error);
    }

    [Fact]
    public void Import_HalfAMillionOneCharacterPieces_FinishesQuickly()
    {
        var text = MacroShareCode.Prefix + string.Concat(Enumerable.Repeat("A ", 500_000)) + MacroShareCode.Suffix;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = MacroShareCode.Import(text, Desktop);

        Assert.Equal(MacroImportError.Damaged, result.Error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Import took {clock.Elapsed}.");
    }

    [Fact]
    public void Import_CodeWithoutEndMarker_ReportsDamaged()
    {
        var code = MacroShareCode.Export(Full, Desktop)[..^MacroShareCode.Suffix.Length];

        Assert.Equal(MacroImportError.Damaged, MacroShareCode.Import(code, Desktop).Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Import_TwoCodes_ReportsDamaged(bool truncateFirst)
    {
        var first = MacroShareCode.Export(Full with { Label = "First" }, Desktop);
        var second = MacroShareCode.Export(Full with { Label = "Second" }, Desktop);
        var text = $"{(truncateFirst ? first[..^1] : first)}\nSent from my phone\n{second}";

        Assert.Equal(MacroImportError.Damaged, MacroShareCode.Import(text, Desktop).Error);
    }

    [Fact]
    public void Import_CodeThatExpandsPastTheLimit_ReportsDamaged()
    {
        var code = Encode(new string('A', 4 * 1024 * 1024 + 1));

        Assert.Equal(MacroImportError.Damaged, MacroShareCode.Import(code, Desktop).Error);
    }

    [Fact]
    public void Import_CodeWithTrailingBytesInsideMarkers_ReportsDamaged()
    {
        var code = MacroShareCode.Export(Full, Desktop);
        var payload = Base64Url.DecodeFromChars(code.AsSpan(MacroShareCode.Prefix.Length..^MacroShareCode.Suffix.Length));
        var damaged = MacroShareCode.Prefix + Base64Url.EncodeToString([.. payload, 0]) + MacroShareCode.Suffix;

        Assert.Equal(MacroImportError.Damaged, MacroShareCode.Import(damaged, Desktop).Error);
    }

    [Theory]
    [InlineData("2.0", "NewerVersion")]
    [InlineData("0.9", "Damaged")]
    [InlineData("one", "Damaged")]
    [InlineData("", "Damaged")]
    public void Import_EngineVersion_IsChecked(string engine, string expected)
    {
        Assert.Equal(expected, MacroShareCode.Import(Encode(Payload(engine, "Kind=KeyPress\nKey=E\nDurationMs=0")), Desktop).Error.ToString());
    }

    [Fact]
    public void Import_NewerMinorWithAnUnreadableStep_ReportsNewerMinor()
    {
        Assert.Equal(MacroImportError.NewerMinor, MacroShareCode.Import(Encode(Payload("1.1", "Kind=Teleport")), Desktop).Error);
        Assert.Equal(MacroImportError.Damaged, MacroShareCode.Import(Encode(Payload("1.0", "Kind=Teleport")), Desktop).Error);
    }

    [Fact]
    public void Import_NewerMinorWithAnUnknownOptionalKey_ImportsTheRest()
    {
        var result = MacroShareCode.Import(Encode(Payload("1.1", "Kind=KeyPress\nKey=E\nDurationMs=0\nFuture=1")), Desktop);

        Assert.Equal(MacroImportError.None, result.Error);
        Assert.Equal(MacroPresses.Press(Key.E), result.Macro!.Steps);
    }

    [Fact]
    public void Import_OldCompactSteps_Converts()
    {
        var result = MacroShareCode.Import(Encode(Payload("1.0", "Kind=MouseClick\nMouseButton=Right\nX=10\nY=20\nDurationMs=0")), Desktop);

        Assert.Equal(MacroImportError.None, result.Error);
        Assert.Equal(MacroPresses.Click(AppMouseButton.Right, 10, 20), result.Macro!.Steps);
    }

    [Fact]
    public void Import_InvalidMacro_ReportsDamaged()
    {
        Assert.Equal(MacroImportError.Damaged, MacroShareCode.Import(Encode(Payload("1.0", "Kind=Wait\nDurationMs=-5")), Desktop).Error);
    }

    [Fact]
    public void Import_DifferentLayout_WarnsAndCountsFixedPositionsOffScreen()
    {
        var code = MacroShareCode.Export(Full, Desktop);
        Rectangle[] laptop = [new(0, 0, 1366, 768)];

        var result = MacroShareCode.Import(code, laptop);

        Assert.True(result.LayoutDiffers);
        // MouseClick at (-123, 456) is off; MoveTo (100, 200) is on; offsets and saved moves are not counted.
        Assert.Equal(1, result.OffScreenPositions);
        Assert.NotNull(result.Macro);
    }

    private static string Payload(string engine, string step) => $"""
        [Share]
        Engine={engine}
        Desktop=0,0,1920,1080

        [Macro0]
        Label=Shared
        StepCount=1

        [Macro0.Step0]
        {step}
        """;

    private static string Encode(string payload)
    {
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            brotli.Write(Encoding.UTF8.GetBytes(payload));
        }
        return MacroShareCode.Prefix + Base64Url.EncodeToString(compressed.ToArray()) + MacroShareCode.Suffix;
    }

    private static string Decode(string code)
    {
        using var brotli = new BrotliStream(new MemoryStream(Base64Url.DecodeFromChars(code.AsSpan(MacroShareCode.Prefix.Length..^MacroShareCode.Suffix.Length))),
            CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
