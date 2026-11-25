using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.DirectComposition;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Spoke.Daemon;

/// <summary>
/// The one GPU device everything draws with: D3D11 → D2D device context, DirectWrite and a DirectComposition
/// device. UI thread only (the D2D factory is single-threaded). Created once at start-up.
/// </summary>
internal sealed unsafe class Gfx
{
    public ID3D11Device* D3D;
    public IDXGIDevice* Dxgi;
    public ID2D1Factory1* Factory;
    public ID2D1Device* Device;
    public ID2D1DeviceContext* Dc;
    public IDWriteFactory* DW;
    public IDCompositionDevice* DComp;
    public ID2D1SolidColorBrush* Brush;

    public bool UsingWarp { get; private set; }
    public string AdapterName { get; private set; } = "";
    public string IconFont { get; private set; } = "Segoe Fluent Icons";

    public Gfx(bool forceWarp)
    {
        ID3D11Device* d3d = null;
        var flags = D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        HRESULT hr = forceWarp ? new HRESULT(-1) :
            PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, default, flags, null, 0, PInvoke.D3D11_SDK_VERSION, &d3d, null, null);
        if (hr.Failed)
        {
            PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, default, flags, null, 0, PInvoke.D3D11_SDK_VERSION, &d3d, null, null).ThrowOnFailure();
            UsingWarp = true;
        }
        D3D = d3d;
        Guid iid = IDXGIDevice.IID_Guid;
        IDXGIDevice* dxgi;
        ((IUnknown*)D3D)->QueryInterface(&iid, (void**)&dxgi).ThrowOnFailure();
        Dxgi = dxgi;
        IDXGIAdapter* adapter;
        Dxgi->GetAdapter(&adapter);
        DXGI_ADAPTER_DESC desc = adapter->GetDesc();
        AdapterName = desc.Description.ToString().TrimEnd((char)0);
        ((IUnknown*)adapter)->Release();

        Guid iidF1 = ID2D1Factory1.IID_Guid;
        ID2D1Factory1* f;
        PInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, &iidF1, null, (void**)&f).ThrowOnFailure();
        Factory = f;
        ID2D1Device* dev;
        Factory->CreateDevice(Dxgi, &dev);
        Device = dev;
        ID2D1DeviceContext* dc;
        Device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS.D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &dc);
        Dc = dc;
        Dc->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);

        Guid iidDw = IDWriteFactory.IID_Guid;
        IDWriteFactory* dw;
        PInvoke.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, &iidDw, (void**)&dw).ThrowOnFailure();
        DW = dw;
        if (!File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "SegoeIcons.ttf")))
            IconFont = "Segoe MDL2 Assets";   // Windows 10

        var white = new D2D1_COLOR_F { r = 1, g = 1, b = 1, a = 1 };
        ID2D1SolidColorBrush* b;
        Dc->CreateSolidColorBrush(&white, null, &b);
        Brush = b;

        Guid iidDc = IDCompositionDevice.IID_Guid;
        IDCompositionDevice* dcomp;
        PInvoke.DCompositionCreateDevice(Dxgi, &iidDc, (void**)&dcomp).ThrowOnFailure();
        DComp = dcomp;
    }

    // ───────────── text ─────────────

    public IDWriteTextFormat* Format(string family, float size, DWRITE_FONT_WEIGHT weight,
        DWRITE_TEXT_ALIGNMENT align = DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER)
    {
        IDWriteTextFormat* fmt;
        fixed (char* fam = family)
        fixed (char* locale = "en-us")
            DW->CreateTextFormat(fam, null, weight, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, size, locale, &fmt);
        fmt->SetTextAlignment(align);
        fmt->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_CENTER);
        fmt->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP);
        return fmt;
    }

    /// <summary>Draws text centred on (cx, cy) in a w×h box (left-aligned formats start at cx - w/2).</summary>
    public void Text(string s, IDWriteTextFormat* fmt, float cx, float cy, float w, float h, D2D1_COLOR_F color)
    {
        if (s.Length == 0) return;
        SetColor(color);
        var origin = new D2D_POINT_2F { x = cx - w / 2, y = cy - h / 2 };
        Dc->DrawTextLayout(origin, Layout(s, fmt, w, h), (ID2D1Brush*)Brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_NONE);
    }

    // Wheel text is a small fixed set (labels, numbers, hub lines), so laying each out once saves the
    // shaping work DrawText would repeat every frame. Capped; cleared wholesale if something churns it.
    private readonly Dictionary<(string, nint, float, float), nint> _layouts = new();
    private const int MaxLayouts = 256;

    private IDWriteTextLayout* Layout(string s, IDWriteTextFormat* fmt, float w, float h)
    {
        var key = (s, (nint)fmt, w, h);
        if (_layouts.TryGetValue(key, out var cached)) return (IDWriteTextLayout*)cached;
        if (_layouts.Count >= MaxLayouts) ClearLayouts();
        IDWriteTextLayout* layout;
        fixed (char* p = s)
            DW->CreateTextLayout(p, (uint)s.Length, fmt, w, h, &layout);
        _layouts[key] = (nint)layout;
        return layout;
    }

    public void ClearLayouts()
    {
        foreach (var l in _layouts.Values) ((IUnknown*)l)->Release();
        _layouts.Clear();
    }

    /// <summary>Width of a single line of text in DIPs.</summary>
    public float Measure(string s, IDWriteTextFormat* fmt)
    {
        if (s.Length == 0) return 0;
        IDWriteTextLayout* layout;
        fixed (char* p = s)
            DW->CreateTextLayout(p, (uint)s.Length, fmt, 4000, 100, &layout);
        DWRITE_TEXT_METRICS m;
        layout->GetMetrics(&m);
        ((IUnknown*)layout)->Release();
        return m.widthIncludingTrailingWhitespace;
    }

    // ───────────── shapes ─────────────

    public void SetColor(D2D1_COLOR_F c) => Brush->SetColor(&c);

    public void FillCircle(float x, float y, float r, D2D1_COLOR_F c) => FillCircle(x, y, r, (ID2D1Brush*)Brush, c);

    public void FillCircle(float x, float y, float r, ID2D1Brush* brush, D2D1_COLOR_F? c = null)
    {
        if (c is { } col) SetColor(col);
        var e = new D2D1_ELLIPSE { point = new D2D_POINT_2F { x = x, y = y }, radiusX = r, radiusY = r };
        Dc->FillEllipse(&e, brush);
    }

    public void StrokeCircle(float x, float y, float r, D2D1_COLOR_F c, float width)
    {
        SetColor(c);
        var e = new D2D1_ELLIPSE { point = new D2D_POINT_2F { x = x, y = y }, radiusX = r, radiusY = r };
        Dc->DrawEllipse(&e, (ID2D1Brush*)Brush, width, null);
    }

    public void FillRound(float l, float t, float r, float b, float radius, D2D1_COLOR_F c)
    {
        SetColor(c);
        var rr = new D2D1_ROUNDED_RECT { rect = new D2D_RECT_F { left = l, top = t, right = r, bottom = b }, radiusX = radius, radiusY = radius };
        Dc->FillRoundedRectangle(&rr, (ID2D1Brush*)Brush);
    }

    public void StrokeRound(float l, float t, float r, float b, float radius, D2D1_COLOR_F c, float width)
    {
        SetColor(c);
        var rr = new D2D1_ROUNDED_RECT { rect = new D2D_RECT_F { left = l, top = t, right = r, bottom = b }, radiusX = radius, radiusY = radius };
        Dc->DrawRoundedRectangle(&rr, (ID2D1Brush*)Brush, width, null);
    }

    public void FillRect(float l, float t, float r, float b, D2D1_COLOR_F c)
    {
        SetColor(c);
        var rc = new D2D_RECT_F { left = l, top = t, right = r, bottom = b };
        Dc->FillRectangle(&rc, (ID2D1Brush*)Brush);
    }

    public void StrokeRect(float l, float t, float r, float b, D2D1_COLOR_F c, float width)
    {
        SetColor(c);
        var rc = new D2D_RECT_F { left = l, top = t, right = r, bottom = b };
        Dc->DrawRectangle(&rc, (ID2D1Brush*)Brush, width, null);
    }

    public void Line(float x1, float y1, float x2, float y2, D2D1_COLOR_F c, float width)
    {
        SetColor(c);
        Dc->DrawLine(new D2D_POINT_2F { x = x1, y = y1 }, new D2D_POINT_2F { x = x2, y = y2 }, (ID2D1Brush*)Brush, width, null);
    }

    public void SetTransform(float m11, float m12, float m21, float m22, float dx, float dy)
    {
        D2D_MATRIX_3X2_F m;
        float* f = (float*)&m;
        f[0] = m11; f[1] = m12; f[2] = m21; f[3] = m22; f[4] = dx; f[5] = dy;
        Dc->SetTransform(&m);
    }

    public void SetTranslate(float dx, float dy) => SetTransform(1, 0, 0, 1, dx, dy);

    public static D2D_MATRIX_3X2_F Matrix(float m11, float m12, float m21, float m22, float dx, float dy)
    {
        D2D_MATRIX_3X2_F m;
        float* f = (float*)&m;
        f[0] = m11; f[1] = m12; f[2] = m21; f[3] = m22; f[4] = dx; f[5] = dy;
        return m;
    }

    /// <summary>A ring segment between two radii and two angles, centred on (0,0).</summary>
    public ID2D1PathGeometry* Annulus(float r1, float r2, float a1, float a2)
    {
        ID2D1PathGeometry* g;
        Factory->CreatePathGeometry(&g);
        ID2D1GeometrySink* s;
        g->Open(&s);
        s->BeginFigure(P(r2, a1), D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED);
        var outer = new D2D1_ARC_SEGMENT { point = P(r2, a2), size = new D2D_SIZE_F { width = r2, height = r2 }, sweepDirection = D2D1_SWEEP_DIRECTION.D2D1_SWEEP_DIRECTION_CLOCKWISE, arcSize = D2D1_ARC_SIZE.D2D1_ARC_SIZE_SMALL };
        s->AddArc(&outer);
        s->AddLine(P(r1, a2));
        var inner = new D2D1_ARC_SEGMENT { point = P(r1, a1), size = new D2D_SIZE_F { width = r1, height = r1 }, sweepDirection = D2D1_SWEEP_DIRECTION.D2D1_SWEEP_DIRECTION_COUNTER_CLOCKWISE, arcSize = D2D1_ARC_SIZE.D2D1_ARC_SIZE_SMALL };
        s->AddArc(&inner);
        s->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED);
        s->Close();
        ((IUnknown*)s)->Release();
        return g;
    }

    public ID2D1EllipseGeometry* Ellipse(float x, float y, float r)
    {
        var e = new D2D1_ELLIPSE { point = new D2D_POINT_2F { x = x, y = y }, radiusX = r, radiusY = r };
        ID2D1EllipseGeometry* g;
        Factory->CreateEllipseGeometry(&e, &g);
        return g;
    }

    public static D2D_POINT_2F P(float r, float a) => new() { x = r * MathF.Cos(a), y = r * MathF.Sin(a) };

    // ───────────── gradients ─────────────

    public ID2D1GradientStopCollection* Stops(params ReadOnlySpan<(float Pos, D2D1_COLOR_F Color)> stops)
    {
        var arr = stackalloc D2D1_GRADIENT_STOP[stops.Length];
        for (int i = 0; i < stops.Length; i++) arr[i] = new D2D1_GRADIENT_STOP { position = stops[i].Pos, color = stops[i].Color };
        ID2D1GradientStopCollection* c;
        ((ID2D1RenderTarget*)Dc)->CreateGradientStopCollection(arr, (uint)stops.Length, D2D1_GAMMA.D2D1_GAMMA_2_2, D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP, &c);
        return c;
    }

    public ID2D1RadialGradientBrush* Radial(float cx, float cy, float r, params ReadOnlySpan<(float Pos, D2D1_COLOR_F Color)> stops)
    {
        var col = Stops(stops);
        var props = new D2D1_RADIAL_GRADIENT_BRUSH_PROPERTIES { center = new D2D_POINT_2F { x = cx, y = cy }, radiusX = r, radiusY = r };
        ID2D1RadialGradientBrush* b;
        ((ID2D1RenderTarget*)Dc)->CreateRadialGradientBrush(&props, null, col, &b);
        ((IUnknown*)col)->Release();
        return b;
    }

    public ID2D1LinearGradientBrush* Linear(float x1, float y1, float x2, float y2, params ReadOnlySpan<(float Pos, D2D1_COLOR_F Color)> stops)
    {
        var col = Stops(stops);
        var props = new D2D1_LINEAR_GRADIENT_BRUSH_PROPERTIES { startPoint = new D2D_POINT_2F { x = x1, y = y1 }, endPoint = new D2D_POINT_2F { x = x2, y = y2 } };
        ID2D1LinearGradientBrush* b;
        ((ID2D1RenderTarget*)Dc)->CreateLinearGradientBrush(&props, null, col, &b);
        ((IUnknown*)col)->Release();
        return b;
    }

    // ───────────── bitmaps ─────────────

    /// <summary>Uploads premultiplied BGRA pixels to a GPU bitmap (ignoreAlpha for screen grabs).</summary>
    public ID2D1Bitmap1* Bitmap(void* pixels, int w, int h, int stride, bool ignoreAlpha = false)
    {
        var props = new D2D1_BITMAP_PROPERTIES1
        {
            pixelFormat = new D2D1_PIXEL_FORMAT
            {
                format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                alphaMode = ignoreAlpha ? D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_IGNORE : D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED,
            },
            dpiX = 96, dpiY = 96,
        };
        ID2D1Bitmap1* bmp;
        Dc->CreateBitmap(new D2D_SIZE_U { width = (uint)w, height = (uint)h }, pixels, (uint)stride, &props, &bmp);
        return bmp;
    }

    public ID2D1BitmapBrush1* BitmapBrush(ID2D1Bitmap1* bmp, bool nearest = false)
    {
        var props = new D2D1_BITMAP_BRUSH_PROPERTIES1
        {
            extendModeX = D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP,
            extendModeY = D2D1_EXTEND_MODE.D2D1_EXTEND_MODE_CLAMP,
            interpolationMode = nearest ? D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR : D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_HIGH_QUALITY_CUBIC,
        };
        ID2D1BitmapBrush1* b;
        Dc->CreateBitmapBrush((ID2D1Bitmap*)bmp, &props, null, &b);
        return b;
    }

    public static void Release(void* p)
    {
        if (p != null) ((IUnknown*)p)->Release();
    }

    public static D2D1_COLOR_F C(uint rgb, float a = 1f) =>
        new() { r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, b = (rgb & 0xFF) / 255f, a = a };

    public static D2D1_COLOR_F WithAlpha(D2D1_COLOR_F c, float a) => new() { r = c.r, g = c.g, b = c.b, a = a };

    public static D2D1_COLOR_F Mix(D2D1_COLOR_F x, D2D1_COLOR_F y, float t) =>
        new() { r = x.r + (y.r - x.r) * t, g = x.g + (y.g - x.g) * t, b = x.b + (y.b - x.b) * t, a = x.a + (y.a - x.a) * t };
}

