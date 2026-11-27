using Windows.Win32.Graphics.Direct2D.Common;
using static Spoke.Daemon.Gfx;

namespace Spoke.Daemon;

/// <summary>A wheel colour scheme. Each slice takes its own hue from <see cref="Hues"/> (by position).</summary>
internal sealed class Theme
{
    public required string Name;
    public required bool Light;
    public required D2D1_COLOR_F RingInner, RingOuter, Stroke, HubTop, HubBottom, HubStroke;
    public required D2D1_COLOR_F Text, TextDim, TextFaint;
    public required float ShadowAlpha, HighlightAlpha, TileAlpha, TileAlphaHover, GlowAlpha;
    public required D2D1_COLOR_F[] Hues;

    public D2D1_COLOR_F Hue(int i) => Hues[((i % Hues.Length) + Hues.Length) % Hues.Length];
    /// <summary>One highlight colour (Windows accent or custom) for glows, edges and the needle; null: each slice's hue.</summary>
    public D2D1_COLOR_F? Highlight { get; init; }
    /// <summary>The colour slice <paramref name="i"/> lights up in. Icons keep <see cref="Hue"/> either way.</summary>
    public D2D1_COLOR_F Glow(int i) => Highlight ?? Hue(i);

    private static D2D1_COLOR_F[] Palette(params uint[] rgb) => rgb.Select(c => C(c)).ToArray();

    /// <summary>Deep navy glass with a full spectrum of slice colours. The default.</summary>
    public static readonly Theme Aurora = new()
    {
        Name = "Aurora", Light = false,
        RingInner = C(0x1A2036), RingOuter = C(0x10142A), Stroke = C(0xFFFFFF, 0.07f),
        HubTop = C(0x1C2238), HubBottom = C(0x0B0E1C), HubStroke = C(0xFFFFFF, 0.10f),
        Text = C(0xF0F3FB), TextDim = C(0xAAB2C8), TextFaint = C(0x667089),
        ShadowAlpha = 0.55f, HighlightAlpha = 0.07f, TileAlpha = 0.16f, TileAlphaHover = 0.30f, GlowAlpha = 0.34f,
        Hues = Palette(0xFF6B6B, 0xFFB547, 0xA3E635, 0x2DD4BF, 0x38BDF8, 0x818CF8, 0xC084FC, 0xF472B6),
    };

    /// <summary>Warm plum-brown with sunset colours.</summary>
    public static readonly Theme Ember = new()
    {
        Name = "Ember", Light = false,
        RingInner = C(0x2A1D24), RingOuter = C(0x1A1116), Stroke = C(0xFFFFFF, 0.07f),
        HubTop = C(0x2B1E25), HubBottom = C(0x130B0F), HubStroke = C(0xFFFFFF, 0.10f),
        Text = C(0xFCF1EC), TextDim = C(0xD0B4AC), TextFaint = C(0x846A64),
        ShadowAlpha = 0.55f, HighlightAlpha = 0.06f, TileAlpha = 0.17f, TileAlphaHover = 0.32f, GlowAlpha = 0.34f,
        Hues = Palette(0xFF7A59, 0xFFB347, 0xFFD166, 0x7AD7C1, 0xF78FB3, 0xE66CA6, 0xC77DFF, 0xFF9F6E),
    };

    /// <summary>Light, paper-white wheel with deeper inks so colours stay readable.</summary>
    public static readonly Theme Paper = new()
    {
        Name = "Paper", Light = true,
        RingInner = C(0xFFFFFF), RingOuter = C(0xF1EEE8), Stroke = C(0x000000, 0.08f),
        HubTop = C(0xFFFFFF), HubBottom = C(0xF4F1EB), HubStroke = C(0x000000, 0.10f),
        Text = C(0x1B1D22), TextDim = C(0x545965), TextFaint = C(0x9A9FA8),
        ShadowAlpha = 0.22f, HighlightAlpha = 0.0f, TileAlpha = 0.12f, TileAlphaHover = 0.20f, GlowAlpha = 0.20f,
        Hues = Palette(0xE5484D, 0xD97706, 0x65A30D, 0x0D9488, 0x0284C7, 0x4F46E5, 0x9333EA, 0xDB2777),
    };

    /// <summary>The spike look: neutral graphite, one amber accent.</summary>
    public static readonly Theme Graphite = new()
    {
        Name = "Graphite", Light = false,
        RingInner = C(0x21242B), RingOuter = C(0x181A1F), Stroke = C(0xFFFFFF, 0.06f),
        HubTop = C(0x1A1D22), HubBottom = C(0x101216), HubStroke = C(0xFFFFFF, 0.09f),
        Text = C(0xE7E9EE), TextDim = C(0xAEB3BD), TextFaint = C(0x6B717C),
        ShadowAlpha = 0.45f, HighlightAlpha = 0.05f, TileAlpha = 0.10f, TileAlphaHover = 0.22f, GlowAlpha = 0.22f,
        Hues = Palette(0xF5A524),
    };

    /// <summary>
    /// Neutral near-black glass where the colour lives in the icons: no tinted tiles, vivid glyphs, and a bold
    /// filled hover in the slice's colour.
    /// </summary>
    public static readonly Theme Onyx = new()
    {
        Name = "Onyx", Light = false,
        RingInner = C(0x1A1A1D), RingOuter = C(0x0F0F11), Stroke = C(0xFFFFFF, 0.06f),
        HubTop = C(0x19191C), HubBottom = C(0x09090A), HubStroke = C(0xFFFFFF, 0.08f),
        Text = C(0xF4F4F6), TextDim = C(0xA6A7AE), TextFaint = C(0x63646B),
        ShadowAlpha = 0.6f, HighlightAlpha = 0.04f, TileAlpha = 0f, TileAlphaHover = 0.0f, GlowAlpha = 0.5f,
        Hues = Palette(0xFF5A4E, 0xFF9F1C, 0xFFD23F, 0x3DDC84, 0x2EC4F1, 0x4D7CFE, 0xA66CFF, 0xFF5CA8),
    };

    public static readonly Theme[] All = [Aurora, Ember, Paper, Graphite, Onyx];

    /// <summary>The same theme lit in one colour (the Windows accent or a custom one). Icons stay in the theme's colours.</summary>
    public Theme WithAccent(uint rgb)
    {
        var c = C(rgb);
        // On light themes a pale accent would vanish: darken it a little so tiles and glows stay visible.
        if (Light && 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b > 0.6f) c = new D2D1_COLOR_F { r = c.r * 0.7f, g = c.g * 0.7f, b = c.b * 0.7f, a = 1 };
        return new Theme
        {
            Name = Name, Light = Light,
            RingInner = RingInner, RingOuter = RingOuter, Stroke = Stroke, HubTop = HubTop, HubBottom = HubBottom, HubStroke = HubStroke,
            Text = Text, TextDim = TextDim, TextFaint = TextFaint,
            ShadowAlpha = ShadowAlpha, HighlightAlpha = HighlightAlpha, TileAlpha = TileAlpha, TileAlphaHover = TileAlphaHover, GlowAlpha = GlowAlpha,
            Hues = Hues,
            Highlight = c,
        };
    }

    public static Theme ByName(string? name) => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Aurora;
}
