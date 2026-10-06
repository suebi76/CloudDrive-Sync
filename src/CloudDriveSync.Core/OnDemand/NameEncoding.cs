using System.Text;

namespace CloudDriveSync.Core.OnDemand;

/// <summary>
/// File names on the PC exactly as rclone writes them on Windows. Servers allow names Windows does not ("Was?.docx",
/// "a:b", a name ending with a space or a dot). rclone's local backend replaces such characters with look-alikes
/// ("Was？.docx", "a：b") and marks look-alikes that were in the name already with a quote character, so every name can
/// be turned back. rclone itself speaks of names in its "standard" encoding (that is what operations/list returns).
/// CloudDrive-Sync creates the placeholders itself, so it must name them the same way - otherwise a folder that bisync
/// filled would not match after switching it to "files on demand". This is a port of rclone's lib/encoder (v1.75.1)
/// for the two encodings involved: Standard and the Windows encoding of the local backend (encoder.OS).
/// Names with bytes that are not valid UTF-8 cannot occur here: they reach CloudDrive-Sync only as text.
/// </summary>
public static class NameEncoding
{
    // rclone's flags, as far as the two encodings use them.
    [Flags]
    private enum Mask
    {
        None = 0,
        Slash = 1 << 0,
        LtGt = 1 << 1,
        DoubleQuote = 1 << 2,
        Colon = 1 << 3,
        Question = 1 << 4,
        Asterisk = 1 << 5,
        Pipe = 1 << 6,
        BackSlash = 1 << 7,
        Del = 1 << 8,
        Ctl = 1 << 9,
        RightSpace = 1 << 10,
        RightPeriod = 1 << 11,
        Dot = 1 << 12,
    }

    /// <summary>rclone's encoder.Standard: NUL, "/", control characters, DEL and the names "." and "..".</summary>
    private const Mask Standard = Mask.Slash | Mask.Ctl | Mask.Del | Mask.Dot;

    /// <summary>rclone's encoder.OS on Windows (Base | Win | BackSlash | Ctl | RightSpace | RightPeriod | InvalidUtf8).</summary>
    private const Mask Windows = Mask.Slash | Mask.Dot | Mask.Colon | Mask.Question | Mask.DoubleQuote | Mask.Asterisk | Mask.LtGt | Mask.Pipe |
        Mask.BackSlash | Mask.Ctl | Mask.RightSpace | Mask.RightPeriod;

    private const char Quote = '‛'; // SINGLE HIGH-REVERSED-9 QUOTATION MARK
    private const int FullOffset = 0xFEE0; // printable ASCII + this = its FULLWIDTH variant
    private const char SymbolNull = '␀'; // SYMBOL FOR NULL; + 0x01..0x1F = the symbols for control characters
    private const char SymbolSpace = '␠'; // SYMBOL FOR SPACE
    private const char FullStop = '．'; // FULLWIDTH FULL STOP
    private const char SymbolDelete = '␡'; // SYMBOL FOR DELETE

    /// <summary>The name a file or folder of the cloud gets on the PC.</summary>
    public static string ToLocalName(string standardName) => Encode(Windows, Decode(Standard, standardName));

    /// <summary>The name in the cloud (rclone's standard encoding) of a file or folder on the PC.</summary>
    public static string ToStandardName(string localName) => Encode(Standard, Decode(Windows, localName));

    /// <summary>A "/" separated path of the cloud as relative Windows path ("Ordner\Datei.txt").</summary>
    public static string ToLocalPath(string standardPath) =>
        string.Join('\\', standardPath.Split('/').Select(ToLocalName));

    /// <summary>A relative Windows path as "/" separated path of the cloud.</summary>
    public static string ToStandardPath(string localPath) =>
        string.Join('/', localPath.Split('\\', '/').Select(ToStandardName));

    private static bool Has(Mask mask, Mask flag) => (mask & flag) != 0;