/// <summary>
/// One window's DirectComposition tree: target → root visual (offset, open animation) → swap chain.
/// The swap chain is created on demand and can be released while hidden to give the memory back.
/// </summary>
internal sealed unsafe class Surface
{
    private readonly Gfx _g;
    private IDCompositionTarget* _target;
    public IDCompositionVisual* Visual;
    private IDXGISwapChain1* _swap;
    private ID2D1Bitmap1* _bmp;
    private IDCompositionEffectGroup* _fx;
    private IDCompositionScaleTransform3D* _scale;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint Dpi { get; private set; }
    public bool HasSwapChain => _swap != null;

    public Surface(Gfx g, HWND hwnd)
    {
        _g = g;
        IDCompositionTarget* t;
        g.DComp->CreateTargetForHwnd(hwnd, true, &t);
        _target = t;
        IDCompositionVisual* v;
        g.DComp->CreateVisual(&v);
        Visual = v;
        _target->SetRoot(Visual);
    }

    /// <summary>Creates or resizes the swap chain (physical pixels) and sets the drawing DPI.</summary>
    public void Resize(int w, int h, uint dpi)
    {
        if (_swap != null && w == Width && h == Height)
        {
            Dpi = dpi;
            return;
        }
        Width = w; Height = h; Dpi = dpi;
        ReleaseTarget();
        if (_swap == null)
        {
            IDXGIAdapter* adapter;
            _g.Dxgi->GetAdapter(&adapter);
            Guid iidF2 = IDXGIFactory2.IID_Guid;
            IDXGIFactory2* fac;
            ((IDXGIObject*)adapter)->GetParent(&iidF2, (void**)&fac);
            var desc = new DXGI_SWAP_CHAIN_DESC1
            {
                Width = (uint)w,
                Height = (uint)h,
                Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                BufferUsage = DXGI_USAGE.DXGI_USAGE_RENDER_TARGET_OUTPUT,
                BufferCount = 2,
                Scaling = DXGI_SCALING.DXGI_SCALING_STRETCH,
                SwapEffect = DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                AlphaMode = DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED,
            };
            IDXGISwapChain1* swap;
            fac->CreateSwapChainForComposition((IUnknown*)_g.D3D, &desc, null, &swap);
            _swap = swap;
            ((IUnknown*)fac)->Release();
            ((IUnknown*)adapter)->Release();
            Visual->SetContent((IUnknown*)_swap);
        }
        else
        {
            _swap->ResizeBuffers(2, (uint)w, (uint)h, DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, 0);
        }
    }

