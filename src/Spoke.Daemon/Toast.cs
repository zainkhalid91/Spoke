using System.Drawing;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;
using static Spoke.Daemon.Gfx;

namespace Spoke.Daemon;

/// <summary>
/// A small confirmation card ("Copied 3b82f6", "Screenshot copied") at the bottom of the current monitor.
/// Never takes focus, fades in inside DWM, hides itself after a couple of seconds; click to dismiss.
/// </summary>
internal sealed unsafe class Toast
{
    public const nuint TimerId = 21;
    private const float W = 360, Margin = 18;   // DIPs (card + shadow margin)
    private float H = 66;                      // 66 for image/colour cards, taller for wrapped notices
    private IDWriteTextFormat* _fmtSubWrap;

    public readonly HWND Hwnd;
    private readonly Gfx _g;
    private readonly App _app;
    private readonly Surface _surface;
    private readonly IDWriteTextFormat* _fmtTitle, _fmtSub, _fmtGlyph;

    private ID2D1Bitmap1* _image;
    private ID2D1BitmapBrush1* _imageBrush;
    private int _imgW, _imgH;

    public Toast(Gfx g, App app, HWND hwnd)
    {
        _g = g;
        _app = app;
        Hwnd = hwnd;
        _surface = new Surface(g, hwnd);
        _fmtTitle = g.Format("Segoe UI Variable Text", 13f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING);
        _fmtSub = g.Format("Segoe UI Variable Text", 11f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING);
        _fmtGlyph = g.Format(g.IconFont, 14f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
        _fmtSubWrap = g.Format("Segoe UI Variable Text", 11f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING);
        _fmtSubWrap->SetWordWrapping(DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_WRAP);
        _fmtSubWrap->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT.DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
    }

    public void ShowColor(Color c, string title, string sub) => Show(title, sub, swatch: c, image: null);

    public void ShowImage(Pixels thumbPremultiplied, string title, string sub) => Show(title, sub, swatch: null, image: thumbPremultiplied);

    /// <summary>A text notice: a check for confirmations, a warning for problems (shown longer).</summary>
    public void ShowMessage(string title, string sub, bool error) => Show(title, sub, swatch: null, image: null, message: true, error: error);

    private bool _message, _error;

    private void Show(string title, string sub, Color? swatch, Pixels? image, bool message = false, bool error = false)
    {
        _message = message;
        _error = error;
        H = message ? 92 : 66;
        ReleaseImage();
        if (image != null)
        {
            fixed (byte* d = image.Data) _image = _g.Bitmap(d, image.Width, image.Height, image.Stride);
            _imageBrush = _g.BitmapBrush(_image);
            _imgW = image.Width; _imgH = image.Height;
        }

        PInvoke.GetCursorPos(out var pt);
        HMONITOR mon = PInvoke.MonitorFromPoint(pt, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        PInvoke.GetMonitorInfo(mon, &mi);
        uint dpi, dy;
        if (PInvoke.GetDpiForMonitor(mon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dpi, &dy).Failed) dpi = 96;
        float s = dpi / 96f;
        int pw = (int)MathF.Ceiling((W + Margin * 2) * s), ph = (int)MathF.Ceiling((H + Margin * 2) * s);
        var work = mi.rcWork;
        int x = (work.left + work.right) / 2 - pw / 2, y = work.bottom - ph - (int)(20 * s);

        _surface.Resize(pw, ph, dpi);
        Draw(title, sub, swatch);
        _surface.AnimateIn(pw / 2f, ph / 2f, 160);
        _g.DComp->Commit();
        PInvoke.SetWindowPos(Hwnd, HWND.HWND_TOPMOST, x, y, pw, ph, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        PInvoke.SetTimer(Hwnd, TimerId, error ? 6000u : 2400u, null);
    }

    private void Draw(string title, string sub, Color? swatch)
    {
        if (!_surface.Begin()) return;
        var t = _app.Renderer.Theme;
        _g.SetTranslate(Margin, Margin);
        var bg = t.Light ? C(0xFFFFFF, 0.98f) : C(0x0E1222, 0.96f);
        _g.FillRound(-2, 2, W + 2, H + 6, 18, C(0x000000, t.Light ? 0.10f : 0.30f));
        _g.FillRound(0, 0, W, H, 16, bg);
        _g.StrokeRound(0.5f, 0.5f, W - 0.5f, H - 0.5f, 16, t.Light ? C(0x000000, 0.08f) : C(0xFFFFFF, 0.10f), 1);

        const float pad = 11; float side = H - pad * 2;
        if (_message)
        {
            // Text notice: a round badge on the left, text across the rest of the card.
            var tone = _error ? C(0xFF6B6B) : t.Hue(3);
            _g.FillCircle(pad + 19, 30, 17, WithAlpha(tone, 0.16f));
            _g.Text(_error ? "" : "", _fmtGlyph, pad + 19, 30, 30, 30, tone);
            float mx = pad + 38 + 12, mw = W - mx - 16;
            _g.Text(title, _fmtTitle, mx + mw / 2, 25, mw, 18, t.Text);
            _g.Text(sub, _fmtSubWrap, mx + mw / 2, 36 + 22, mw, 44, t.TextDim);   // up to three lines
            _surface.End();
            return;
        }
        if (_imageBrush != null)
        {
            // Image thumbnail, centre-cropped into a rounded square.
            float sc = MathF.Max(side / _imgW, side / _imgH);
            var m = Matrix(sc, 0, 0, sc, pad + (side - _imgW * sc) / 2, pad + (side - _imgH * sc) / 2);
            ((ID2D1Brush*)_imageBrush)->SetTransform(&m);
            var rr = new D2D1_ROUNDED_RECT { rect = new D2D_RECT_F { left = pad, top = pad, right = pad + side, bottom = pad + side }, radiusX = 9, radiusY = 9 };
            _g.Dc->FillRoundedRectangle(&rr, (ID2D1Brush*)_imageBrush);
        }
        else if (swatch is { } c)
        {
            _g.FillRound(pad, pad, pad + side, pad + side, 10, C((uint)(c.R << 16 | c.G << 8 | c.B)));
        }
        _g.StrokeRound(pad + 0.5f, pad + 0.5f, pad + side - 0.5f, pad + side - 0.5f, 9.5f, t.Light ? C(0x000000, 0.10f) : C(0xFFFFFF, 0.14f), 1);

        float tx = pad + side + 14, tw = W - tx - 40;
        _g.Text(title, _fmtTitle, tx + tw / 2, H / 2 - 9, tw, 18, t.Text);
        _g.Text(sub, _fmtSub, tx + tw / 2, H / 2 + 10, tw, 16, t.TextDim);
        // A check in the accent colour.
        _g.FillCircle(W - 26, H / 2, 11, WithAlpha(t.Hue(3), 0.18f));
        _g.Text(''.ToString(), _fmtGlyph, W - 26, H / 2, 22, 22, t.Hue(3));
        _surface.End();
    }

    public bool Handle(uint msg, WPARAM wParam, out LRESULT result)
    {
        result = default;
        switch (msg)
        {
            case PInvoke.WM_MOUSEACTIVATE:
                result = (LRESULT)(nint)PInvoke.MA_NOACTIVATE;
                return true;
            case PInvoke.WM_LBUTTONUP:
                Hide();
                return true;
            case PInvoke.WM_TIMER when wParam.Value == TimerId:
                Hide();
                return true;
        }
        return false;
    }

    public void Hide()
    {
        PInvoke.KillTimer(Hwnd, TimerId);
        PInvoke.SetWindowPos(Hwnd, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_HIDEWINDOW | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
        _surface.ReleaseSwapChain();
        _g.DComp->Commit();
        ReleaseImage();
    }

    private void ReleaseImage()
    {
        Release(_imageBrush); _imageBrush = null;
        Release(_image); _image = null;
    }
}
