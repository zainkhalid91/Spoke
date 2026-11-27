using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Spoke.Core;

// ───────────────────────── settings.json, as written by people ─────────────────────────

/// <summary>%APPDATA%\Spoke\settings.json. Hand-editable; every field is optional.</summary>
public sealed class SpokeConfig
{
    public int Version { get; set; } = 1;
    /// <summary>e.g. "Ctrl+Alt+Space". Null: the first free one of Spoke's defaults.</summary>
    public string? Hotkey { get; set; }
    public string? Theme { get; set; }
    /// <summary>Wheels. M1 uses the first one; more wheels and the wheel row arrive in M3.</summary>
    public List<WheelConfig>? Wheels { get; set; }
}

public sealed class WheelConfig
{
    public string? Name { get; set; }
    public List<SliceConfig>? Slices { get; set; }
}

public sealed class SliceConfig
{
    /// <summary>tool | app | url | folder | search | text</summary>
    public string? Type { get; set; }
    public string? Label { get; set; }
    /// <summary>Second line in the hub. Derived from the action when left out.</summary>
    public string? Detail { get; set; }
    /// <summary>A glyph name (see <see cref="Glyphs"/>) or a code point like "E774". App slices use the app's own icon.</summary>
    public string? Icon { get; set; }

    /// <summary>tool: screenshot | colorPicker | clips</summary>
    public string? Tool { get; set; }
    /// <summary>app: exe name or path, .lnk, or shell:AppsFolder\&lt;AUMID&gt;. url: the address. folder: the path.</summary>
    public string? Target { get; set; }
    /// <summary>app: command-line arguments.</summary>
    public string? Args { get; set; }
    /// <summary>app: working folder.</summary>
    public string? WorkDir { get; set; }
    /// <summary>url: open in this browser (exe name or path) instead of the default one.</summary>
    public string? Browser { get; set; }
    /// <summary>folder: explorer (default) | terminal | copyPath</summary>
    public string? Open { get; set; }
    /// <summary>search: google | duckduckgo | bing | youtube | wikipedia | github, or a URL with {query}.</summary>
    public string? Engine { get; set; }
    /// <summary>text: what to type. Tokens: {date} {time} {iso} {unix} {uuid} {clipboard} {date:format}.</summary>
    public string? Text { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true)]
[JsonSerializable(typeof(SpokeConfig))]
internal sealed partial class ConfigJson : JsonSerializerContext;

// ───────────────────────── what the daemon runs ─────────────────────────

public enum SliceKind { Tool, App, Url, Folder, Search, Text }
public enum ToolKind { None, Screenshot, ColorPicker, Clips }
public enum FolderOpen { Explorer, Terminal, CopyPath }

/// <summary>A validated slice with every default filled in. Paths still hold %ENV% variables (expanded at run time).</summary>
public sealed record Slice(
    SliceKind Kind,
    string Label,
    string Detail,
    string? Icon,
    ToolKind Tool = ToolKind.None,
    string Target = "",
    string Args = "",
    string WorkDir = "",
    string Browser = "",
    FolderOpen Open = FolderOpen.Explorer,
    string SearchTemplate = "",
    string Text = "");

public sealed record ConfigResult(
    SpokeConfig? Config,
    string WheelName,
    IReadOnlyList<Slice> Slices,
    IReadOnlyList<string> Errors,
    bool UsedDefaultWheel)
{
    public bool Ok => Errors.Count == 0;
}

public static class ConfigLoader
{
    public const int MinSlices = 2, MaxSlices = 12;

