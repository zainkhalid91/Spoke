using System.Collections.Concurrent;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Spoke.Daemon;

/// <summary>
/// Extracts app icons for app slices on a background STA thread (shell icon extraction can take tens of
/// milliseconds per app on a cold cache). Results are 32-bit premultiplied pixels; the UI thread turns them
/// into GPU bitmaps when <see cref="App.WM_APP_ICONS"/> arrives.
/// </summary>
internal sealed unsafe class IconLoader
{
    public const int Size = 48;   // px; drawn at 24 DIPs, so sharp up to 200% scaling

    public readonly ConcurrentQueue<(int Generation, int Index, Pixels Pixels)> Results = new();
    private readonly HWND _notify;

    public IconLoader(HWND notify) => _notify = notify;

    /// <summary>Starts loading; <paramref name="generation"/> lets the UI ignore results for an older config.</summary>
    public void Load(int generation, IReadOnlyList<(int Index, string Target)> apps)
    {
        if (apps.Count == 0) return;
        var t = new Thread(() =>
        {
            PInvoke.CoInitializeEx(null, COINIT.COINIT_APARTMENTTHREADED);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int ok = 0;
            foreach (var (index, target) in apps)
            {
                try
                {
                    if (Extract(Resolve(target)) is { } px) { Results.Enqueue((generation, index, px)); ok++; }
                }
                catch (Exception ex)
                {
                    Log.Write($"Icon for {target}: {ex.Message}");
                }
            }
            Log.Write($"Icons: {ok}/{apps.Count} in {Log.Ms(t0, System.Diagnostics.Stopwatch.GetTimestamp()):F0} ms");
            PInvoke.PostMessage(_notify, App.WM_APP_ICONS, default, default);
            PInvoke.CoUninitialize();
        })
        { IsBackground = true, Name = "Spoke icons", Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>"wt.exe" → a full path (PATH, then App Paths), env vars expanded; shell: paths unchanged.</summary>
    public static string Resolve(string target)
    {
        string t = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
        if (t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(t)) return t;

        var buf = stackalloc char[1024];
        fixed (char* name = t)
        fixed (char* ext = ".exe")
        {
            uint n = PInvoke.SearchPath(null, name, ext, 1024, buf, null);
            if (n > 0 && n < 1024) return new string(buf, 0, (int)n);
        }
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            string exe = t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t : t + ".exe";
            using var key = root.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (key?.GetValue(null) is string path && path.Length > 0) return Environment.ExpandEnvironmentVariables(path.Trim('"'));
        }
        return t;
    }

    private static Pixels? Extract(string path)
    {
        Guid iid = IShellItemImageFactory.IID_Guid;
        IShellItemImageFactory* factory;
        fixed (char* p = path)
        {
            if (PInvoke.SHCreateItemFromParsingName(p, null, &iid, (void**)&factory).Failed) return null;
        }
        HBITMAP hbm = default;
        try
        {
            factory->GetImage(new SIZE { cx = Size, cy = Size }, SIIGBF.SIIGBF_ICONONLY, &hbm);
            return hbm.IsNull ? null : ToPixels(hbm);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (!hbm.IsNull) PInvoke.DeleteObject(hbm);
            Gfx.Release(factory);
        }
    }

    /// <summary>HBITMAP → top-down premultiplied BGRA.</summary>
    private static Pixels? ToPixels(HBITMAP hbm)
    {
        BITMAP bm;
        if (PInvoke.GetObject(hbm, sizeof(BITMAP), &bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var data = new byte[w * h * 4];
        var bi = new BITMAPINFO();
        bi.bmiHeader = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        HDC dc = PInvoke.GetDC(HWND.Null);
        try
        {
            fixed (byte* d = data)
                if (PInvoke.GetDIBits(dc, hbm, 0, (uint)h, d, &bi, DIB_USAGE.DIB_RGB_COLORS) == 0) return null;
        }
        finally
        {
            PInvoke.ReleaseDC(HWND.Null, dc);
        }

        // Icons come premultiplied with real alpha; some old ones have no alpha at all (all zero) → opaque.
        bool anyAlpha = false, straight = false;
        for (int i = 0; i < data.Length; i += 4)
        {
            byte a = data[i + 3];
            if (a != 0) anyAlpha = true;
            if (data[i] > a || data[i + 1] > a || data[i + 2] > a) straight = true;
        }
        if (!anyAlpha)
        {
            for (int i = 3; i < data.Length; i += 4) data[i] = 255;
        }
        else if (straight)
        {
            for (int i = 0; i < data.Length; i += 4)
            {
                int a = data[i + 3];
                data[i] = (byte)(data[i] * a / 255); data[i + 1] = (byte)(data[i + 1] * a / 255); data[i + 2] = (byte)(data[i + 2] * a / 255);
            }
        }
        return new Pixels { Width = w, Height = h, Data = data };
    }
}
