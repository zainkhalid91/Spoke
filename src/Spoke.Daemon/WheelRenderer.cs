using System.Diagnostics;
using System.Runtime.InteropServices;
using Spoke.Core;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using static Spoke.Daemon.Gfx;

namespace Spoke.Daemon;

/// <summary>An image slot in the Clips wheel: a GPU thumbnail and its brush.</summary>
internal unsafe struct ClipThumb
{
    public ID2D1Bitmap1* Bitmap;
    public ID2D1BitmapBrush1* Brush;
    public int Width, Height;     // thumbnail pixels
    public string Info;           // "1920×1080 · 3 min"
    public readonly bool Valid => Brush != null;
}

/// <summary>What to draw: the main wheel or the Clips (image history) wheel.</summary>
internal sealed class WheelView
{
    public int Hover = -1;
    public bool Clips;
    public ClipThumb[] Thumbs = new ClipThumb[8];
    public int ClipCount, Page, Pages;
}

/// <summary>
/// Draws the wheel with Direct2D into the overlay's composition swap chain. Geometry, text formats and
/// theme brushes are built once (and again only on a theme change), so an open is one draw + present.
/// Drawing is in DIPs; the surface DPI maps DIPs to the monitor's physical pixels.
/// </summary>
internal sealed unsafe class WheelRenderer
{
    public const float Extent = 216f;   // half the drawn square: ring + hover lift + shadow
    public const float Lift = 9f;
    public readonly WheelLayout Layout;

    private readonly Gfx _g;
    private readonly Surface _surface;
    private readonly SliceDef[] _slices;
    private readonly string[] _numbers;

    private IDWriteTextFormat* _fmtLabel, _fmtNum, _fmtGlyph, _fmtGlyphHub, _fmtHub, _fmtHubSub, _fmtPill;
    private ID2D1PathGeometry** _sliceGeo;
    private ID2D1PathGeometry** _edgeGeo;
    private D2D_RECT_F* _sliceBounds;
    private ID2D1EllipseGeometry* _previewGeo;

    // Theme brushes (rebuilt on theme change).
    public Theme Theme { get; private set; } = Theme.Aurora;
    private ID2D1RadialGradientBrush* _ringBrush, _hubBrush, _shadowBrush;
    private ID2D1RadialGradientBrush** _glow;   // one per slice hue
    private ID2D1LinearGradientBrush* _highlight;

    // Cached resting wheel (built on first open per DPI + theme).
    private ID2D1Bitmap1* _cacheFull, _cacheUnder;
    private ID2D1BitmapBrush1* _cacheUnderBrush;
    private uint _cacheDpi;
    private Theme? _cacheTheme;
    private readonly string[] _glyphs;

    // Draw-time stats for the bench report.
    public long DrawCount;
    public double DrawMsTotal, DrawMsMax;

    public uint Dpi => _surface.Dpi;
    public int PixelSize => _surface.Width;
    public Surface Surface => _surface;

