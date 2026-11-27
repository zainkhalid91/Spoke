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
    /// <summary>The hovered item of the outer band (subMenus "ring"), or -1.</summary>
    public int RingHover = -1;
    /// <summary>The hovered pill of the wheel row (index into the wheels), or -1.</summary>
    public int RowHover = -1;
    public bool Clips;
    public ClipThumb[] Thumbs = new ClipThumb[Spoke.Core.ConfigLoader.MaxSlices];
    public int ClipCount, Page, Pages;
    /// <summary>Search box mode: the hovered search slice's query being typed.</summary>
    public bool Query;
    public string QueryText = "";
    public string QueryEngine = "";
    /// <summary>Drop mode: files are being dragged onto the wheel (the wheel row is hidden).</summary>
    public bool Drop;
}

/// <summary>
/// Draws the wheel with Direct2D into the overlay's composition swap chain. Geometry, text formats and
/// theme brushes are built once (and again only on a theme change), so an open is one draw + present.
/// Drawing is in DIPs; the surface DPI maps DIPs to the monitor's physical pixels.
/// </summary>
internal sealed unsafe class WheelRenderer
{
    /// <summary>Half the drawn square: the wheel + hover lift + shadow, or the outer band's reach in ring mode.</summary>
    public float Extent { get; private set; } = WheelExtent;
    private const float WheelExtent = 216f;
    public const float Lift = 9f;
    public WheelLayout Layout { get; private set; }
    private const int MaxSlices = Spoke.Core.ConfigLoader.MaxSlices;
    /// <summary>Shown in the hub when nothing is hovered.</summary>
    public string WheelName = "Spoke";
    /// <summary>Replaces the hub's "1–n · arrows · Esc" line (drop mode: "photo.heic · 4.9 MB").</summary>
    public string? HubHint;
    /// <summary>Drop mode: the dropped files' size now, shown as "4.9 MB → 1.4 MB" for the hovered format.</summary>
    public string? HubFrom;

    private readonly Gfx _g;
    private readonly Surface _surface;
    private SliceDef[] _slices = [];
    private readonly string[] _numbers = new string[MaxSlices];

    private IDWriteTextFormat* _fmtQuery, _fmtQueryHint;
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
    private readonly string[] _glyphs = new string[MaxSlices];

    // The outer band (subMenus "ring"): built when it opens, released when it closes.
    public sealed class RingBand
    {
        public required int Parent;
        public required SliceDef[] Items;
        public required string Title;
        public required RingLayout Layout;
        public ID2D1PathGeometry*[] Geo = [];
        public ID2D1PathGeometry*[] Edge = [];
        public ID2D1PathGeometry* Back;
        public string[] Glyphs = [];
    }
    public RingBand? Ring { get; private set; }

    // The wheel row: a pill per wheel under the ring (only with 2+ wheels). Pills that don't fit are left out
    // around the active one; Tab still reaches them.
    private string[] _rowNames = [];
    private int _rowActive;
    private readonly List<(int Index, float L, float R, string Text)> _rowPills = [];
    private IDWriteTextFormat* _fmtRow;
    private const float RowH = 20f;
    public bool HasRow => _rowNames.Length > 1;
    private float RowY => Layout.OuterRadius + 27f;
    /// <summary>How far below the centre the row reaches (for clamping the wheel on screen).</summary>
    public float RowBottom => RowY + RowH / 2 + 2;
    private IDWriteTextFormat* _fmtRingLabel;
    private uint _monitorDpi = 96;

    // Draw-time stats for the bench report.
    public long DrawCount;
    public double DrawMsTotal, DrawMsMax;

    public uint Dpi => _surface.Dpi;
    public int PixelSize => _surface.Width;
    public Surface Surface => _surface;

