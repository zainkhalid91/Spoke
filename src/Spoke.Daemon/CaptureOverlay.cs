using System.Drawing;
using Spoke.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using static Spoke.Daemon.Gfx;

namespace Spoke.Daemon;

internal enum CaptureMode { Screenshot, ColorPicker }

/// <summary>
/// Full-screen tool overlay over a frozen frame of every monitor: region/window/screen screenshots and the
/// colour picker (loupe + formats, after PowerToys Color Picker). Like the wheel, it never takes focus: the
/// keyboard comes from <see cref="KeyHook"/>, so the app you were in stays focused and pastes land there.
/// Its full-screen swap chain and the frozen frame exist only while it is open.
/// </summary>
internal sealed unsafe class CaptureOverlay
{
    public readonly HWND Hwnd;
    private readonly Gfx _g;
    private readonly App _app;
    private readonly Surface _surface;
    private readonly IDWriteTextFormat* _fmtPill, _fmtMono, _fmtMonoBold, _fmtSmall, _fmtGlyph;

    public bool IsOpen { get; private set; }
    public CaptureMode Mode { get; private set; }

    private ScreenGrab? _grab;
    private ID2D1Bitmap1* _frame;
    private ID2D1BitmapBrush1* _loupeBrush;
    private ID2D1EllipseGeometry* _loupeGeo;
    private List<RECT> _windows = [];

    // Pointer state, screen px.
    private int _mx, _my;
    private bool _dragging, _hasDrag;
    private int _dx0, _dy0;
    private float _scale = 1f;       // DIP → px for the monitor under the pointer
    private RECT _monitor;           // that monitor's rect
    private int _zoomIndex = 2;
    private static readonly int[] ZoomSteps = [5, 7, 10, 14, 19, 26];

    // Colour picker.
    private ColorPickerConfig _colorCfg = ColorPickerConfig.Default;
    private ColorHistory? _historyLazy;
    /// <summary>Loaded on first use, so the JSON parse costs nothing until the picker is opened.</summary>
    private ColorHistory _history
    {
        get
        {
            if (_historyLazy == null)
            {
                _historyLazy = new ColorHistory(Path.Combine(Settings.Dir, "colors.json"));
                _historyLazy.Load(Path.Combine(Path.GetDirectoryName(ColorPickerConfig.DefaultPowerToysPath)!, "colorHistory.json"));
            }
            return _historyLazy;
        }
    }

    private static readonly VIRTUAL_KEY[] Keys =
    [
        VIRTUAL_KEY.VK_ESCAPE, VIRTUAL_KEY.VK_RETURN, VIRTUAL_KEY.VK_SPACE,
        VIRTUAL_KEY.VK_LEFT, VIRTUAL_KEY.VK_RIGHT, VIRTUAL_KEY.VK_UP, VIRTUAL_KEY.VK_DOWN, .. KeyHook.Digits,
    ];