    private void ReleaseTarget()
    {
        if (_bmp != null)
        {
            _g.Dc->SetTarget(null);
            Gfx.Release(_bmp);
            _bmp = null;
        }
    }

    /// <summary>Starts drawing into the back buffer. Returns false if there is no swap chain.</summary>
    public bool Begin()
    {
        if (_swap == null) return false;
        if (_bmp == null)
        {
            Guid iidSurf = IDXGISurface.IID_Guid;
            IDXGISurface* surface;
            _swap->GetBuffer(0, &iidSurf, (void**)&surface);
            var props = new D2D1_BITMAP_PROPERTIES1
            {
                pixelFormat = new D2D1_PIXEL_FORMAT { format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM, alphaMode = D2D1_ALPHA_MODE.D2D1_ALPHA_MODE_PREMULTIPLIED },
                dpiX = 96, dpiY = 96,
                bitmapOptions = D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS.D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            };
            ID2D1Bitmap1* bmp;
            _g.Dc->CreateBitmapFromDxgiSurface(surface, &props, &bmp);
            ((IUnknown*)surface)->Release();
            _bmp = bmp;
        }
        _g.Dc->SetTarget((ID2D1Image*)_bmp);
        _g.Dc->SetDpi(Dpi, Dpi);
        _g.Dc->BeginDraw();
        var clear = new D2D1_COLOR_F();
        _g.Dc->Clear(&clear);
        return true;
    }

