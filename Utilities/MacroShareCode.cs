using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using sWinShortcuts.Configuration;
using sWinShortcuts.Models;

namespace sWinShortcuts.Utilities;

internal enum MacroImportError
{
    None,
    NotFound,
    Damaged,
    NewerVersion,
    NewerMinor
}

// LayoutDiffers: the code was made on a different monitor layout. OffScreenPositions: fixed positions outside
// every current monitor.
internal readonly record struct MacroImportResult(MacroDefinition? Macro, MacroImportError Error, bool LayoutDiffers, int OffScreenPositions);

// A framed Base64Url code containing a Brotli-compressed INI payload, without the macro's Id, On state or shortcut.
internal static class MacroShareCode
{
    // Engine version of the payload. Bump EngineMinor when a code can hold something older builds may skip
    // (a new optional key). Bump EngineMajor, and reset EngineMinor, when older builds would misread a code
    // (a new step kind, or a changed meaning), so they refuse it.
    internal const int EngineMajor = 1, EngineMinor = 0;
    internal const string Prefix = "SWSM:";
    internal const string Suffix = ":74656B6B79:"; // ASCII hex for "tekky".
    private const string ShareSection = "Share";
    private const string MacroSection = "Macro0";
    private const int MaxTextLength = 1024 * 1024;
    private const int MaxPayloadBytes = 4 * 1024 * 1024;

    internal static string Export(MacroDefinition macro, Rectangle[] monitors)
    {
        if (MacroValidation.GetFormatError(macro) is { } error)
        {
            throw new ArgumentException(error, nameof(macro));
        }

        var document = new IniDocument();
        document.SetValue(ShareSection, "Engine", FormattableString.Invariant($"{EngineMajor}.{EngineMinor}"));
        document.SetValue(ShareSection, "Desktop", FormatDesktop(monitors));
        IniProfileStore.WriteMacroDefinition(document, MacroSection, macro, includeIdentity: false);

        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            brotli.Write(Encoding.UTF8.GetBytes(document.ToText()));
        }
        return Prefix + Base64Url.EncodeToString(compressed.ToArray()) + Suffix;
    }

    internal static MacroImportResult Import(string clipboardText, Rectangle[] monitors)
    {
        if (clipboardText.Length > MaxTextLength)
        {
            return Failed(MacroImportError.Damaged);
        }

        var start = clipboardText.IndexOf(Prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return Failed(MacroImportError.NotFound);
        }

        start += Prefix.Length;
        var end = clipboardText.IndexOf(Suffix, start, StringComparison.Ordinal);
        if (end < 0 || clipboardText.IndexOf(Prefix, start, StringComparison.Ordinal) >= 0 ||
            DecodePayload(clipboardText.AsSpan(start, end - start)) is not { } payload)
        {
            return Failed(MacroImportError.Damaged);
        }

        var document = IniDocument.Parse(payload);
        if (!TryParseEngine(document.GetValue(ShareSection, "Engine"), out var major, out var minor) || major < EngineMajor)
        {
            return Failed(MacroImportError.Damaged);
        }
        if (major > EngineMajor)
        {
            return Failed(MacroImportError.NewerVersion);
        }

        MacroDefinition macro;
        try
        {
            var indexedSections = new HashSet<string>(
                document.SectionNames.Where(IniProfileStore.IsIndexedMacroSection), StringComparer.OrdinalIgnoreCase);
            macro = IniProfileStore.ReadMacroDefinition(document, MacroSection, indexedSections, includeIdentity: false);
            if (indexedSections.Count != 0 || MacroValidation.GetFormatError(macro) is not null)
            {
                throw new FormatException("The code holds more than one macro or an invalid one.");
            }
        }
        catch (FormatException)
        {
            return Failed(minor > EngineMinor ? MacroImportError.NewerMinor : MacroImportError.Damaged);
        }

        var offScreen = macro.Steps.Count(step => step.Kind is MacroStepKind.MoveTo or MacroStepKind.MouseClick &&
            !monitors.Any(monitor => monitor.Contains(step.X, step.Y)));
        var layoutDiffers = !string.Equals(document.GetValue(ShareSection, "Desktop"), FormatDesktop(monitors), StringComparison.Ordinal);
        return new MacroImportResult(macro, MacroImportError.None, layoutDiffers, offScreen);
    }

    private static string? DecodePayload(ReadOnlySpan<char> text)
    {
        var code = new StringBuilder();
        foreach (var character in text)
        {
            if (IsBase64UrlChar(character)) code.Append(character);
            else if (!char.IsWhiteSpace(character)) return null;
        }

        if (code.Length == 0) return null;
        var compressed = new byte[Base64Url.GetMaxDecodedLength(code.Length)];
        if (!Base64Url.TryDecodeFromChars(code.ToString(), compressed, out var decoded)) return null;

        var buffer = new byte[MaxPayloadBytes];
        using var decoder = new BrotliDecoder();
        return decoder.Decompress(compressed.AsSpan(0, decoded), buffer, out var consumed, out var written) == OperationStatus.Done && consumed == decoded
            ? Encoding.UTF8.GetString(buffer, 0, written)
            : null;
    }

    private static bool IsBase64UrlChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    private static bool TryParseEngine(string? value, out int major, out int minor)
    {
        major = minor = 0;
        var parts = value?.Split('.');
        return parts is { Length: 2 } &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor);
    }

    // Sorted, because Windows does not promise a stable monitor enumeration order.
    private static string FormatDesktop(Rectangle[] monitors) => string.Join(';',
        monitors.OrderBy(m => m.X).ThenBy(m => m.Y).Select(m => FormattableString.Invariant($"{m.X},{m.Y},{m.Width},{m.Height}")));

    private static MacroImportResult Failed(MacroImportError error) => new(null, error, false, 0);
}