    public CaptureOverlay(Gfx g, App app, HWND hwnd)
    {
        _g = g;
        _app = app;
        Hwnd = hwnd;
        _surface = new Surface(g, hwnd);
        _fmtPill = g.Format("Segoe UI Variable Text", 12f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
        _fmtMono = g.Format("Cascadia Mono", 12f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL, DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING);
        _fmtMonoBold = g.Format("Cascadia Mono", 12f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING);
        _fmtSmall = g.Format("Cascadia Mono", 9.5f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
        _fmtGlyph = g.Format(g.IconFont, 13f, DWRITE_FONT_WEIGHT.DWRITE_FONT_WEIGHT_NORMAL);
    }

    private Theme T => _app.Renderer.Theme;

    // ───────────── open / close ─────────────

    public void Begin(CaptureMode mode)
    {
        if (IsOpen) End();
        Mode = mode;
        if (mode == CaptureMode.ColorPicker) _colorCfg = ColorPickerConfig.Load();   // follows PowerToys changes

        // The wheel was just hidden: wait for DWM to compose a frame without it before grabbing the screen.
        PInvoke.DwmFlush();
        PInvoke.DwmFlush();
        _grab = ScreenGrab.Capture();
        _frame = _g.Bitmap(_grab.Bits, _grab.Width, _grab.Height, _grab.Stride, ignoreAlpha: true);
        _loupeBrush = _g.BitmapBrush(_frame, nearest: true);
        _windows = mode == CaptureMode.Screenshot ? ScreenGrab.VisibleWindows() : [];

        _surface.Resize(_grab.Width, _grab.Height, 96);
        PInvoke.GetCursorPos(out var pt);
        _mx = pt.X; _my = pt.Y;
        _dragging = _hasDrag = false;
        UpdateMonitor();
        Draw();
        _g.DComp->Commit();
        PInvoke.SetWindowPos(Hwnd, HWND.HWND_TOPMOST, _grab.X, _grab.Y, _grab.Width, _grab.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        IsOpen = true;
        KeyHook.Install(Hwnd, Keys);
    }

    public void End()
    {
        if (!IsOpen) return;
        IsOpen = false;
        KeyHook.Uninstall();
        if (_dragging) PInvoke.ReleaseCapture();
        _dragging = false;
        PInvoke.SetWindowPos(Hwnd, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_HIDEWINDOW | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
        // Give the full-screen buffers back right away.
        _surface.ReleaseSwapChain();
        _g.DComp->Commit();
        Release(_loupeBrush); _loupeBrush = null;
        Release(_loupeGeo); _loupeGeo = null;
        Release(_frame); _frame = null;
        _grab?.Dispose(); _grab = null;
        _windows = [];
        _app.ScheduleTrim();
    }

    // ───────────── input ─────────────

    public bool Handle(uint msg, WPARAM wParam, LPARAM lParam, out LRESULT result)
    {
        result = default;
        if (!IsOpen) return false;
        switch (msg)
        {
            case PInvoke.WM_MOUSEACTIVATE:
                result = (LRESULT)(nint)PInvoke.MA_NOACTIVATE;
                return true;
            case PInvoke.WM_SETCURSOR:
                PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_CROSS));
                result = (LRESULT)1;
                return true;
            case PInvoke.WM_MOUSEMOVE:
                PInvoke.GetCursorPos(out var pt);
                MoveTo(pt.X, pt.Y);
                return true;
            case PInvoke.WM_LBUTTONDOWN when Mode == CaptureMode.Screenshot:
                _dragging = true;
                _hasDrag = false;
                _dx0 = _mx; _dy0 = _my;
                PInvoke.SetCapture(Hwnd);
                return true;
            case PInvoke.WM_LBUTTONUP:
                if (Mode == CaptureMode.ColorPicker) { PickColor(0); return true; }
                if (_dragging)
                {
                    PInvoke.ReleaseCapture();
                    _dragging = false;
                    FinishScreenshot(_hasDrag ? DragRect() : WindowOrMonitorAt(_mx, _my));
                }
                return true;
            case PInvoke.WM_RBUTTONUP:
                End();
                return true;
            case PInvoke.WM_MOUSEWHEEL when Mode == CaptureMode.ColorPicker:
                short delta = (short)((wParam.Value >> 16) & 0xFFFF);
                _zoomIndex = Math.Clamp(_zoomIndex + (delta > 0 ? 1 : -1), 0, ZoomSteps.Length - 1);
                Draw();
                return true;
            case KeyHook.WM_APP_KEY:
                OnKey((VIRTUAL_KEY)(ushort)wParam.Value);
                return true;
            case KeyHook.WM_APP_FG:
                End();   // something else took the foreground (Alt+Tab, a notification click…)
                return true;
        }
        return false;
    }

    private void OnKey(VIRTUAL_KEY vk)
    {
        int step = KeyHook.IsDown(VIRTUAL_KEY.VK_SHIFT) ? 10 : 1;
        switch (vk)
        {
            case VIRTUAL_KEY.VK_ESCAPE: End(); return;
            case VIRTUAL_KEY.VK_LEFT: Nudge(-step, 0); return;
            case VIRTUAL_KEY.VK_RIGHT: Nudge(step, 0); return;
            case VIRTUAL_KEY.VK_UP: Nudge(0, -step); return;
            case VIRTUAL_KEY.VK_DOWN: Nudge(0, step); return;
            case VIRTUAL_KEY.VK_RETURN:
                if (Mode == CaptureMode.ColorPicker) PickColor(0);
                else FinishScreenshot(WindowOrMonitorAt(_mx, _my));
                return;
            case VIRTUAL_KEY.VK_SPACE:
                if (Mode == CaptureMode.Screenshot) FinishScreenshot(_monitor);
                return;
        }
        int d = KeyHook.DigitOf(vk);
        if (d > 0 && Mode == CaptureMode.ColorPicker && d <= _colorCfg.Formats.Count) PickColor(d);
    }

    private void Nudge(int dx, int dy)
    {
        PInvoke.SetCursorPos(_mx + dx, _my + dy);   // the WM_MOUSEMOVE that follows redraws
    }

    private void MoveTo(int x, int y)
    {
        if (x == _mx && y == _my) return;
        _mx = x; _my = y;
        if (_dragging && (Math.Abs(_mx - _dx0) > 3 || Math.Abs(_my - _dy0) > 3)) _hasDrag = true;
        UpdateMonitor();
        Draw();
    }

    private void UpdateMonitor()
    {
        HMONITOR mon = PInvoke.MonitorFromPoint(new Point(_mx, _my), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        PInvoke.GetMonitorInfo(mon, &mi);
        _monitor = mi.rcMonitor;
        uint dx, dy;
        _scale = PInvoke.GetDpiForMonitor(mon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dx, &dy).Succeeded ? dx / 96f : 1f;
    }

    private RECT DragRect() => new()
    {
        left = Math.Min(_dx0, _mx), top = Math.Min(_dy0, _my),
        right = Math.Max(_dx0, _mx) + 1, bottom = Math.Max(_dy0, _my) + 1,
    };

    private RECT WindowOrMonitorAt(int x, int y)
    {
        foreach (var r in _windows)
            if (x >= r.left && x < r.right && y >= r.top && y < r.bottom) return Clip(r, _monitorUnion());
        return _monitor;
    }

    private RECT _monitorUnion() => new() { left = _grab!.X, top = _grab.Y, right = _grab.X + _grab.Width, bottom = _grab.Y + _grab.Height };

    private static RECT Clip(RECT r, RECT b) => new()
    {
        left = Math.Max(r.left, b.left), top = Math.Max(r.top, b.top), right = Math.Min(r.right, b.right), bottom = Math.Min(r.bottom, b.bottom),
    };

    // ───────────── results ─────────────

    private void FinishScreenshot(RECT r)
    {
        if (_grab == null || r.right - r.left < 2 || r.bottom - r.top < 2) { End(); return; }
        var px = _grab.Crop(r);
        End();
        _app.OnScreenshot(px);
    }

    private void PickColor(int formatNumber)
    {
        if (_grab == null) return;
        Color c = _grab.At(_mx, _my);
        var fmt = formatNumber > 0 ? _colorCfg.Formats[formatNumber - 1] : _colorCfg.Copy;
        string text = fmt.Render(c);
        _history.Add(c);
        _history.Save();
        End();
        _app.OnColorPicked(c, fmt.Name, text, _colorCfg.Formats.Count);
    }

    // ───────────── drawing (window client px = screen px − virtual origin) ─────────────

    private float Ox => _grab!.X;
    private float Oy => _grab!.Y;

    private void Draw()
    {
        if (_grab == null || !_surface.Begin()) return;
        _g.SetTranslate(0, 0);
        var full = new D2D_RECT_F { left = 0, top = 0, right = _grab.Width, bottom = _grab.Height };
        _g.Dc->DrawBitmap((ID2D1Bitmap*)_frame, &full, 1f, D2D1_INTERPOLATION_MODE.D2D1_INTERPOLATION_MODE_NEAREST_NEIGHBOR, null, null);
        if (Mode == CaptureMode.Screenshot) DrawScreenshot();
        else DrawPicker();
        _surface.End();
        _g.DComp->Commit();
    }

    private void DrawScreenshot()
    {
        var accent = T.Hue(0);
        RECT focus = _dragging && _hasDrag ? DragRect() : WindowOrMonitorAt(_mx, _my);
        bool hasFocus = !(_dragging && !_hasDrag);
        float l = focus.left - Ox, t = focus.top - Oy, r = focus.right - Ox, b = focus.bottom - Oy;
        var dim = C(0x05070D, 0.52f);
        float W = _grab!.Width, H = _grab.Height;
        if (hasFocus)
        {
            _g.FillRect(0, 0, W, t, dim);
            _g.FillRect(0, b, W, H, dim);
            _g.FillRect(0, t, l, b, dim);
            _g.FillRect(r, t, W, b, dim);
            _g.StrokeRect(l + 1, t + 1, r - 1, b - 1, accent, 2f * _scale);
            // Corner ticks.
            float k = 14 * _scale, w = 4 * _scale;
            foreach (var (cx, cy, sx, sy) in new[] { (l, t, 1, 1), (r, t, -1, 1), (l, b, 1, -1), (r, b, -1, -1) })
            {
                _g.FillRect(Math.Min(cx, cx + sx * k), Math.Min(cy, cy + sy * w), Math.Max(cx, cx + sx * k), Math.Max(cy, cy + sy * w), accent);
                _g.FillRect(Math.Min(cx, cx + sx * w), Math.Min(cy, cy + sy * k), Math.Max(cx, cx + sx * w), Math.Max(cy, cy + sy * k), accent);
            }
            string size = $"{focus.right - focus.left} × {focus.bottom - focus.top}";
            float pw = _g.Measure(size, _fmtPill) * _scale + 20 * _scale, ph = 24 * _scale;
            float py = b + 8 * _scale + ph < H ? b + 8 * _scale : t - 8 * _scale - ph;
            Pill(l, py, pw, ph, size, C(0x0B0E1A, 0.88f), C(0xFFFFFF));
        }
        else
        {
            _g.FillRect(0, 0, W, H, dim);
        }

        if (!_dragging)
        {
            // Guides through the pointer.
            float x = _mx - Ox + 0.5f, y = _my - Oy + 0.5f;
            _g.Line(x, _monitor.top - Oy, x, _monitor.bottom - Oy, C(0xFFFFFF, 0.22f), 1);
            _g.Line(_monitor.left - Ox, y, _monitor.right - Ox, y, C(0xFFFFFF, 0.22f), 1);
        }

        // Hint at the top of the monitor under the pointer.
        const string hint = "Drag to select   ·   Click a window   ·   Space  whole screen   ·   Esc";
        float hw = (_g.Measure(hint, _fmtPill) + 52) * _scale, hh = 34 * _scale;
        float hx = (_monitor.left + _monitor.right) / 2f - Ox - hw / 2, hy = _monitor.top - Oy + 18 * _scale;
        _g.SetTransform(_scale, 0, 0, _scale, hx, hy);
        _g.FillRound(0, 0, hw / _scale, hh / _scale, 17, C(0x0B0E1A, 0.9f));
        _g.StrokeRound(0.5f, 0.5f, hw / _scale - 0.5f, hh / _scale - 0.5f, 17, C(0xFFFFFF, 0.12f), 1);
        _g.Text(''.ToString(), _fmtGlyph, 20, hh / _scale / 2, 20, 20, accent);
        _g.Text(hint, _fmtPill, hw / _scale / 2 + 13, hh / _scale / 2, hw / _scale, 20, C(0xE8EBF3));
        _g.SetTranslate(0, 0);
    }

    private void Pill(float x, float y, float w, float h, string text, D2D1_COLOR_F bg, D2D1_COLOR_F fg)
    {
        _g.SetTransform(_scale, 0, 0, _scale, x, y);
        _g.FillRound(0, 0, w / _scale, h / _scale, h / _scale / 2, bg);
        _g.Text(text, _fmtPill, w / _scale / 2, h / _scale / 2, w / _scale, h / _scale, fg);
        _g.SetTranslate(0, 0);
    }

    private void DrawPicker()
    {
        var t = T;
        Color c = _grab!.At(_mx, _my);
        var col = C((uint)(c.R << 16 | c.G << 8 | c.B));
        float s = _scale;
        int zoom = ZoomSteps[_zoomIndex];

        // Layout in DIPs around the pointer, flipped to stay on the pointer's monitor.
        const float R = 66, Gap = 26, PanelW = 232;
        float panelH = 74 + _colorCfg.Formats.Count * 22 + (_history.Items.Count > 0 ? 30 : 0);
        float totalH = R * 2 + 10 + panelH;
        float mx = _mx - Ox, my = _my - Oy;
        float ml = _monitor.left - Ox, mt = _monitor.top - Oy, mr = _monitor.right - Ox, mb = _monitor.bottom - Oy;
        float lx = mx + (Gap + R) * s, ly = my + (Gap + R) * s;
        if (lx + Math.Max(R, PanelW / 2) * s > mr) lx = mx - (Gap + R) * s;
        if (ly - R * s + totalH * s > mb) ly = my - (Gap + R) * s - (totalH - 2 * R) * s;

        // Loupe: the frozen frame magnified around the pointer, nearest-neighbour so pixels stay crisp.
        float px = zoom * s;   // screen px per source pixel
        float srcX = _mx - _grab.X + 0.5f, srcY = _my - _grab.Y + 0.5f;
        var m = Matrix(px, 0, 0, px, lx - srcX * px, ly - srcY * px);
        ((ID2D1Brush*)_loupeBrush)->SetTransform(&m);
        _g.SetColor(C(0x000000, 0.35f));
        _g.FillCircle(lx, ly + 4 * s, R * s + 7 * s, C(0x000000, 0.28f));
        _g.FillCircle(lx, ly, R * s, (ID2D1Brush*)_loupeBrush);

        // Pixel grid inside the loupe (clipped to the circle).
        if (zoom >= 7)
        {
            Release(_loupeGeo);
            _loupeGeo = _g.Ellipse(lx, ly, R * s);
            var lp = new D2D1_LAYER_PARAMETERS1
            {
                contentBounds = new D2D_RECT_F { left = lx - R * s, top = ly - R * s, right = lx + R * s, bottom = ly + R * s },
                geometricMask = (ID2D1Geometry*)_loupeGeo,
                maskAntialiasMode = D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_PER_PRIMITIVE,
                maskTransform = Matrix(1, 0, 0, 1, 0, 0),
                opacity = 1,
            };
            _g.Dc->PushLayer(&lp, null);
            float ox = lx - px / 2, oy = ly - px / 2;
            int cells = (int)(R * s / px) + 1;
            var grid = C(0x000000, 0.18f);
            for (int i = -cells; i <= cells + 1; i++)
            {
                _g.Line(ox + i * px, ly - R * s, ox + i * px, ly + R * s, grid, 1);
                _g.Line(lx - R * s, oy + i * px, lx + R * s, oy + i * px, grid, 1);
            }
            _g.Dc->PopLayer();
        }
        // The picked pixel.
        _g.StrokeRect(lx - px / 2 - 1, ly - px / 2 - 1, lx + px / 2 + 1, ly + px / 2 + 1, C(0x000000, 0.9f), 1.5f);
        _g.StrokeRect(lx - px / 2, ly - px / 2, lx + px / 2, ly + px / 2, C(0xFFFFFF), 1f);
        // Rim in the picked colour.
        _g.StrokeCircle(lx, ly, R * s + 3 * s, col, 6 * s);
        _g.StrokeCircle(lx, ly, R * s + 6 * s, C(0xFFFFFF, 0.55f), 1);

        // Panel under the loupe.
        float pl = lx / s - PanelW / 2, pt = ly / s + R + 14;
        _g.SetTransform(s, 0, 0, s, 0, 0);
        var bg = t.Light ? C(0xFFFFFF, 0.97f) : C(0x0E1222, 0.95f);
        _g.FillRound(pl, pt + 3, pl + PanelW, pt + panelH + 3, 14, C(0x000000, 0.25f));
        _g.FillRound(pl, pt, pl + PanelW, pt + panelH, 14, bg);
        _g.StrokeRound(pl + 0.5f, pt + 0.5f, pl + PanelW - 0.5f, pt + panelH - 0.5f, 14, t.Light ? C(0x000000, 0.08f) : C(0xFFFFFF, 0.10f), 1);

        // Swatch + main value.
        _g.FillRound(pl + 12, pt + 12, pl + 52, pt + 52, 10, col);
        _g.StrokeRound(pl + 12.5f, pt + 12.5f, pl + 51.5f, pt + 51.5f, 10, t.Light ? C(0x000000, 0.12f) : C(0xFFFFFF, 0.18f), 1);
        string main = _colorCfg.Copy.Render(c);
        _g.Text(main, _fmtMonoBold, pl + 64 + 78, pt + 24, 156, 18, t.Text);
        _g.Text($"{_mx}, {_my}  ·  {zoom}×", _fmtSmall, pl + 64 + 54, pt + 42, 108, 14, t.TextFaint);

        // Every visible format, numbered (press the number to copy that one).
        float y = pt + 66;
        for (int i = 0; i < _colorCfg.Formats.Count; i++)
        {
            var f = _colorCfg.Formats[i];
            bool def = f.Name == _colorCfg.Copy.Name;
            if (def) _g.FillRound(pl + 8, y - 1, pl + PanelW - 8, y + 19, 6, WithAlpha(t.Hue(1), t.Light ? 0.12f : 0.16f));
            _g.Text((i + 1).ToString(), _fmtSmall, pl + 20, y + 9, 14, 14, def ? t.Hue(1) : t.TextFaint);
            _g.Text(f.Name, _fmtSmall, pl + 48, y + 9, 40, 14, t.TextDim);
            _g.Text(f.Render(c), _fmtMono, pl + 72 + 74, y + 9, 148, 16, t.Text);
            y += 22;
        }

        // Recent colours.
        if (_history.Items.Count > 0)
        {
            y += 6;
            int n = Math.Min(12, _history.Items.Count);
            for (int i = 0; i < n; i++)
            {
                var h = _history.Items[i];
                _g.FillRound(pl + 12 + i * 17.5f, y, pl + 12 + i * 17.5f + 13, y + 13, 4, C((uint)(h.R << 16 | h.G << 8 | h.B)));
            }
        }
        _g.SetTranslate(0, 0);
    }
}