    /// <summary>The wheel a fresh install gets: every kind of action once.</summary>
    public static WheelConfig DefaultWheel() => new()
    {
        Name = "Spoke",
        Slices =
        [
            new() { Type = "tool", Tool = "screenshot" },
            new() { Type = "tool", Tool = "colorPicker" },
            new() { Type = "tool", Tool = "clips" },
            new() { Type = "app", Label = "Terminal", Target = "wt.exe", Detail = "Windows Terminal" },
            new() { Type = "folder", Label = "Downloads", Target = "%USERPROFILE%\\Downloads" },
            new() { Type = "search", Label = "Search", Engine = "google" },
            new() { Type = "url", Label = "GitHub", Target = "https://github.com" },
            new() { Type = "text", Label = "Today", Text = "{date}", Detail = "Types today's date" },
        ],
    };

    public static SpokeConfig Default() => new() { Wheels = [DefaultWheel()] };

    // Hand-edited file: write apostrophes, accents and the like as themselves, not as ' escapes.
    private static readonly JsonSerializerOptions WriteOptions = new(ConfigJson.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(SpokeConfig c) =>
        JsonSerializer.Serialize(c, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<SpokeConfig>)WriteOptions.GetTypeInfo(typeof(SpokeConfig))) + Environment.NewLine;

