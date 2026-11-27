namespace Spoke.Core;

public enum AccentMode { Theme, Windows, Custom }

/// <summary>How a slice with more options (Files, Text tools…) shows them.</summary>
public enum SubMenuStyle
{
    /// <summary>The wheel grows an outer band of options around the chosen slice (default).</summary>
    Ring,
    /// <summary>A new wheel replaces the current one (Esc goes back).</summary>
    Wheel,
}

/// <summary>
/// How the wheel looks, resolved from settings.json: which theme (or "Auto" to follow Windows light/dark mode),
/// the slice colours (the theme's own palette, the Windows accent colour, or one custom colour) and the size.
/// </summary>
public sealed record Appearance(
    string Theme,              // a theme name, or "Auto"
    string LightTheme,
    string DarkTheme,
    AccentMode Accent,
    uint AccentRgb,            // 0xRRGGBB, for AccentMode.Custom
    float Scale)
{
    public const string Auto = "Auto";
    public static readonly string[] ThemeNames = ["Aurora", "Ember", "Paper", "Graphite", "Onyx"];
    public static readonly (string Name, float Scale)[] Sizes = [("S", 0.8f), ("M", 1f), ("L", 1.2f), ("XL", 1.4f)];
    public const float MinScale = 0.7f, MaxScale = 1.6f;

    public static readonly Appearance Default = new("Aurora", "Paper", "Aurora", AccentMode.Theme, 0, 1f);

    /// <summary>"subMenus" in settings.json: ring (default) or wheel.</summary>
    public SubMenuStyle SubMenus { get; init; } = SubMenuStyle.Ring;

    public bool FollowsSystem => Theme == Auto;

    /// <summary>The theme to draw with, given whether Windows apps are currently in light mode.</summary>
    public string ThemeFor(bool systemLight) => FollowsSystem ? (systemLight ? LightTheme : DarkTheme) : Theme;

    /// <summary>"S", "M", "L", "XL", or the number (e.g. "1.1") when it isn't one of those.</summary>
    public string SizeName =>
        Sizes.FirstOrDefault(s => MathF.Abs(s.Scale - Scale) < 0.001f).Name ?? Scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Reads the appearance keys of a config. Unknown values are reported and fall back to defaults.</summary>
    public static Appearance From(SpokeConfig c, List<string> errors)
    {
        string theme = ThemeName(c.Theme, allowAuto: true, "theme", errors) ?? Default.Theme;
        string light = ThemeName(c.LightTheme, allowAuto: false, "lightTheme", errors) ?? Default.LightTheme;
        string dark = ThemeName(c.DarkTheme, allowAuto: false, "darkTheme", errors) ?? Default.DarkTheme;

        var accent = AccentMode.Theme;
        uint rgb = 0;
        string a = (c.Accent ?? "").Trim();
        if (a.Length == 0 || a.Equals("theme", StringComparison.OrdinalIgnoreCase) || a.Equals("none", StringComparison.OrdinalIgnoreCase)) { }
        else if (a.Equals("windows", StringComparison.OrdinalIgnoreCase) || a.Equals("system", StringComparison.OrdinalIgnoreCase)) accent = AccentMode.Windows;
        else if (TryParseColor(a, out rgb)) accent = AccentMode.Custom;
        else errors.Add($"\"accent\": \"{a}\" must be \"theme\", \"windows\" or a colour like \"#3B82F6\".");

        float scale = 1f;
        if (c.Size is { } size)
        {
            if (!TryParseSize(size, out scale))
            {
                errors.Add($"\"size\": \"{size}\" must be S, M, L, XL or a number from {MinScale} to {MaxScale}.");
                scale = 1f;
            }
        }
        var sub = SubMenuStyle.Ring;
        switch ((c.SubMenus ?? "").Trim().ToLowerInvariant())
        {
            case "" or "ring" or "extend" or "outer": break;
            case "wheel" or "new" or "replace": sub = SubMenuStyle.Wheel; break;
            default: errors.Add($"\"subMenus\": \"{c.SubMenus}\" must be \"ring\" (extend the wheel) or \"wheel\" (open a new wheel)."); break;
        }
        return new(theme, light, dark, accent, rgb, scale) { SubMenus = sub };
    }

    private static string? ThemeName(string? value, bool allowAuto, string key, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string v = value.Trim();
        if (allowAuto && (v.Equals(Auto, StringComparison.OrdinalIgnoreCase) || v.Equals("system", StringComparison.OrdinalIgnoreCase) || v.Equals("windows", StringComparison.OrdinalIgnoreCase)))
            return Auto;
        foreach (var n in ThemeNames) if (n.Equals(v, StringComparison.OrdinalIgnoreCase)) return n;
        errors.Add($"\"{key}\": \"{v}\" is not a theme (use {string.Join(", ", allowAuto ? [Auto, .. ThemeNames] : ThemeNames)}).");
        return null;
    }

    public static bool TryParseSize(string s, out float scale)
    {
        s = s.Trim();
        foreach (var (name, sc) in Sizes)
            if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) { scale = sc; return true; }
        string num = s.EndsWith('%') ? s[..^1] : s;
        if (float.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out scale))
        {
            if (s.EndsWith('%') || scale > 10) scale /= 100f;   // "120%" or "120"
            if (scale >= MinScale - 0.0001f && scale <= MaxScale + 0.0001f) return true;
        }
        scale = 1f;
        return false;
    }

    /// <summary>"#RGB", "#RRGGBB" or "RRGGBB".</summary>
    public static bool TryParseColor(string s, out uint rgb)
    {
        rgb = 0;
        string h = s.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(ch => new string(ch, 2)));
        return h.Length == 6 && uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out rgb);
    }
}
