using System.Text;

namespace Spoke.Core;

/// <summary>
/// Text-slice tokens: {date} {time} {iso} {unix} {uuid} {clipboard}, plus {date:FORMAT} with a .NET date
/// format (e.g. {date:dd/MM/yyyy}). "{{" and "}}" give literal braces. Unknown tokens are left as typed.
/// Formats are culture-invariant (the daemon runs with invariant globalization), so {date} is ISO-style.
/// </summary>
public static class Tokens
{
    public static bool UsesClipboard(string text) => text.Contains("{clipboard}", StringComparison.OrdinalIgnoreCase);

    public static string Expand(string text, DateTimeOffset now, Func<string?>? clipboard = null, Func<Guid>? newGuid = null)
    {
        var sb = new StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{') { sb.Append('{'); i++; continue; }
            if (c == '}' && i + 1 < text.Length && text[i + 1] == '}') { sb.Append('}'); i++; continue; }
            if (c != '{') { sb.Append(c); continue; }

            int end = text.IndexOf('}', i + 1);
            if (end < 0) { sb.Append(text, i, text.Length - i); break; }
            string token = text.Substring(i + 1, end - i - 1);
            string? value = Value(token, now, clipboard, newGuid);
            if (value == null) sb.Append(text, i, end - i + 1);   // not a token: keep "{...}" as written
            else sb.Append(value);
            i = end;
        }
        return sb.ToString();
    }

    private static string? Value(string token, DateTimeOffset now, Func<string?>? clipboard, Func<Guid>? newGuid)
    {
        string name = token, format = "";
        int colon = token.IndexOf(':');
        if (colon > 0) { name = token[..colon]; format = token[(colon + 1)..]; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            return name.ToLowerInvariant() switch
            {
                "date" => now.ToString(format.Length > 0 ? format : "yyyy-MM-dd", inv),
                "time" => now.ToString(format.Length > 0 ? format : "HH:mm", inv),
                "iso" when format.Length == 0 => now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", inv),
                "unix" when format.Length == 0 => now.ToUnixTimeSeconds().ToString(inv),
                "uuid" or "guid" when format.Length == 0 => (newGuid?.Invoke() ?? Guid.NewGuid()).ToString("D"),
                "clipboard" when format.Length == 0 => clipboard?.Invoke() ?? "",
                _ => null,
            };
        }
        catch (FormatException)
        {
            return null;   // a bad custom format stays visible as written, so the user can spot it
        }
    }
}

public static class SearchEngines
{
    private static readonly (string Key, string Name, string Template)[] Known =
    [
        ("google", "Google", "https://www.google.com/search?q={query}"),
        ("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={query}"),
        ("bing", "Bing", "https://www.bing.com/search?q={query}"),
        ("youtube", "YouTube", "https://www.youtube.com/results?search_query={query}"),
        ("wikipedia", "Wikipedia", "https://en.wikipedia.org/w/index.php?search={query}"),
        ("github", "GitHub", "https://github.com/search?q={query}"),
    ];

    public static IEnumerable<string> Names => Known.Select(k => k.Key);

    /// <summary>A known engine name (default google) or a custom http(s) template containing {query}.</summary>
    public static bool TryResolve(string? engine, out string template, out string name)
    {
        string e = string.IsNullOrWhiteSpace(engine) ? "google" : engine.Trim();
        foreach (var k in Known)
        {
            if (!string.Equals(k.Key, e, StringComparison.OrdinalIgnoreCase)) continue;
            template = k.Template;
            name = k.Name;
            return true;
        }
        if (e.Contains("{query}", StringComparison.Ordinal) &&
            Uri.TryCreate(e.Replace("{query}", "x"), UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
        {
            template = e;
            name = u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
            return true;
        }
        template = name = "";
        return false;
    }

    /// <summary>The engine's display name for a template (for the search box's placeholder).</summary>
    public static string NameOf(string template)
    {
        foreach (var k in Known) if (k.Template == template) return k.Name;
        return Uri.TryCreate(template.Replace("{query}", "x"), UriKind.Absolute, out var u) ? u.Host : "the web";
    }

    public static string Url(string template, string query) => template.Replace("{query}", Uri.EscapeDataString(query.Trim()));
}