    /// <summary>Parses and validates. Errors are short sentences a person can act on; on any error the caller keeps its last good config.</summary>
    public static ConfigResult Parse(string json)
    {
        SpokeConfig? c;
        try
        {
            c = string.IsNullOrWhiteSpace(json) ? new SpokeConfig() : JsonSerializer.Deserialize(json, ConfigJson.Default.SpokeConfig);
        }
        catch (JsonException ex)
        {
            // Built without .NET's resource strings, so the exception text isn't readable: say where, and what to look for.
            string where = ex.LineNumber is long l ? $" near line {l + 1}" + (ex.BytePositionInLine is long col ? $", column {col + 1}" : "") : "";
            return new(null, "", [], [$"settings.json isn't valid JSON{where}. Look for a missing comma, quote or bracket."], false);
        }
        c ??= new SpokeConfig();

        var errors = new List<string>();
        if (c.Hotkey is { } hk && !Spoke.Core.Hotkey.TryParse(hk, out _))
            errors.Add($"\"hotkey\": \"{hk}\" is not a shortcut Spoke understands (try \"Ctrl+Alt+Space\").");

        bool usedDefault = c.Wheels is not { Count: > 0 } || c.Wheels[0].Slices is null;
        var wheel = usedDefault ? DefaultWheel() : c.Wheels![0];
        var list = wheel.Slices!;
        if (list.Count is < MinSlices or > MaxSlices)
            errors.Add($"A wheel needs {MinSlices}–{MaxSlices} slices; this one has {list.Count}.");

        var slices = new List<Slice>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var s = Resolve(list[i], out string? error);
            if (error != null) errors.Add($"Slice {i + 1}{(list[i]?.Label is { Length: > 0 } lb ? $" ({lb})" : "")}: {error}");
            else slices.Add(s!);
        }
        string name = string.IsNullOrWhiteSpace(wheel.Name) ? "Spoke" : wheel.Name.Trim();
        return new(c, name, slices, errors, usedDefault);
    }

    public static Slice? Resolve(SliceConfig? s, out string? error)
    {
        error = null;
        if (s == null) { error = "is empty."; return null; }
        string type = (s.Type ?? "").Trim().ToLowerInvariant();
        string? label = Trimmed(s.Label), detail = Trimmed(s.Detail), icon = Trimmed(s.Icon);
        string target = Trimmed(s.Target) ?? "";

        switch (type)
        {
            case "tool":
            {
                var tool = (s.Tool ?? "").Replace("-", "").Replace(" ", "").ToLowerInvariant() switch
                {
                    "screenshot" => ToolKind.Screenshot,
                    "colorpicker" or "colourpicker" => ToolKind.ColorPicker,
                    "clips" => ToolKind.Clips,
                    _ => ToolKind.None,
                };
                if (tool == ToolKind.None) { error = "\"tool\" must be screenshot, colorPicker or clips."; return null; }
                var (l, d) = tool switch
                {
                    ToolKind.Screenshot => ("Screenshot", "Region, window, screen"),
                    ToolKind.ColorPicker => ("Color picker", "Copy any pixel's colour"),
                    _ => ("Clips", "Your copied images"),
                };
                return new(SliceKind.Tool, label ?? l, detail ?? d, icon, tool);
            }

            case "app":
                if (target.Length == 0) { error = "an app slice needs a \"target\" (an exe name or path, a shortcut, or shell:AppsFolder\\<AppID>)."; return null; }
                return new(SliceKind.App, label ?? AppName(target), detail ?? $"App · {ShortTarget(target)}", icon,
                    Target: target, Args: Trimmed(s.Args) ?? "", WorkDir: Trimmed(s.WorkDir) ?? "");

            case "url" or "website" or "web" or "link":
            {
                if (target.Length == 0) { error = "a website slice needs a \"target\" address."; return null; }
                string url = NormalizeUrl(target);
                if (url.Length == 0) { error = $"\"{target}\" is not a web address."; return null; }
                return new(SliceKind.Url, label ?? HostOf(url), detail ?? HostOf(url), icon, Target: url, Browser: Trimmed(s.Browser) ?? "");
            }

            case "folder":
            {
                if (target.Length == 0) { error = "a folder slice needs a \"target\" path."; return null; }
                var open = (s.Open ?? "explorer").Replace("-", "").Replace(" ", "").ToLowerInvariant() switch
                {
                    "explorer" or "open" or "" => FolderOpen.Explorer,
                    "terminal" => FolderOpen.Terminal,
                    "copypath" or "copy" => FolderOpen.CopyPath,
                    _ => (FolderOpen)(-1),
                };
                if (open < 0) { error = "\"open\" must be explorer, terminal or copyPath."; return null; }
                string folder = FolderName(target);
                string d = open switch { FolderOpen.Terminal => $"Terminal in {folder}", FolderOpen.CopyPath => $"Copy path of {folder}", _ => $"Folder · {folder}" };
                return new(SliceKind.Folder, label ?? folder, detail ?? d, icon, Target: target, Open: open);
            }

            case "search":
            {
                if (!SearchEngines.TryResolve(s.Engine, out var template, out var engineName))
                {
                    error = $"\"engine\" must be one of {string.Join(", ", SearchEngines.Names)}, or a web address containing {{query}}.";
                    return null;
                }
                return new(SliceKind.Search, label ?? "Search", detail ?? $"Search {engineName}", icon, SearchTemplate: template);
            }

            case "text" or "snippet":
            {
                if (string.IsNullOrEmpty(s.Text)) { error = "a text slice needs some \"text\" to type."; return null; }
                return new(SliceKind.Text, label ?? "Snippet", detail ?? Preview(s.Text), icon, Text: s.Text);
            }

            case "":
                error = "\"type\" is missing (tool, app, url, folder, search or text).";
                return null;
            default:
                error = $"\"type\": \"{s.Type}\" is unknown (use tool, app, url, folder, search or text).";
                return null;
        }
    }

    /// <summary>Sets one top-level value in the user's file without touching the rest (comments are not kept).</summary>
    public static string WithValue(string json, string key, string value)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return json;   // never overwrite a file the user is in the middle of fixing
        }
        // Match an existing key case-insensitively, so "Theme" isn't duplicated as "theme".
        string name = root.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
        root[name] = value;
        return root.ToJsonString(WriteOptions) + Environment.NewLine;
    }

    /// <summary>Adds the default wheel to a file that has none (e.g. one holding only a theme), keeping the other values.</summary>
    public static string WithDefaultWheel(string json)
    {
        var parsed = Parse(json);
        if (parsed.Config is not { } c) return json;
        c.Wheels = [DefaultWheel()];
        return Serialize(c);
    }

    // ───────────────────────── helpers ─────────────────────────

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static string NormalizeUrl(string t)
    {
        if (t.Contains("://", StringComparison.Ordinal) || t.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(t, UriKind.Absolute, out _) ? t : "";
        // "github.com" or "github.com/foo" → https://
        return t.Contains('.') && !t.Contains(' ') && Uri.TryCreate("https://" + t, UriKind.Absolute, out _) ? "https://" + t : "";
    }

    internal static string HostOf(string url)
    {
        if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return url[7..];
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host)) return url;
        return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
    }

    private static string ShortTarget(string t)
    {
        const string apps = "shell:AppsFolder\\";
        if (t.StartsWith(apps, StringComparison.OrdinalIgnoreCase)) return "Store app";
        return Path.GetFileName(t.TrimEnd('\\', '/')) is { Length: > 0 } f ? f : t;
    }

    private static string AppName(string t)
    {
        string f = ShortTarget(t);
        if (f == "Store app") return "App";
        string n = Path.GetFileNameWithoutExtension(f);
        return n.Length == 0 ? f : char.ToUpperInvariant(n[0]) + n[1..];
    }

    private static string FolderName(string t)
    {
        string trimmed = t.TrimEnd('\\', '/');
        string n = Path.GetFileName(trimmed);
        if (n.Length == 0) return trimmed.Length > 0 ? trimmed : t;              // "C:\" → "C:"
        if (n.StartsWith('%') && n.EndsWith('%')) return n.Trim('%');            // "%USERPROFILE%" → "USERPROFILE"
        return n;
    }

    private static string Preview(string text)
    {
        string one = text.ReplaceLineEndings(" ").Trim();
        return one.Length <= 26 ? one : one[..25] + "…";
    }
}