    public void End()
    {
        // D3D11 keeps buffer 0 pointing at the current back buffer across flips, so the D2D target stays bound.
        HRESULT hr = _g.Dc->EndDraw(null, null);
        hr = hr.Succeeded ? _swap->Present(0, 0) : hr;
        if (hr.Failed) Log.Write($"Present failed 0x{hr.Value:X8}");
    }

    public void SetOffset(float x, float y)
    {
        Visual->SetOffsetX(x);
        Visual->SetOffsetY(y);
    }

    /// <summary>
    /// A short scale + fade-in that runs inside DWM (no extra frames from us). Starts visible (opacity 0.35,
    /// 94% size) so it never delays the first frame. Skipped when Windows animations are turned off.
    /// </summary>
    public void AnimateIn(float centerX, float centerY, float ms = 140)
    {
        if (!Animations.Enabled) return;
        if (_fx == null)
        {
            IDCompositionEffectGroup* fx;
            _g.DComp->CreateEffectGroup(&fx);
            _fx = fx;
            IDCompositionScaleTransform3D* s;
            _g.DComp->CreateScaleTransform3D(&s);
            _scale = s;
            _fx->SetTransform3D((IDCompositionTransform3D*)_scale);
            Visual->SetEffect((IDCompositionEffect*)_fx);
        }
        _scale->SetCenterX(centerX);
        _scale->SetCenterY(centerY);
        var sc = EaseOut(0.94f, 1f, ms);
        var op = EaseOut(0.35f, 1f, ms * 0.8f);
        _scale->SetScaleX(sc);
        _scale->SetScaleY(sc);
        _fx->SetOpacity(op);
        Gfx.Release(sc);
        Gfx.Release(op);
    }

    /// <summary>Cubic ease-out from a to b: v(t) = a + d·(3u − 3u² + u³), u = t/T.</summary>
    private IDCompositionAnimation* EaseOut(float a, float b, float ms)
    {
        IDCompositionAnimation* anim;
        _g.DComp->CreateAnimation(&anim);
        double T = ms / 1000.0;
        float d = b - a;
        anim->AddCubic(0, a, (float)(3 * d / T), (float)(-3 * d / (T * T)), (float)(d / (T * T * T)));
        anim->End(T, b);
        return anim;
    }

    /// <summary>Drops the swap chain (e.g. a full-screen capture overlay) to give GPU memory back.</summary>
    public void ReleaseSwapChain()
    {
        ReleaseTarget();
        if (_swap == null) return;
        Visual->SetContent(null);
        Gfx.Release(_swap);
        _swap = null;
        Width = Height = 0;
    }
}

internal static unsafe class Animations
{
    /// <summary>Honours Settings → Accessibility → Visual effects → Animation effects.</summary>
    public static bool Enabled
    {
        get
        {
            BOOL on = true;
            PInvoke.SystemParametersInfo(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCLIENTAREAANIMATION, 0, &on, 0);
            return on;
        }
    }
}
