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

    public static readonly Theme[] All = [Aurora, Ember, Paper, Graphite];

    public static Theme ByName(string? name) => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Aurora;
}