/// <summary>Named Segoe Fluent Icons glyphs a slice can use as "icon".</summary>
public static class Glyphs
{
    public static readonly IReadOnlyDictionary<string, char> Named = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase)
    {
        ["app"] = '\uE71D', ["apps"] = '\uE71D', ["terminal"] = '\uE756', ["folder"] = '\uE8B7', ["globe"] = '\uE774',
        ["web"] = '\uE774', ["search"] = '\uE721', ["text"] = '\uE8D2', ["note"] = '\uE70B', ["settings"] = '\uE713',
        ["code"] = '\uE943', ["mail"] = '\uE715', ["calendar"] = '\uE787', ["music"] = '\uE8D6', ["camera"] = '\uE722',
        ["home"] = '\uE80F', ["download"] = '\uE896', ["copy"] = '\uE8C8', ["star"] = '\uE734', ["chat"] = '\uE8BD',
        ["page"] = '\uE7C3', ["screenshot"] = '\uF406', ["color"] = '\uEF3C', ["image"] = '\uE8B9', ["clock"] = '\uE823',
        ["link"] = '\uE71B', ["lock"] = '\uE72E', ["game"] = '\uE7FC', ["video"] = '\uE714', ["cloud"] = '\uE753',
    };

    /// <summary>The glyph for a slice: its "icon" if valid, else one for its kind.</summary>
    public static char For(Slice s)
    {
        if (s.Icon is { } name)
        {
            if (Named.TryGetValue(name, out char g)) return g;
            string hex = name.StartsWith("U+", StringComparison.OrdinalIgnoreCase) ? name[2..] : name;
            if (hex.Length is 4 or 5 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int cp) && cp is >= 0xE000 and <= 0xF8FF)
                return (char)cp;
        }
        return s.Kind switch
        {
            SliceKind.Tool => s.Tool switch { ToolKind.Screenshot => '\uF406', ToolKind.ColorPicker => '\uEF3C', _ => '\uE8B9' },
            SliceKind.App => '\uE71D',
            SliceKind.Url => '\uE774',
            SliceKind.Folder => s.Open == FolderOpen.Terminal ? '\uE756' : s.Open == FolderOpen.CopyPath ? '\uE8C8' : '\uE8B7',
            SliceKind.Search => '\uE721',
            _ => '\uE8D2',
        };
    }
}