    public WheelRenderer(Gfx g, HWND hwnd, SliceDef[] slices)
    {
        _g = g;
        for (int i = 0; i < MaxSlices; i++) _numbers[i] = i < 9 ? (i + 1).ToString() : "";

        const string ui = "Segoe UI Variable Text", mono = "Cascadia Mono";
        _fmtLabel = g.Format(ui, 11.5f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD);
        _fmtNum = g.Format(mono, 8.5f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
        _fmtGlyph = g.Format(g.IconFont, 15f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
        _fmtGlyphHub = g.Format(g.IconFont, 18f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
        _fmtHub = g.Format("Segoe UI Variable Display", 14f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD);
        _fmtHubSub = g.Format(ui, 9f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
        _fmtPill = g.Format(mono, 8.5f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
        _fmtRingLabel = g.Format(ui, 10f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD);
        _fmtRow = g.Format(ui, 10f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD);

        _fmtQuery = g.Format(ui, 15f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING);
        _fmtQueryHint = g.Format(ui, 10f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);

        _sliceGeo = (ID2D1PathGeometry**)NativeMemory.AllocZeroed((nuint)(MaxSlices * sizeof(nint)));
        _edgeGeo = (ID2D1PathGeometry**)NativeMemory.AllocZeroed((nuint)(MaxSlices * sizeof(nint)));
        _sliceBounds = (D2D_RECT_F*)NativeMemory.AllocZeroed((nuint)(MaxSlices * sizeof(D2D_RECT_F)));
        _glow = (ID2D1RadialGradientBrush**)NativeMemory.AllocZeroed((nuint)(MaxSlices * sizeof(nint)));
        SetSlices(slices);
        _previewGeo = g.Ellipse(0, 0, Layout.HubRadius + 6);

        _surface = new Surface(g, hwnd);
        SetTheme(Theme.Aurora);
        SetDpi(96);
    }

    /// <summary>Replaces the wheel's slices (settings.json reloaded): new geometry when the count changes.</summary>
    public void SetSlices(SliceDef[] slices)
    {
        int n = slices.Length;
        if (n != _slices.Length)
        {
            Layout = new WheelLayout(n, hubRadius: 60f, innerRadius: 76f, outerRadius: 172f, gap: n > 8 ? 0.022f : 0.03f);
            for (int i = 0; i < MaxSlices; i++)
            {
                Release(_sliceGeo[i]); _sliceGeo[i] = null;
                Release(_edgeGeo[i]); _edgeGeo[i] = null;
                if (i >= n) continue;
                var (a1, a2) = Layout.SliceAngles(i);
                _sliceGeo[i] = _g.Annulus(Layout.InnerRadius, Layout.OuterRadius, a1, a2);
                _edgeGeo[i] = _g.Annulus(Layout.InnerRadius, Layout.InnerRadius + 3.5f, a1, a2);
                D2D_RECT_F b;
                ((ID2D1Geometry*)_sliceGeo[i])->GetBounds(null, &b);
                _sliceBounds[i] = b;
            }
        }
        _slices = slices;
        for (int i = 0; i < n; i++) _glyphs[i] = slices[i].Glyph.ToString();
        ReleaseCache();
    }

    public SliceDef[] Slices => _slices;

    /// <summary>The wheels to show in the row and which one is showing. One name (or none) hides the row.</summary>
    public void SetRow(string[] names, int active)
    {
        _rowNames = names;
        _rowActive = active;
        _rowPills.Clear();
        if (names.Length < 2) return;
        const float gap = 6f, maxWidth = 404f;
        var widths = new float[names.Length];
        var texts = new string[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            string n = names[i].Length > 18 ? names[i][..17] + "…" : names[i];
            texts[i] = n;
            widths[i] = MathF.Ceiling(_g.Measure(n, _fmtRow)) + 22f;
        }
        // Grow outwards from the active wheel while there's room.
        int lo = active, hi = active;
        float total = widths[active];
        for (bool grew = true; grew;)
        {
            grew = false;
            if (hi + 1 < names.Length && total + gap + widths[hi + 1] <= maxWidth) { total += gap + widths[++hi]; grew = true; }
            if (lo - 1 >= 0 && total + gap + widths[lo - 1] <= maxWidth) { total += gap + widths[--lo]; grew = true; }
        }
        float x = -total / 2;
        for (int i = lo; i <= hi; i++)
        {
            _rowPills.Add((i, x, x + widths[i], texts[i]));
            x += widths[i] + gap;
        }
    }

    /// <summary>The wheel whose pill is under a point (relative to the centre, DIPs), or -1.</summary>
    public int RowHit(float dx, float dy)
    {
        if (!HasRow || Ring != null || MathF.Abs(dy - RowY) > RowH / 2 + 3) return -1;
        foreach (var p in _rowPills) if (dx >= p.L - 3 && dx <= p.R + 3) return p.Index;
        return -1;
    }

    /// <summary>Builds the cached resting wheel now (after returning from a sub-wheel), so the next open is one blit.</summary>
    public void Warm() => EnsureCache();

    public void SetTheme(Theme t)
    {
        Theme = t;
        Release(_ringBrush); Release(_hubBrush); Release(_shadowBrush); Release(_highlight);
        for (int i = 0; i < MaxSlices; i++)
        {
            Release(_glow[i]);
            var h = t.Glow(i);
            _glow[i] = _g.Radial(0, 0, 118, (0f, WithAlpha(h, t.GlowAlpha)), (0.55f, WithAlpha(h, t.GlowAlpha * 0.35f)), (1f, WithAlpha(h, 0)));
        }
        float o = Layout.OuterRadius, inner = Layout.InnerRadius / o;
        _ringBrush = _g.Radial(0, 0, o, (inner, t.RingInner), (1f, t.RingOuter));
        _hubBrush = _g.Radial(0, -Layout.HubRadius * 0.6f, Layout.HubRadius * 1.7f, (0f, t.HubTop), (1f, t.HubBottom));
        float sr = o + 30;
        _shadowBrush = _g.Radial(0, 0, sr, ((o - 24) / sr, C(0, t.ShadowAlpha)), ((o - 4) / sr, C(0, t.ShadowAlpha * 0.45f)), (1f, C(0, 0)));
        _highlight = _g.Linear(0, -o, 0, o * 0.2f, (0f, C(0xFFFFFF, t.HighlightAlpha)), (1f, C(0xFFFFFF, 0)));
    }

    /// <summary>Wheel size (settings "size"): applied as a DPI factor, so everything scales and stays sharp.</summary>
    public float Scale = 1f;

    /// <summary>Resizes the swap chain for a monitor DPI (times <see cref="Scale"/>). A no-op unless that changed since the last open.</summary>
    public void SetDpi(uint monitorDpi)
    {
        _monitorDpi = monitorDpi;
        uint dpi = (uint)MathF.Round(monitorDpi * Scale);
        int px = (int)MathF.Ceiling(Extent * 2 * dpi / 96f);
        _surface.Resize(px, px, dpi);
    }

    /// <summary>
    /// Ring mode needs a larger drawing square so the outer band fits. Changing it resizes the surface and
    /// drops the cached wheel; done when settings change, never while the wheel is open.
    /// </summary>
    public void SetRingMode(bool ring)
    {
        float e = ring ? MathF.Ceiling(RingLayout.Reach(Layout.OuterRadius) + 14f) : WheelExtent;
        if (e == Extent) return;
        Extent = e;
        ReleaseCache();
        SetDpi(_monitorDpi);
    }

    /// <summary>Shows the outer band of <paramref name="items"/> around slice <paramref name="parent"/>.</summary>
    public void OpenRing(int parent, SliceDef[] items, string title)
    {
        CloseRing();
        var layout = new RingLayout(items.Length, Layout.CenterAngle(parent), Layout.OuterRadius);
        var band = new RingBand { Parent = parent, Items = items, Title = title, Layout = layout };
        band.Geo = new ID2D1PathGeometry*[items.Length];
        band.Edge = new ID2D1PathGeometry*[items.Length];
        band.Glyphs = new string[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            var (a1, a2) = layout.ItemAngles(i);
            band.Geo[i] = _g.Annulus(layout.InnerRadius, layout.OuterRadius, a1, a2);
            band.Edge[i] = _g.Annulus(layout.InnerRadius, layout.InnerRadius + 3f, a1, a2);
            band.Glyphs[i] = items[i].Glyph.ToString();
        }
        float pad = 3f / ((layout.InnerRadius + layout.OuterRadius) / 2f);
        band.Back = _g.Annulus(layout.InnerRadius - 3f, layout.OuterRadius + 3f, layout.Start - pad, layout.Start + layout.Step * items.Length + pad);
        Ring = band;
    }

    public void CloseRing()
    {
        if (Ring is not { } band) return;
        foreach (var g in band.Geo) Release(g);
        foreach (var g in band.Edge) Release(g);
        Release(band.Back);
        Ring = null;
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
        DrawBody(v, cached);
        _surface.End();

        double ms = Log.Ms(t0, Stopwatch.GetTimestamp());
        DrawCount++;
        DrawMsTotal += ms;
        if (ms > DrawMsMax) DrawMsMax = ms;
    }

    /// <summary>
    /// Draws a frame into an offscreen bitmap over an opaque backdrop and returns its pixels (for
    /// <c>Spoke.exe --render</c>: checking the look without showing anything on screen).
    /// </summary>
    public Pixels Snapshot(WheelView v, uint backdropRgb)
    {
        int px = _surface.Width;
        var bg = C(backdropRgb);
        var bmp = RenderToBitmap(px, () =>
        {
            var fill = bg;
            _g.Dc->Clear(&fill);
            DrawBody(v, cached: false);
        });
        var props = new D2D1_BITMAP_PROPERTIES1
        {
            pixelFormat = new D2D1_PIXEL_FORMAT { format = Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
            dpiX = _surface.Dpi, dpiY = _surface.Dpi,
            bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CPU_READ | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
        };
        ID2D1Bitmap1* cpu;
        _g.Dc->CreateBitmap(new D2D_SIZE_U { width = (uint)px, height = (uint)px }, null, 0, &props, &cpu);
        ((ID2D1Bitmap*)cpu)->CopyFromBitmap(null, (ID2D1Bitmap*)bmp, null);
        D2D1_MAPPED_RECT mapped;
        cpu->Map(D2D1_MAP_OPTIONS.D2D1_MAP_OPTIONS_READ, &mapped);
        var data = new byte[px * px * 4];
        for (int y = 0; y < px; y++)
            new ReadOnlySpan<byte>(mapped.bits + y * mapped.pitch, px * 4).CopyTo(data.AsSpan(y * px * 4));
        cpu->Unmap();
        Release(cpu);
        Release(bmp);
        return new Pixels { Width = px, Height = px, Data = data };
    }

    private void DrawBody(WheelView v, bool cached)
    {
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
            // The hovered slice and the slice whose band is open are drawn lifted.
            int parent = Ring?.Parent ?? -1;
            foreach (int lifted in (ReadOnlySpan<int>)[parent, hover == parent ? -1 : hover])
            {
                if (lifted < 0) continue;
                // Put back what lies under the slice (shadow + backplate), then draw it lifted.
                var geo = (ID2D1Geometry*)_sliceGeo[lifted];
                dc->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
                dc->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND.D2D1_PRIMITIVE_BLEND_COPY);
                dc->FillGeometry(geo, (ID2D1Brush*)_cacheUnderBrush, null);
                dc->DrawGeometry(geo, (ID2D1Brush*)_cacheUnderBrush, 2.5f, null);
                dc->SetPrimitiveBlend(D2D1_PRIMITIVE_BLEND.D2D1_PRIMITIVE_BLEND_SOURCE_OVER);
                dc->SetAntialiasMode(D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE);
                DrawSlice(lifted, v, true);
            }
        }
        else
        {
            DrawUnder();
            int parent = Ring?.Parent ?? -1;
            for (int i = 0; i < _slices.Length; i++) DrawSlice(i, v, i == hover || i == parent);
            DrawDial(hover);
        }

        if (Ring is { } band) DrawRing(band, v);
        // With a band open, the colour (and needle) belong to its slice unless another slice is hovered.
        int lead = hover >= 0 ? hover : Ring?.Parent ?? -1;
        var accent = lead >= 0 ? t.Glow(lead) : t.Glow(0);
        hover = lead;

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
        else if (Ring is { } r && (v.RingHover >= 0 || v.Hover < 0)) DrawRingHub(r, v.RingHover, accent);
        else DrawMainHub(hover, accent);
        if (v.Query) DrawQuery(v, accent);
        else if (HasRow && Ring == null && !v.Clips && !v.Drop) DrawRow(v);
    }

    /// <summary>The wheel row: the showing wheel as a solid pill, the others as quiet ones.</summary>
    private void DrawRow(WheelView v)
    {
        var t = Theme;
        float y = RowY, top = y - RowH / 2, bottom = y + RowH / 2;
        foreach (var (i, l, r, text) in _rowPills)
        {
            bool active = i == _rowActive, hov = i == v.RowHover;
            if (active)
            {
                _g.FillRound(l, top, r, bottom, RowH / 2, t.Light ? C(0x20242C) : C(0xEEF1F6));
                _g.Text(text, _fmtRow, (l + r) / 2, y, r - l, RowH, t.Light ? C(0xFFFFFF) : C(0x14171E));
            }
            else
            {
                _g.FillRound(l, top, r, bottom, RowH / 2, t.Light ? WithAlpha(t.RingOuter, 0.97f) : WithAlpha(t.HubBottom, 0.92f));
                _g.StrokeRound(l + 0.5f, top + 0.5f, r - 0.5f, bottom - 0.5f, RowH / 2, hov ? WithAlpha(t.Glow(0), 0.8f) : t.Stroke, 1f);
                _g.Text(text, _fmtRow, (l + r) / 2, y, r - l, RowH, hov ? t.Text : t.TextDim);
            }
        }
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
        D2D1_COLOR_F hue = t.Hue(i), glow = t.Glow(i);   // icon colour, light-up colour
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

        _g.SetColor(hov ? WithAlpha(glow, 0.65f) : t.Stroke);
        dc->DrawGeometry(geo, (ID2D1Brush*)_g.Brush, hov ? 1.2f : 1f, null);
        if (hov)
        {
            _g.SetColor(glow);
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
            // Colour tile + glyph, then the label (and the badge line under it, moved up to make room).
            string? badge = _slices[i].Badge;
            if (badge != null) iy -= 7;
            _g.FillRound(ix - 16, iy - 27, ix + 16, iy + 5, 10, WithAlpha(hue, hov ? t.TileAlphaHover : t.TileAlpha));
            if (hov) _g.StrokeRound(ix - 16, iy - 27, ix + 16, iy + 5, 10, WithAlpha(glow, 0.55f), 1f);
            if (_slices[i].Icon != null) DrawIcon(_slices[i].Icon, ix, iy - 11, 22);
            else _g.Text(_glyphs[i], _fmtGlyph, ix, iy - 11, 32, 32, hue);
            _g.Text(_slices[i].Label, _fmtLabel, ix, iy + 18, 110, 16, hov ? t.Text : t.TextDim);
            if (badge != null) _g.Text(badge, _fmtHubSub, ix, iy + 32, 100, 13, hov ? glow : t.TextFaint);
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
            if (s.Icon != null) DrawIcon(s.Icon, 0, -24, 24);
            else _g.Text(_glyphs[hover], _fmtGlyphHub, 0, -24, 40, 26, Theme.Hue(hover));
            _g.Text(s.Label, _fmtHub, 0, 2, 112, 20, t.Text);
            string detail = s.Badge is { } b && b.StartsWith('≈') && HubFrom != null ? $"{HubFrom} → {b[2..]}" : s.Badge ?? s.Detail;
            _g.Text(detail, _fmtHubSub, 0, 20, 112, 13, t.TextFaint);
        }
        else
        {
            _g.FillCircle(0, -22, 4f, accent);
            _g.Text(WheelName, _fmtHub, 0, 0, 112, 20, t.Text);
            int n = Math.Min(_slices.Length, 9);
            _g.Text(HubHint ?? (HasRow ? $"1–{n} · Tab wheels · Esc" : $"1–{n} · arrows · Esc"), _fmtHubSub, 0, 19, 118, 13, t.TextFaint);
        }
    }

    /// <summary>
    /// The outer band: a backplate, then one segment per item in the parent slice's colour. The hovered item
    /// fills with that colour and gets a bright inner edge, like a hovered slice.
    /// </summary>
    private void DrawRing(RingBand band, WheelView v)
    {
        var t = Theme;
        var dc = _g.Dc;
        var L = band.Layout;
        var hue = t.Glow(band.Parent);
        _g.SetColor(t.Light ? WithAlpha(t.RingOuter, 0.96f) : WithAlpha(t.HubBottom, 0.9f));
        dc->FillGeometry((ID2D1Geometry*)band.Back, (ID2D1Brush*)_g.Brush, null);

        float rMid = (L.InnerRadius + L.OuterRadius) / 2f;   // glyph above the label, like a slice: reads the same at any angle
        for (int i = 0; i < band.Items.Length; i++)
        {
            bool hov = i == v.RingHover;
            var geo = (ID2D1Geometry*)band.Geo[i];
            _g.SetColor(t.RingInner);
            dc->FillGeometry(geo, (ID2D1Brush*)_g.Brush, null);
            _g.SetColor(WithAlpha(hue, hov ? t.GlowAlpha + 0.08f : 0.07f));
            dc->FillGeometry(geo, (ID2D1Brush*)_g.Brush, null);
            _g.SetColor(hov ? WithAlpha(hue, 0.7f) : t.Stroke);
            dc->DrawGeometry(geo, (ID2D1Brush*)_g.Brush, hov ? 1.2f : 1f, null);
            if (hov)
            {
                _g.SetColor(hue);
                dc->FillGeometry((ID2D1Geometry*)band.Edge[i], (ID2D1Brush*)_g.Brush, null);
            }
            float a = L.CenterAngle(i), c = MathF.Cos(a), s = MathF.Sin(a);
            // Each item's icon in its own colour (offset from the parent's, so the first item isn't the slice's twin).
            if (band.Items[i].Icon != null) DrawIcon(band.Items[i].Icon, rMid * c, rMid * s - 9f, 20);
            else _g.Text(band.Glyphs[i], _fmtGlyph, rMid * c, rMid * s - 9f, 30, 24, hov ? t.Text : t.Hue(band.Parent + 1 + i));
            _g.Text(band.Items[i].Label, _fmtRingLabel, rMid * c, rMid * s + 14f, 70, 14, hov ? t.Text : t.TextDim);
        }
    }

    /// <summary>Hub text while a band is open: the hovered item, or the band's title and how to go back.</summary>
    private void DrawRingHub(RingBand band, int item, D2D1_COLOR_F accent)
    {
        var t = Theme;
        if (item >= 0)
        {
            var s = band.Items[item];
            if (s.Icon != null) DrawIcon(s.Icon, 0, -24, 24);
            else _g.Text(band.Glyphs[item], _fmtGlyphHub, 0, -24, 40, 26, Theme.Hue(band.Parent + 1 + item));
            _g.Text(s.Label, _fmtHub, 0, 2, 112, 20, t.Text);
            _g.Text(s.Detail, _fmtHubSub, 0, 20, 112, 13, t.TextFaint);
        }
        else
        {
            _g.FillCircle(0, -22, 4f, accent);
            _g.Text(band.Title, _fmtHub, 0, 0, 112, 20, t.Text);
            int n = Math.Min(band.Items.Length, 9);
            _g.Text(n > 1 ? $"1–{(n == 10 ? 0 : n)} · Esc back" : "Enter · Esc back", _fmtHubSub, 0, 19, 112, 13, t.TextFaint);
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

    private void DrawIcon(ID2D1Bitmap1* icon, float cx, float cy, float size)
    {
        var dest = new D2D_RECT_F { left = cx - size / 2, top = cy - size / 2, right = cx + size / 2, bottom = cy + size / 2 };
        _g.Dc->DrawBitmap((ID2D1Bitmap*)icon, &dest, 1f, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC, null, null);
    }

    /// <summary>
    /// The search box: the wheel dims and a bar across its middle shows what's being typed. Keys arrive
    /// through the keyboard hook, so the app underneath keeps focus the whole time.
    /// </summary>
    private void DrawQuery(WheelView v, D2D1_COLOR_F accent)
    {
        var t = Theme;
        _g.FillCircle(0, 0, Layout.OuterRadius + 2, t.Light ? C(0xFFFFFF, 0.55f) : C(0x000000, 0.50f));
        const float w = 300, h = 46, r = 23;
        _g.FillRound(-w / 2 - 1, -h / 2 + 3, w / 2 + 1, h / 2 + 5, r + 1, C(0x000000, t.Light ? 0.10f : 0.35f));
        _g.FillRound(-w / 2, -h / 2, w / 2, h / 2, r, t.Light ? C(0xFFFFFF) : C(0x10131F));
        _g.StrokeRound(-w / 2 + 0.5f, -h / 2 + 0.5f, w / 2 - 0.5f, h / 2 - 0.5f, r, WithAlpha(accent, 0.75f), 1.2f);
        float gx = -w / 2 + 24;
        _g.FillCircle(gx, 0, 14, WithAlpha(accent, 0.18f));
        _g.Text("", _fmtGlyph, gx, 0, 28, 28, accent);

        float tx = gx + 22, tw = w / 2 - 18 - tx;
        if (v.QueryText.Length == 0)
        {
            _g.Text($"Search {v.QueryEngine}", _fmtQuery, tx + tw / 2, 0, tw, 24, t.TextFaint);
            _g.Line(tx, -9, tx, 9, accent, 1.5f);
        }
        else
        {
            // Show the end of long queries, where the caret is.
            string text = v.QueryText;
            while (text.Length > 1 && _g.Measure(text, _fmtQuery) > tw - 4) text = text[1..];
            if (text.Length < v.QueryText.Length) text = "…" + text[1..];
            float width = _g.Measure(text, _fmtQuery);
            _g.Text(text, _fmtQuery, tx + tw / 2, 0, tw, 24, t.Text);
            _g.Line(tx + width + 1.5f, -9, tx + width + 1.5f, 9, accent, 1.5f);
        }
        float hy = h / 2 + 18;
        _g.FillRound(-86, hy - 10, 86, hy + 10, 10, t.Light ? C(0xFFFFFF, 0.9f) : C(0x000000, 0.55f));
        _g.Text("Enter to search · Esc to go back", _fmtQueryHint, 0, hy, 172, 16, t.Light ? t.TextDim : C(0xFFFFFF, 0.8f));
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
