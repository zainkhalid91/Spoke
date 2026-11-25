using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Spoke.Daemon;

/// <summary>
/// A frozen copy of the whole virtual screen (every monitor) in physical pixels, 32-bit BGRx, top-down.
/// The capture overlay draws this frame, so what you select or pick is exactly what was on screen.
/// </summary>
internal sealed unsafe class ScreenGrab : IDisposable
{
    public int X, Y, Width, Height;   // virtual-screen rect, physical px
    public byte* Bits;
    private HBITMAP _bmp;
    public int Stride => Width * 4;

    public static ScreenGrab Capture()
    {
        var g = new ScreenGrab
        {
            X = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
            Y = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
            Width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
            Height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN),
        };
        HDC screen = PInvoke.GetDC(HWND.Null);
        HDC mem = PInvoke.CreateCompatibleDC(screen);
        var bi = new BITMAPINFO();
        bi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = g.Width;
        bi.bmiHeader.biHeight = -g.Height;   // top-down
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        void* bits;
        g._bmp = PInvoke.CreateDIBSection(mem, &bi, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        g.Bits = (byte*)bits;
        var old = PInvoke.SelectObject(mem, (HGDIOBJ)g._bmp.Value);
        PInvoke.BitBlt(mem, 0, 0, g.Width, g.Height, screen, g.X, g.Y, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT);
        PInvoke.SelectObject(mem, old);
        PInvoke.DeleteDC(mem);
        PInvoke.ReleaseDC(HWND.Null, screen);
        return g;
    }

    public Color At(int sx, int sy)
    {
        int x = Math.Clamp(sx - X, 0, Width - 1), y = Math.Clamp(sy - Y, 0, Height - 1);
        byte* p = Bits + y * Stride + x * 4;
        return Color.FromArgb(255, p[2], p[1], p[0]);
    }

    /// <summary>Copies a screen-coordinate rectangle out as opaque BGRA.</summary>
    public Pixels Crop(RECT r)
    {
        int l = Math.Clamp(r.left - X, 0, Width), t = Math.Clamp(r.top - Y, 0, Height);
        int w = Math.Clamp(r.right - X, 0, Width) - l, h = Math.Clamp(r.bottom - Y, 0, Height) - t;
        w = Math.Max(1, w); h = Math.Max(1, h);
        var data = new byte[w * h * 4];
        fixed (byte* d = data)
        {
            for (int y = 0; y < h; y++)
            {
                uint* src = (uint*)(Bits + (t + y) * Stride + l * 4);
                uint* dst = (uint*)(d + y * w * 4);
                for (int x = 0; x < w; x++) dst[x] = src[x] | 0xFF000000u;
            }
        }
        return new Pixels { Width = w, Height = h, Data = data };
    }

    public void Dispose()
    {
        if (!_bmp.IsNull) { PInvoke.DeleteObject((HGDIOBJ)_bmp.Value); _bmp = default; Bits = null; }
    }

    // ───────────── top-level windows, for "click a window" ─────────────

    private static List<RECT>? _collect;

    /// <summary>Visible top-level windows in z-order (topmost first), excluding Spoke's own and the desktop.</summary>
    public static List<RECT> VisibleWindows()
    {
        _collect = [];
        PInvoke.EnumWindows(&EnumProc, default);
        var list = _collect;
        _collect = null;
        return list;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL EnumProc(HWND hwnd, LPARAM _)
    {
        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd)) return true;
        uint pid;
        PInvoke.GetWindowThreadProcessId(hwnd, &pid);
        if (pid == (uint)Environment.ProcessId) return true;
        int cloaked = 0;
        PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        if (cloaked != 0) return true;
        Span<char> cls = stackalloc char[32];
        int n = PInvoke.GetClassName(hwnd, cls);
        var name = cls[..Math.Max(0, n)];
        if (name.SequenceEqual("Progman") || name.SequenceEqual("WorkerW")) return true;
        RECT r;
        if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT)).Failed) return true;
        if (r.right - r.left < 8 || r.bottom - r.top < 8) return true;
        _collect!.Add(r);
        return true;
    }
}