    /// <summary>Port of MultiEncoder.Encode.</summary>
    private static string Encode(Mask mask, string text)
    {
        if (text.Length == 0) return "";
        if (Has(mask, Mask.Dot))
        {
            switch (text)
            {
                case ".": return FullStop.ToString();
                case "..": return $"{FullStop}{FullStop}";
                case "．": return $"{Quote}{FullStop}";
                case "．．": return $"{Quote}{FullStop}{Quote}{FullStop}";
            }
        }

        // Only the end of a name: a trailing space or period (Windows drops them).
        var suffix = "";
        if (Has(mask, Mask.RightSpace))
        {
            if (text[^1] == ' ') (suffix, text) = (SymbolSpace.ToString(), text[..^1]);
            else if (text[^1] == SymbolSpace) (suffix, text) = ($"{Quote}{SymbolSpace}", text[..^1]);
        }
        if (Has(mask, Mask.RightPeriod) && suffix.Length == 0 && text.Length > 0)
        {
            if (text[^1] == '.') (suffix, text) = (FullStop.ToString(), text[..^1]);
            else if (text[^1] == FullStop) (suffix, text) = ($"{Quote}{FullStop}", text[..^1]);
        }

        var output = new StringBuilder(text.Length + suffix.Length + 4);
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value == 0)
            {
                output.Append(SymbolNull);
                continue;
            }
            if (value == SymbolNull || value == Quote)
            {
                output.Append(Quote).Append((char)value);
                continue;
            }
            if (EncodedForm(mask, value) is { } replacement)
            {
                output.Append(replacement);
                continue;
            }
            if (IsReplacement(mask, value))
            {
                // A look-alike that was in the name already: quoted, so it is not taken for a replacement later.
                output.Append(Quote).Append((char)value);
                continue;
            }
            output.Append(rune.ToString());
        }
        return output.Append(suffix).ToString();
    }

    /// <summary>Port of MultiEncoder.Decode.</summary>
    private static string Decode(Mask mask, string text)
    {
        if (Has(mask, Mask.Dot))
        {
            switch (text)
            {
                case "．": return ".";
                case "．．": return "..";
                case "‛．": return "．";
                case "‛．‛．": return "．．";
            }
        }

        var suffix = "";
        if (text.Length > 0 && Has(mask, Mask.RightSpace) && text[^1] == SymbolSpace)
        {
            text = text[..^1];
            if (text.Length > 0 && text[^1] == Quote) (suffix, text) = (SymbolSpace.ToString(), text[..^1]);
            else suffix = " ";
        }
        else if (text.Length > 0 && Has(mask, Mask.RightPeriod) && text[^1] == FullStop)
        {
            text = text[..^1];
            if (text.Length > 0 && text[^1] == Quote) (suffix, text) = (FullStop.ToString(), text[..^1]);
            else suffix = ".";
        }

        var output = new StringBuilder(text.Length + 1);
        var unquote = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            var quoted = unquote;
            unquote = false;
            if (value == SymbolNull)
            {
                output.Append(quoted ? SymbolNull : '\0');
                continue;
            }
            if (value == Quote)
            {
                if (quoted) output.Append(Quote);
                else unquote = true;
                continue;
            }
            if (IsReplacement(mask, value))
            {
                output.Append(quoted ? (char)value : OriginalOf(value));
                continue;
            }
            // A quote character that quoted nothing stays as it was.
            if (quoted) output.Append(Quote);
            output.Append(rune.ToString());
        }
        if (unquote) output.Append(Quote);
        return output.Append(suffix).ToString();
    }

    /// <summary>The look-alike for a character the encoding replaces, or null.</summary>
    private static string? EncodedForm(Mask mask, int value) => value switch
    {
        '*' when Has(mask, Mask.Asterisk) => Fullwidth(value),
        '<' or '>' when Has(mask, Mask.LtGt) => Fullwidth(value),
        '?' when Has(mask, Mask.Question) => Fullwidth(value),
        ':' when Has(mask, Mask.Colon) => Fullwidth(value),
        '|' when Has(mask, Mask.Pipe) => Fullwidth(value),
        '"' when Has(mask, Mask.DoubleQuote) => Fullwidth(value),
        '/' when Has(mask, Mask.Slash) => Fullwidth(value),
        '\\' when Has(mask, Mask.BackSlash) => Fullwidth(value),
        0x7F when Has(mask, Mask.Del) => SymbolDelete.ToString(),
        >= 0x01 and <= 0x1F when Has(mask, Mask.Ctl) => ((char)(SymbolNull + value)).ToString(),
        _ => null,
    };

    /// <summary>Whether a character is a look-alike this encoding uses as a replacement.</summary>
    private static bool IsReplacement(Mask mask, int value) => value switch
    {
        '＊' => Has(mask, Mask.Asterisk),
        '＜' or '＞' => Has(mask, Mask.LtGt),
        '？' => Has(mask, Mask.Question),
        '：' => Has(mask, Mask.Colon),
        '｜' => Has(mask, Mask.Pipe),
        '＂' => Has(mask, Mask.DoubleQuote),
        '／' => Has(mask, Mask.Slash),
        '＼' => Has(mask, Mask.BackSlash),
        SymbolDelete => Has(mask, Mask.Del),
        > SymbolNull and <= SymbolNull + 0x1F => Has(mask, Mask.Ctl),
        _ => false,
    };

    private static char OriginalOf(int replacement) => replacement switch
    {
        SymbolDelete => (char)0x7F,
        > SymbolNull and <= SymbolNull + 0x1F => (char)(replacement - SymbolNull),
        _ => (char)(replacement - FullOffset),
    };

    private static string Fullwidth(int value) => ((char)(value + FullOffset)).ToString();
}
