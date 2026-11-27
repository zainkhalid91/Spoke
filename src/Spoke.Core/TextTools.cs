using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Spoke.Core;

/// <summary>The text tools wheel: pure string transforms (Spoke copies the selection, applies one, pastes it back).</summary>
public static class TextTools
{
    public enum Op { Upper, Lower, Title, Sentence, CleanSpaces, PrettyJson, Base64Encode, Base64Decode, UrlEncode, UrlDecode, Count }

    public static readonly (Op Op, string Label, string Detail, char Glyph)[] All =
    [
        (Op.Upper, "UPPERCASE", "MAKE IT LOUD", ''),
        (Op.Lower, "lowercase", "make it quiet", ''),
        (Op.Title, "Title Case", "Capitalise Each Word", ''),
        (Op.Sentence, "Sentence case", "First letter of each sentence", ''),
        (Op.CleanSpaces, "Clean spaces", "Trim, single spaces, no blank runs", ''),
        (Op.PrettyJson, "Pretty JSON", "Indent (or minify with Shift)", ''),
        (Op.Base64Encode, "Base64 encode", "UTF-8 → Base64", ''),
        (Op.Base64Decode, "Base64 decode", "Base64 → UTF-8 text", ''),
        (Op.UrlEncode, "URL encode", "Escape for a web address", ''),
        (Op.UrlDecode, "URL decode", "%20 → space", ''),
        (Op.Count, "Count", "Words, characters, lines", ''),
    ];

    /// <summary>Applies an op. Returns null and an error sentence when the input doesn't fit (e.g. invalid Base64).</summary>
    public static string? Apply(Op op, string text, out string? error, bool alternate = false)
    {
        error = null;
        switch (op)
        {
            case Op.Upper: return text.ToUpperInvariant();
            case Op.Lower: return text.ToLowerInvariant();
            case Op.Title: return TitleCase(text);
            case Op.Sentence: return SentenceCase(text);
            case Op.CleanSpaces: return CleanSpaces(text);
            case Op.PrettyJson:
                try
                {
                    using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    using var ms = new MemoryStream();
                    using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = !alternate, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                        doc.WriteTo(w);
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
                catch (JsonException ex)
                {
                    error = ex.LineNumber is long l ? $"That isn't valid JSON (line {l + 1})." : "That isn't valid JSON.";
                    return null;
                }
            case Op.Base64Encode: return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            case Op.Base64Decode:
            {
                string b = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).Replace('-', '+').Replace('_', '/');
                b = b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=');
                try
                {
                    byte[] bytes = Convert.FromBase64String(b);
                    var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
                    return utf8.GetString(bytes);
                }
                catch (FormatException) { error = "That isn't Base64 text."; return null; }
                catch (DecoderFallbackException) { error = "It decodes to binary data, not text."; return null; }
            }
            case Op.UrlEncode: return Uri.EscapeDataString(text);
            case Op.UrlDecode:
                try { return Uri.UnescapeDataString(text.Replace('+', ' ')); }
                catch (UriFormatException) { error = "That isn't URL-encoded text."; return null; }
            case Op.Count: return text;   // nothing to change; see Summary
            default: return text;
        }
    }

    /// <summary>"12 words · 68 characters · 3 lines".</summary>
    public static string Summary(string text)
    {
        int words = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            bool w = !char.IsWhiteSpace(c);
            if (w && !inWord) words++;
            inWord = w;
        }
        var si = new StringInfo(text.ReplaceLineEndings("\n"));
        int lines = text.Length == 0 ? 0 : text.ReplaceLineEndings("\n").Count(c => c == '\n') + 1;
        return $"{words} {(words == 1 ? "word" : "words")} · {si.LengthInTextElements} characters · {lines} {(lines == 1 ? "line" : "lines")}";
    }

    private static readonly HashSet<string> Small = new(StringComparer.OrdinalIgnoreCase)
        { "a", "an", "and", "as", "at", "but", "by", "for", "in", "nor", "of", "on", "or", "the", "to", "via", "vs" };

    public static string TitleCase(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0, wordIndex = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i])) { sb.Append(text[i++]); continue; }
            int start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '\'' or '’')) i++;
            string word = text[start..i];
            bool afterBreak = wordIndex == 0 || IsBreakBefore(text, start);
            if (!afterBreak && Small.Contains(word)) sb.Append(word.ToLowerInvariant());
            else if (word.Length > 1 && word.Skip(1).Any(char.IsUpper) && word.Any(char.IsLower)) sb.Append(word);   // iPhone, McDonald
            else sb.Append(char.ToUpperInvariant(word[0])).Append(word[1..].ToLowerInvariant());
            wordIndex++;
        }
        return sb.ToString();
    }

    private static bool IsBreakBefore(string text, int start)
    {
        for (int j = start - 1; j >= 0; j--)
        {
            char c = text[j];
            if (c is '\n' or ':' or '.' or '!' or '?' or '—') return true;
            if (c == '-' && j > 0 && text[j - 1] == ' ') return true;   // "Word - the rest", not "well-known"
            if (!char.IsWhiteSpace(c) && c is not '"' and not '(' and not '“' and not '\'') return false;
        }
        return true;
    }

    public static string SentenceCase(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool start = true;
        foreach (char c in text)
        {
            if (char.IsLetter(c))
            {
                sb.Append(start ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                start = false;
            }
            else
            {
                sb.Append(c);
                if (c is '.' or '!' or '?' or '\n') start = true;
            }
        }
        // Keep "I" on its own capitalised.
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\bi\b", "I");
    }

    /// <summary>Trims each line, collapses runs of spaces/tabs, and allows at most one blank line in a row.</summary>
    public static string CleanSpaces(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l.Replace(' ', ' '), @"[ \t]+", " ").Trim())
            .ToList();
        var outLines = new List<string>(lines.Count);
        foreach (var l in lines)
        {
            if (l.Length == 0 && (outLines.Count == 0 || outLines[^1].Length == 0)) continue;
            outLines.Add(l);
        }
        while (outLines.Count > 0 && outLines[^1].Length == 0) outLines.RemoveAt(outLines.Count - 1);
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        return string.Join(nl, outLines);
    }

    /// <summary>A strong random password: letters, digits and symbols, at least one of each class, no look-alikes or braces.</summary>
    public static string Password(int length = 20)
    {
        const string lower = "abcdefghijkmnpqrstuvwxyz", upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", digits = "23456789", symbols = "!@#$%^&*-_=+?";
        string all = lower + upper + digits + symbols;
        length = Math.Clamp(length, 8, 128);
        var chars = new char[length];
        string[] classes = [lower, upper, digits, symbols];
        for (int i = 0; i < length; i++)
        {
            string set = i < classes.Length ? classes[i] : all;
            chars[i] = set[System.Security.Cryptography.RandomNumberGenerator.GetInt32(set.Length)];
        }
        // Shuffle so the guaranteed classes aren't always first.
        for (int i = length - 1; i > 0; i--)
        {
            int j = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }

    public const string Lorem =
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. " +
        "Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat.";
}
