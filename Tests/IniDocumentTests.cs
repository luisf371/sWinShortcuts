using System.IO;
using sWinShortcuts.Models;
using sWinShortcuts.Utilities;
using Xunit;

namespace Tests;

public sealed class IniDocumentTests : IDisposable
{
    private readonly string _path = Path.GetTempFileName();

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Load_ExplicitlyEmptySourceValue_RetainsStrictPresenceAndLegacyReaderSemantics()
    {
        File.WriteAllText(_path, "[Macros]\nEnabled=\nCount=   \n[Empty]\n[Legacy]\nValue=42\nValue=\nLabel=\tText\t\n");

        var document = IniDocument.Load(_path);

        Assert.True(document.ContainsSection("Macros"));
        Assert.True(document.ContainsSection("empty"));
        Assert.False(document.ContainsSection("Missing"));
        Assert.Contains("Empty", document.SectionNames);
        Assert.True(document.TryGetSourceValue("MACROS", "enabled", out var empty));
        Assert.Equal(string.Empty, empty);
        Assert.False(document.TryGetSourceValue("Macros", "Missing", out _));
        Assert.False(document.TryGetBoolean("Macros", "Enabled", out _));
        Assert.False(document.TryGetInt32("Macros", "Count", out _));
        Assert.Null(document.GetValue("Macros", "Enabled"));
        Assert.True(document.GetBoolean("Macros", "Enabled", true));
        Assert.Equal(17, document.GetInt32("Macros", "Count", 17));
        Assert.Empty(document.GetSection("Macros"));
        Assert.Null(document.GetValue("Legacy", "Value"));
        Assert.Equal("Text", document.GetValue("Legacy", "Label"));
        Assert.True(document.TryGetSourceValue("Legacy", "Label", out var label));
        Assert.Equal("\tText\t", label);
        document.SetValue("Legacy", "Value", "new");
        Assert.Equal("new", document.GetValue("Legacy", "Value"));
        document.SetValue("Legacy", "Value", "");
        Assert.Null(document.GetValue("Legacy", "Value"));
    }

    [Fact]
    public void TryGetTypedValue_InvalidOrEmptySources_ReturnsFalseWithoutLegacyFallback()
    {
        File.WriteAllText(_path, "[Macro]\nInteger=1.0\nOverflow=2147483648\nBool=invalid\nKey=System\nEnum=999\nNone=None\nValid=-12\n");
        var document = IniDocument.Load(_path);

        Assert.False(document.TryGetInt32("Macro", "Integer", out _));
        Assert.False(document.TryGetInt32("Macro", "Overflow", out _));
        Assert.False(document.TryGetBoolean("Macro", "Bool", out _));
        Assert.False(document.TryGetKey("Macro", "Key", out _));
        Assert.False(document.TryGetEnum<MacroStepKind>("Macro", "Enum", out _));
        Assert.True(document.TryGetKey("Macro", "None", out var key));
        Assert.Equal(System.Windows.Input.Key.None, key);
        Assert.True(document.TryGetInt32("Macro", "Valid", out var number));
        Assert.Equal(-12, number);
    }
}