    public WheelRenderer(Gfx g, HWND hwnd, SliceDef[] slices)
    {
        _g = g;
        _slices = slices;
        Layout = new WheelLayout(slices.Length, hubRadius: 60f, innerRadius: 76f, outerRadius: 172f, gap: 0.03f);
        _numbers = new string[slices.Length];
        for (int i = 0; i < slices.Length; i++) _numbers[i] = i < 9 ? (i + 1).ToString() : "";
        _glyphs = Array.ConvertAll(slices, x => x.Glyph.ToString());

        const string ui = "Segoe UI Variable Text", mono = "Cascadia Mono";
        _fmtLabel = g.Format(ui, 11.5f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD);
        _fmtNum = g.Format(mono, 8.5f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
        _fmtGlyph = g.Format(g.IconFont, 15f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
        _fmtGlyphHub = g.Format(g.IconFont, 18f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
        _fmtHub = g.Format("Segoe UI Variable Display", 14f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD);
        _fmtHubSub = g.Format(ui, 9f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
        _fmtPill = g.Format(mono, 8.5f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);

        int n = slices.Length;
        _sliceGeo = (ID2D1PathGeometry**)NativeMemory.AllocZeroed((nuint)(n * sizeof(nint)));
        _edgeGeo = (ID2D1PathGeometry**)NativeMemory.AllocZeroed((nuint)(n * sizeof(nint)));
        _sliceBounds = (D2D_RECT_F*)NativeMemory.AllocZeroed((nuint)(n * sizeof(D2D_RECT_F)));
        for (int i = 0; i < n; i++)
        {
            var (a1, a2) = Layout.SliceAngles(i);
            _sliceGeo[i] = g.Annulus(Layout.InnerRadius, Layout.OuterRadius, a1, a2);
            _edgeGeo[i] = g.Annulus(Layout.InnerRadius, Layout.InnerRadius + 3.5f, a1, a2);
            D2D_RECT_F b;
            ((ID2D1Geometry*)_sliceGeo[i])->GetBounds(null, &b);
            _sliceBounds[i] = b;
        }
        _previewGeo = g.Ellipse(0, 0, Layout.HubRadius + 6);

        _surface = new Surface(g, hwnd);
        SetTheme(Theme.Aurora);
        SetDpi(96);
    }

    public void SetTheme(Theme t)
    {
        Theme = t;
        Release(_ringBrush); Release(_hubBrush); Release(_shadowBrush); Release(_highlight);
        if (_glow == null) _glow = (ID2D1RadialGradientBrush**)NativeMemory.AllocZeroed((nuint)(_slices.Length * sizeof(nint)));
        for (int i = 0; i < _slices.Length; i++)
        {
            Release(_glow[i]);
            var h = t.Hue(i);
            _glow[i] = _g.Radial(0, 0, 118, (0f, WithAlpha(h, t.GlowAlpha)), (0.55f, WithAlpha(h, t.GlowAlpha * 0.35f)), (1f, WithAlpha(h, 0)));
        }
        float o = Layout.OuterRadius, inner = Layout.InnerRadius / o;
        _ringBrush = _g.Radial(0, 0, o, (inner, t.RingInner), (1f, t.RingOuter));
        _hubBrush = _g.Radial(0, -Layout.HubRadius * 0.6f, Layout.HubRadius * 1.7f, (0f, t.HubTop), (1f, t.HubBottom));
        float sr = o + 30;
        _shadowBrush = _g.Radial(0, 0, sr, ((o - 24) / sr, C(0, t.ShadowAlpha)), ((o - 4) / sr, C(0, t.ShadowAlpha * 0.45f)), (1f, C(0, 0)));
        _highlight = _g.Linear(0, -o, 0, o * 0.2f, (0f, C(0xFFFFFF, t.HighlightAlpha)), (1f, C(0xFFFFFF, 0)));
    }

    /// <summary>Resizes the swap chain for a monitor DPI. A no-op unless the DPI changed since the last open.</summary>
    public void SetDpi(uint dpi)
    {
        int px = (int)MathF.Ceiling(Extent * 2 * dpi / 96f);
        _surface.Resize(px, px, dpi);
    }

    public void SetOffset(int x, int y) => _surface.SetOffset(x, y);
    public void AnimateIn() => _surface.AnimateIn(PixelSize / 2f, PixelSize / 2f);
    public void Commit() => _g.DComp->Commit();
    public void WaitForCommitCompletion() => _g.DComp->WaitForCommitCompletion();

    /// <summary>
    /// Draws the wheel and presents it. For the main wheel the resting picture (shadow, backplate, every slice,
    /// dial numbers, hub fill) is a cached bitmap built once per DPI and theme, so a frame is one blit, a
    /// restore of the hovered slice's resting spot, the lifted slice, the needle and the hub text.
    /// </summary>
    public void Draw(WheelView v)
    {
        long t0 = Stopwatch.GetTimestamp();
        bool cached = !v.Clips && EnsureCache();
        if (!_surface.Begin()) return;
        var t = Theme;
        var dc = _g.Dc;
        var ring = Layout;
        float E = Extent;
        int hover = v.Hover;

        if (cached)
        {
            _g.SetTranslate(0, 0);
            dc->DrawImage((ID2D1Image*)_cacheFull, null, null, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, D2D1_COMPOSITE_MODE.D2D1_COMPOSITE_MODE_SOURCE_COPY);
            _g.SetTranslate(E, E);
            if (hover >= 0)
            {
                // Put back what lies under the hovered slice (shadow + backplate), then draw it lifted.
                var geo = (ID2D1Geometry*)_sliceGeo[hover];
                dc->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
                dc->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND.D2D1_PRIMITIVE_BLEND_COPY);
                dc->FillGeometry(geo, (ID2D1Brush*)_cacheUnderBrush, null);
                dc->DrawGeometry(geo, (ID2D1Brush*)_cacheUnderBrush, 2.5f, null);
                dc->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND.D2D1_PRIMITIVE_BLEND_SOURCE_OVER);
                dc->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                DrawSlice(hover, v, true);
            }
        }
        else
        {
            DrawUnder();
            for (int i = 0; i < _slices.Length; i++) DrawSlice(i, v, i == hover);
            DrawDial(hover);
        }

        var accent = hover >= 0 ? t.Hue(hover) : t.Hue(0);

        // The spoke: a needle from the hub to the hovered slice, with its number on a bead.
        if (hover >= 0)
        {
            float am = ring.CenterAngle(hover);
            float c = MathF.Cos(am), sn = MathF.Sin(am);
            float r = ring.InnerRadius + Lift + 1f;
            _g.Line(ring.HubRadius * c, ring.HubRadius * sn, r * c, r * sn, accent, 2f);
            _g.FillCircle(Dial * c, Dial * sn, 7.5f, accent);
            _g.Text(_numbers[hover], _fmtNum, Dial * c, Dial * sn, 14, 12, t.Light ? C(0xFFFFFF) : C(0x0B0E1A));
        }

        // Hub (the cached picture already has its fill).
        if (!cached) _g.FillCircle(0, 0, ring.HubRadius, (ID2D1Brush*)_hubBrush);
        _g.StrokeCircle(0, 0, ring.HubRadius, hover >= 0 ? WithAlpha(accent, 0.55f) : t.HubStroke, 1f);

        if (v.Clips) DrawClipsHub(v, accent);
        else DrawMainHub(hover, accent);

        _surface.End();

        double ms = Log.Ms(t0, Stopwatch.GetTimestamp());
        DrawCount++;
        DrawMsTotal += ms;
        if (ms > DrawMsMax) DrawMsMax = ms;
    }

    private float Dial => (Layout.HubRadius + Layout.InnerRadius) / 2f;

    /// <summary>Soft shadow and the backplate that ties hub and slices into one object.</summary>
    private void DrawUnder()
    {
        var t = Theme;
        float E = Extent;
        _g.SetTranslate(E, E + 7);
        _g.FillCircle(0, 0, Layout.OuterRadius + 30, (ID2D1Brush*)_shadowBrush);
        _g.SetTranslate(E, E);
        _g.FillCircle(0, 0, Layout.OuterRadius + 1.5f, t.Light ? WithAlpha(t.RingOuter, 0.92f) : WithAlpha(t.HubBottom, 0.82f));
    }

    /// <summary>Slice numbers around the hub, in the gap between hub and ring.</summary>
    private void DrawDial(int hover)
    {
        for (int i = 0; i < _slices.Length; i++)
        {
            if (i == hover) continue;
            float am = Layout.CenterAngle(i);
            _g.Text(_numbers[i], _fmtNum, Dial * MathF.Cos(am), Dial * MathF.Sin(am), 14, 12, Theme.TextFaint);
        }
    }

    private void DrawSlice(int i, WheelView v, bool hov)
    {
        var t = Theme;
        var dc = _g.Dc;
        var ring = Layout;
        float E = Extent;
        float am = ring.CenterAngle(i);
        var hue = t.Hue(i);
        if (hov) _g.SetTranslate(E + Lift * MathF.Cos(am), E + Lift * MathF.Sin(am));
        var geo = (ID2D1Geometry*)_sliceGeo[i];

        bool image = v.Clips && v.Thumbs[i].Valid;
        if (image) FillCover(geo, _sliceBounds[i], v.Thumbs[i]);
        else dc->FillGeometry(geo, (ID2D1Brush*)_ringBrush, null);

        if (hov && !image)
        {
            // Coloured glow from the slice's inner edge outwards.
            float rg = ring.InnerRadius + 14;
            _glow[i]->SetCenter(new D2D_POINT_2F { x = rg * MathF.Cos(am), y = rg * MathF.Sin(am) });
            dc->FillGeometry(geo, (ID2D1Brush*)_glow[i], null);
        }
        if (image && !hov)
        {
            _g.SetColor(C(0x000000, 0.42f));
            dc->FillGeometry(geo, (ID2D1Brush*)_g.Brush, null);
        }
        if (!image) dc->FillGeometry(geo, (ID2D1Brush*)_highlight, null);

        _g.SetColor(hov ? WithAlpha(hue, 0.65f) : t.Stroke);
        dc->DrawGeometry(geo, (ID2D1Brush*)_g.Brush, hov ? 1.2f : 1f, null);
        if (hov)
        {
            _g.SetColor(hue);
            dc->FillGeometry((ID2D1Geometry*)_edgeGeo[i], (ID2D1Brush*)_g.Brush, null);
        }

        float rc = (ring.InnerRadius + ring.OuterRadius) / 2f + 3f;
        float ix = rc * MathF.Cos(am), iy = rc * MathF.Sin(am);
        if (v.Clips)
        {
            if (!image) _g.Text("·", _fmtLabel, ix, iy, 20, 20, t.TextFaint);
        }
        else
        {
            // Colour tile + glyph, then the label.
            _g.FillRound(ix - 16, iy - 27, ix + 16, iy + 5, 10, WithAlpha(hue, hov ? t.TileAlphaHover : t.TileAlpha));
            if (hov) _g.StrokeRound(ix - 16, iy - 27, ix + 16, iy + 5, 10, WithAlpha(hue, 0.55f), 1f);
            _g.Text(_glyphs[i], _fmtGlyph, ix, iy - 11, 32, 32, hue);
            _g.Text(_slices[i].Label, _fmtLabel, ix, iy + 18, 110, 16, hov ? t.Text : t.TextDim);
        }

        if (hov) _g.SetTranslate(E, E);
    }

    /// <summary>
    /// Builds the two cached pictures for the current DPI and theme: "under" (shadow + backplate) and "full"
    /// (the whole resting wheel). Returns false if there is no surface yet.
    /// </summary>
    private bool EnsureCache()
    {
        if (_cacheFull != null && _cacheDpi == _surface.Dpi && _cacheTheme == Theme) return true;
        ReleaseCache();
        int px = _surface.Width;
        if (px == 0) return false;
        _cacheUnder = RenderToBitmap(px, () => DrawUnder());
        var rest = new WheelView();
        _cacheFull = RenderToBitmap(px, () =>
        {
            DrawUnder();
            for (int i = 0; i < _slices.Length; i++) DrawSlice(i, rest, false);
            DrawDial(-1);
            _g.FillCircle(0, 0, Layout.HubRadius, (ID2D1Brush*)_hubBrush);
        });
        _cacheUnderBrush = _g.BitmapBrush(_cacheUnder, nearest: true);
        // Brush space follows the world transform (translated by Extent); map it back to bitmap space.
        var m = Matrix(1, 0, 0, 1, -Extent, -Extent);
        ((ID2D1Brush*)_cacheUnderBrush)->SetTransform(&m);
        _cacheDpi = _surface.Dpi;
        _cacheTheme = Theme;
        Log.Write($"Wheel cache built ({px}px @ {_cacheDpi} dpi, {Theme.Name})");
        return true;
    }

    private ID2D1Bitmap1* RenderToBitmap(int px, Action draw)
    {
        var props = new D2D1_BITMAP_PROPERTIES1
        {
            pixelFormat = new D2D1_PIXEL_FORMAT { format = Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
            dpiX = _surface.Dpi, dpiY = _surface.Dpi,
            bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET,
        };
        ID2D1Bitmap1* bmp;
        var dc = _g.Dc;
        dc->CreateBitmap(new D2D_SIZE_U { width = (uint)px, height = (uint)px }, null, 0, &props, &bmp);
        dc->SetTarget((ID2D1Image*)bmp);
        dc->SetDpi(_surface.Dpi, _surface.Dpi);
        dc->BeginDraw();
        var clear = new D2D1_COLOR_F();
        dc->Clear(&clear);
        draw();
        dc->EndDraw(null, null);
        dc->SetTarget(null);
        return bmp;
    }

    /// <summary>Drops the cached pictures (on theme or DPI change, and when the app trims memory while idle).</summary>
    public void ReleaseCache()
    {
        Release(_cacheUnderBrush); _cacheUnderBrush = null;
        Release(_cacheUnder); _cacheUnder = null;
        Release(_cacheFull); _cacheFull = null;
        _cacheTheme = null;
    }

    private void DrawMainHub(int hover, D2D1_COLOR_F accent)
    {
        var t = Theme;
        if (hover >= 0)
        {
            var s = _slices[hover];
            _g.Text(s.Glyph.ToString(), _fmtGlyphHub, 0, -24, 40, 26, accent);
            _g.Text(s.Label, _fmtHub, 0, 2, 112, 20, t.Text);
            _g.Text(s.Detail, _fmtHubSub, 0, 20, 112, 13, t.TextFaint);
        }
        else
        {
            _g.FillCircle(0, -22, 4f, accent);
            _g.Text(Wheels.Name, _fmtHub, 0, 0, 112, 20, t.Text);
            _g.Text("1–8 · arrows · Esc", _fmtHubSub, 0, 19, 112, 13, t.TextFaint);
        }
    }

    private void DrawClipsHub(WheelView v, D2D1_COLOR_F accent)
    {
        var t = Theme;
        if (v.Hover >= 0 && v.Thumbs[v.Hover].Valid)
        {
            // The hub becomes a lens on the hovered image.
            var th = v.Thumbs[v.Hover];
            float r = Layout.HubRadius + 6;
            FillCover((ID2D1Geometry*)_previewGeo, new D2D_RECT_F { left = -r, top = -r, right = r, bottom = r }, th);
            _g.StrokeCircle(0, 0, r, accent, 2f);
            float w = _g.Measure(th.Info, _fmtPill) + 14;
            _g.FillRound(-w / 2, r - 22, w / 2, r - 7, 7.5f, C(0x000000, 0.72f));
            _g.Text(th.Info, _fmtPill, 0, r - 14.5f, w, 14, C(0xFFFFFF, 0.92f));
            return;
        }
        _g.Text(''.ToString(), _fmtGlyphHub, 0, -24, 40, 26, accent);
        if (v.ClipCount == 0)
        {
            _g.Text("No images yet", _fmtHub, 0, 2, 118, 20, t.Text);
            _g.Text("Copy one, or screenshot", _fmtHubSub, 0, 20, 118, 13, t.TextFaint);
            return;
        }
        _g.Text("Clips", _fmtHub, 0, 2, 112, 20, t.Text);
        string sub = v.Pages > 1 ? $"{v.ClipCount} images · {v.Page + 1}/{v.Pages}" : $"{v.ClipCount} image{(v.ClipCount == 1 ? "" : "s")}";
        _g.Text(sub, _fmtHubSub, 0, 20, 118, 13, t.TextFaint);
    }

    /// <summary>Fills a geometry with an image scaled to cover its bounds (centre crop).</summary>
    private void FillCover(ID2D1Geometry* geo, D2D_RECT_F b, ClipThumb th)
    {
        float bw = b.right - b.left, bh = b.bottom - b.top;
        float s = MathF.Max(bw / th.Width, bh / th.Height);
        var m = Matrix(s, 0, 0, s, b.left + (bw - th.Width * s) / 2, b.top + (bh - th.Height * s) / 2);
        ((ID2D1Brush*)th.Brush)->SetTransform(&m);
        _g.Dc->FillGeometry(geo, (ID2D1Brush*)th.Brush, null);
    }

}
