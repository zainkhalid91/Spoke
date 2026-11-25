using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Spoke.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Spoke.Daemon;

/// <summary>
/// The daemon: the wheel overlay, the capture overlay (screenshot + colour picker), a toast, a hidden tray
/// window, a global hotkey, and the clipboard image history. Input hooks exist only while an overlay is open.
/// Everything here runs on the UI thread; image processing for the history runs on <see cref="ClipWorker"/>.
/// </summary>
internal sealed unsafe class App
{
    public static App? Instance;

    // Private window messages (WM_APP + 1/2 are KeyHook's).
    public const uint WM_APP_TRAY = PInvoke.WM_APP + 3;    // tray icon callback
    public const uint WM_APP_QUIT = PInvoke.WM_APP + 4;
    public const uint WM_APP_CLIPS = PInvoke.WM_APP + 5;   // image history changed (from the worker)

    private const int HotkeyId = 1;
    private const nuint TrimTimerId = 7, ClipTimerId = 8;
    private const uint TrimAfterMs = 60_000;   // give memory back to Windows a minute after the wheel closes
    private const int CmdOpen = 100, CmdPause = 101, CmdQuit = 102, CmdClips = 103, CmdClearClips = 104, CmdTheme0 = 110;

    public HWND Overlay { get; private set; }
    private readonly HWND _tray;
    private uint _taskbarCreated;
    private HICON _icon;

    public readonly Gfx Gfx;
    public readonly WheelRenderer Renderer;
    private readonly CaptureOverlay _capture;
    private readonly Toast _toast;
    private readonly SliceDef[] _slices;
    private readonly WheelLayout _layout;
    private readonly Settings _settings;

    // Image history.
    private readonly ClipStore _clips;
    private readonly ClipWorker _clipWorker;
    private Imaging? _imaging;   // UI-thread WIC, created on first use
    private Imaging Img => _imaging ??= new Imaging();
    private readonly Dictionary<ulong, ClipThumb> _thumbCache = [];
    private IReadOnlyList<ClipEntry> _clipEntries = [];
    private int _clipRetries;

    // Open-wheel state (physical pixels unless noted).
    public bool IsOpen { get; private set; }
    private readonly WheelView _view = new();
    private RECT _work;
    private int _centerX, _centerY;   // wheel centre, screen px
    private HWND _fgAtOpen;
    private bool _paused;
    private bool _hotkeyOk;

    // Bench instrumentation (written on the UI thread, read by the bench thread after waits).
    public readonly ManualResetEventSlim Shown = new(false), Hidden = new(true);
    public volatile bool BenchMode;
    public long LastHotkeyTicks, LastShownTicks, LastDwmTicks;
    public double StagePrep, StageDraw, StageCommit, StageShow;   // summed ms per open stage
    public int StageCount;
    private RECT _placedRect;
    public long KeyToFrameCount;
    public double KeyToFrameTotal, KeyToFrameMax;

    private static readonly VIRTUAL_KEY[] WheelKeys =
    [
        VIRTUAL_KEY.VK_ESCAPE, VIRTUAL_KEY.VK_RETURN, VIRTUAL_KEY.VK_LEFT, VIRTUAL_KEY.VK_RIGHT, VIRTUAL_KEY.VK_UP, VIRTUAL_KEY.VK_DOWN, .. KeyHook.Digits,
    ];
    private static readonly VIRTUAL_KEY[] ClipsKeys =
    [
        .. WheelKeys, VIRTUAL_KEY.VK_BACK, VIRTUAL_KEY.VK_DELETE, VIRTUAL_KEY.VK_PRIOR, VIRTUAL_KEY.VK_NEXT, VIRTUAL_KEY.VK_TAB,
    ];

    public App(SliceDef[] slices, bool forceWarp, string? preferredHotkey = null)
    {
        _slices = slices;
        Instance = this;
        _settings = Settings.Load();

        HINSTANCE hinst = (HINSTANCE)(nint)PInvoke.GetModuleHandle((PCWSTR)null).Value;
        RegisterClass("SpokeOverlay", hinst, PInvoke.IDC_ARROW);
        RegisterClass("SpokeTray", hinst, PInvoke.IDC_ARROW);
        RegisterClass("SpokeCapture", hinst, PInvoke.IDC_CROSS);
        RegisterClass("SpokeToast", hinst, PInvoke.IDC_ARROW);

        // Overlays: popup, never activated, no taskbar/Alt+Tab entry, topmost, DComp-only content.
        var overlayEx = WINDOW_EX_STYLE.WS_EX_NOACTIVATE | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP;
        Overlay = Create("SpokeOverlay", hinst, overlayEx, WINDOW_STYLE.WS_POPUP);
        var captureHwnd = Create("SpokeCapture", hinst, overlayEx, WINDOW_STYLE.WS_POPUP);
        var toastHwnd = Create("SpokeToast", hinst, overlayEx, WINDOW_STYLE.WS_POPUP);
        _tray = Create("SpokeTray", hinst, WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, WINDOW_STYLE.WS_POPUP);

        Gfx = new Gfx(forceWarp);
        Renderer = new WheelRenderer(Gfx, Overlay, slices);
        Renderer.SetTheme(Theme.ByName(_settings.Theme));
        _layout = Renderer.Layout;
        _capture = new CaptureOverlay(Gfx, this, captureHwnd);
        _toast = new Toast(Gfx, this, toastHwnd);

        // Pre-draw once, at the likely monitor's DPI, so the first open doesn't pay lazy D2D/DWrite set-up or
        // building the cached wheel picture.
        PInvoke.GetCursorPos(out var cur);
        uint warmDpi, warmDpiY;
        if (PInvoke.GetDpiForMonitor(PInvoke.MonitorFromPoint(cur, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST), MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &warmDpi, &warmDpiY).Failed) warmDpi = 96;
        Renderer.SetDpi(warmDpi);
        Renderer.Draw(_view);
        Renderer.Commit();

        _clips = new ClipStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spoke", "clips"), new DpapiProtector());
        _clipWorker = new ClipWorker(_clips, _tray);
        PInvoke.AddClipboardFormatListener(_tray);

        fixed (char* tb = "TaskbarCreated") _taskbarCreated = PInvoke.RegisterWindowMessage(tb);
        var notice = RegisterHotkey(preferredHotkey);
        AddTrayIcon();
        if (notice is { } n) Balloon(n.Title, n.Text);
        ScheduleTrim();
    }

    /// <summary>
    /// One-shot timer (not polling): a minute after the last overlay closes, drop cached thumbnails and empty
    /// our working set so the idle daemon holds only a few MB. Opens within that minute stay fast.
    /// </summary>
    public void ScheduleTrim()
    {
        if (!BenchMode) PInvoke.SetTimer(_tray, TrimTimerId, TrimAfterMs, null);
    }

    // ───────────────────────── windows ─────────────────────────

    private static void RegisterClass(string name, HINSTANCE hinst, PCWSTR cursor)
    {
        fixed (char* cls = name)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = hinst,
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, cursor),
                lpszClassName = cls,
            };
            if (PInvoke.RegisterClassEx(&wc) == 0) throw new InvalidOperationException($"RegisterClassEx {name} failed");
        }
    }

    private static HWND Create(string cls, HINSTANCE hinst, WINDOW_EX_STYLE ex, WINDOW_STYLE style)
    {
        fixed (char* c = cls)
        fixed (char* title = "Spoke")
        {
            HWND h = PInvoke.CreateWindowEx(ex, c, title, style, 0, 0, 1, 1, HWND.Null, HMENU.Null, hinst, null);
            if (h.IsNull) throw new InvalidOperationException($"CreateWindowEx {cls} failed");
            return h;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            var app = Instance;
            if (app != null && app.Handle(hwnd, msg, wParam, lParam, out LRESULT result)) return result;
        }
        catch (Exception ex)
        {
            Log.Write($"WndProc 0x{msg:X}: {ex}");
        }
        return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private bool Handle(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam, out LRESULT result)
    {
        result = default;
        if (_capture != null && hwnd == _capture.Hwnd) return _capture.Handle(msg, wParam, lParam, out result);
        if (_toast != null && hwnd == _toast.Hwnd) return _toast.Handle(msg, wParam, out result);

        switch (msg)
        {
            case PInvoke.WM_MOUSEACTIVATE:
                result = (LRESULT)(nint)PInvoke.MA_NOACTIVATE;   // clicking the wheel never takes focus
                return true;

            case PInvoke.WM_HOTKEY when (int)wParam.Value == HotkeyId:
                if (IsOpen) Close();
                else if (_capture!.IsOpen) _capture.End();
                else Open(Stopwatch.GetTimestamp());
                return true;

            case PInvoke.WM_MOUSEMOVE when hwnd == Overlay && IsOpen:
                SetHover(HitTestClient(lParam));
                return true;

            case PInvoke.WM_LBUTTONUP when hwnd == Overlay && IsOpen:
            {
                int s = HitTestClient(lParam);
                bool shift = (wParam.Value & 0x0004) != 0;   // MK_SHIFT
                if (s >= 0) Run(s, copyOnly: shift);
                else if (_view.Clips && !IsOutsideClient(lParam)) ExitClips();   // the hub goes back
                else Close();
                return true;
            }

            case PInvoke.WM_RBUTTONUP when hwnd == Overlay && IsOpen:
                if (_view.Clips) ExitClips(); else Close();
                return true;

            case PInvoke.WM_MOUSEWHEEL when hwnd == Overlay && IsOpen && _view.Clips:
                TurnPage((short)((wParam.Value >> 16) & 0xFFFF) > 0 ? -1 : 1);
                return true;

            case KeyHook.WM_APP_KEY when hwnd == Overlay && IsOpen:
                OnKey((VIRTUAL_KEY)(ushort)wParam.Value);
                return true;

            case KeyHook.WM_APP_FG when hwnd == Overlay && IsOpen:
                if (PInvoke.GetForegroundWindow() != _fgAtOpen) Close();
                return true;

            case WM_APP_TRAY when hwnd == _tray:
                OnTray((uint)(lParam.Value & 0xFFFF));
                return true;

            case PInvoke.WM_CLIPBOARDUPDATE when hwnd == _tray:
                // Apps often update the clipboard several times in a row; act once it settles.
                _clipRetries = 0;
                PInvoke.SetTimer(_tray, ClipTimerId, 150, null);
                return true;

            case WM_APP_CLIPS when hwnd == _tray:
                if (IsOpen && _view.Clips) { LoadClipThumbs(); Renderer.Draw(_view); Renderer.Commit(); }
                return true;

            case PInvoke.WM_TIMER when hwnd == _tray && wParam.Value == ClipTimerId:
                OnClipboardSettled();
                return true;

            case PInvoke.WM_TIMER when hwnd == _tray && wParam.Value == TrimTimerId:
                PInvoke.KillTimer(_tray, TrimTimerId);
                if (!IsOpen && !_capture!.IsOpen)
                {
                    ReleaseThumbs();
                    Bench.TrimWorkingSet();
                }
                return true;

            case WM_APP_QUIT:
                Quit();
                return true;

            case PInvoke.WM_COMMAND when hwnd == _tray:
                OnCommand((int)(wParam.Value & 0xFFFF));
                return true;
        }

        if (msg == _taskbarCreated && _taskbarCreated != 0)
        {
            AddTrayIcon();   // Explorer restarted
            return true;
        }
        return false;
    }

    // ───────────────────────── open / close ─────────────────────────

    public void Open(long hotkeyTicks, bool clips = false)
    {
        if (_paused) return;
        LastHotkeyTicks = hotkeyTicks;
        PInvoke.KillTimer(_tray, TrimTimerId);

        PInvoke.GetCursorPos(out var pt);
        HMONITOR mon = PInvoke.MonitorFromPoint(pt, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        PInvoke.GetMonitorInfo(mon, &mi);
        _work = mi.rcWork;
        uint dpiX, dpiY;
        if (PInvoke.GetDpiForMonitor(mon, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dpiX, &dpiY).Failed) dpiX = 96;
        Renderer.SetDpi(dpiX);   // no-op unless this monitor's DPI differs from the last open

        int extentPx = Renderer.PixelSize / 2;
        int ringPx = (int)MathF.Ceiling((_layout.OuterRadius + WheelRenderer.Lift + 4) * dpiX / 96f);
        (_centerX, _centerY) = WheelLayout.ClampCenter(pt.X, pt.Y, ringPx, _work.left, _work.top, _work.right, _work.bottom);

        _view.Clips = clips;
        _view.Page = 0;
        if (clips) LoadClipThumbs();
        _view.Hover = HitTestScreen(pt.X, pt.Y);
        long tPrep = Stopwatch.GetTimestamp();
        Renderer.SetOffset(_centerX - extentPx - _work.left, _centerY - extentPx - _work.top);
        Renderer.Draw(_view);
        Renderer.AnimateIn();
        long tDraw = Stopwatch.GetTimestamp();
        Renderer.Commit();
        long tCommit = Stopwatch.GetTimestamp();

        // Only move/resize the window when the monitor changed; otherwise just show it in place.
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW;
        if (_work.Equals(_placedRect)) flags |= SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE;
        PInvoke.SetWindowPos(Overlay, HWND.HWND_TOPMOST, _work.left, _work.top, _work.right - _work.left, _work.bottom - _work.top, flags);
        _placedRect = _work;
        LastShownTicks = Stopwatch.GetTimestamp();
        StagePrep += Log.Ms(hotkeyTicks, tPrep); StageDraw += Log.Ms(tPrep, tDraw);
        StageCommit += Log.Ms(tDraw, tCommit); StageShow += Log.Ms(tCommit, LastShownTicks); StageCount++;

        IsOpen = true;
        _fgAtOpen = PInvoke.GetForegroundWindow();
        KeyHook.Install(Overlay, clips ? ClipsKeys : WheelKeys);

        if (BenchMode)
        {
            Renderer.WaitForCommitCompletion();
            LastDwmTicks = Stopwatch.GetTimestamp();
        }
        Hidden.Reset();
        Shown.Set();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        PInvoke.SetWindowPos(Overlay, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_HIDEWINDOW | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
        KeyHook.Uninstall();
        _view.Hover = -1;
        _view.Clips = false;
        Shown.Reset();
        Hidden.Set();
        ScheduleTrim();
    }

    private void SetHover(int s)
    {
        if (s == _view.Hover) return;
        _view.Hover = s;
        Renderer.Draw(_view);
    }

    private int HitTestClient(LPARAM lParam)
    {
        int x = (short)(lParam.Value & 0xFFFF), y = (short)((lParam.Value >> 16) & 0xFFFF);
        return HitTestScreen(x + _work.left, y + _work.top);
    }

    /// <summary>A click beyond the ring closes the wheel instead of selecting.</summary>
    private bool IsOutsideClient(LPARAM lParam)
    {
        int x = (short)(lParam.Value & 0xFFFF) + _work.left, y = (short)((lParam.Value >> 16) & 0xFFFF) + _work.top;
        float scale = 96f / Renderer.Dpi;
        return _layout.IsOutside((x - _centerX) * scale, (y - _centerY) * scale);
    }

    private int HitTestScreen(int x, int y)
    {
        float scale = 96f / Renderer.Dpi;
        return _layout.HitTest((x - _centerX) * scale, (y - _centerY) * scale);
    }

    private void OnKey(VIRTUAL_KEY vk)
    {
        switch (vk)
        {
            case VIRTUAL_KEY.VK_ESCAPE:
            case VIRTUAL_KEY.VK_BACK:
                if (_view.Clips) ExitClips(); else Close();
                return;
            case VIRTUAL_KEY.VK_RETURN: if (_view.Hover >= 0) Run(_view.Hover, copyOnly: KeyHook.IsDown(VIRTUAL_KEY.VK_SHIFT)); return;
            case VIRTUAL_KEY.VK_RIGHT or VIRTUAL_KEY.VK_DOWN: SetHover(_layout.Step1(_view.Hover, 1)); break;
            case VIRTUAL_KEY.VK_LEFT or VIRTUAL_KEY.VK_UP: SetHover(_layout.Step1(_view.Hover, -1)); break;
            case VIRTUAL_KEY.VK_NEXT or VIRTUAL_KEY.VK_TAB: TurnPage(KeyHook.IsDown(VIRTUAL_KEY.VK_SHIFT) && vk == VIRTUAL_KEY.VK_TAB ? -1 : 1); return;
            case VIRTUAL_KEY.VK_PRIOR: TurnPage(-1); return;
            case VIRTUAL_KEY.VK_DELETE: DeleteHoveredClip(); return;
            default:
            {
                int n = KeyHook.DigitOf(vk) - 1;
                if (n >= 0 && n < _slices.Length) Run(n, copyOnly: KeyHook.IsDown(VIRTUAL_KEY.VK_SHIFT));
                return;
            }
        }
        // Arrow keys: measure hook → frame presented.
        double ms = Log.Ms(KeyHook.LastKeyTicks, Stopwatch.GetTimestamp());
        KeyToFrameCount++; KeyToFrameTotal += ms; if (ms > KeyToFrameMax) KeyToFrameMax = ms;
    }

    private void Run(int slice, bool copyOnly = false)
    {
        if (_view.Clips)
        {
            int idx = _view.Page * _slices.Length + slice;
            if (idx >= _clipEntries.Count) return;
            var entry = _clipEntries[idx];
            Close();
            PasteClip(entry, paste: !copyOnly);
            return;
        }

        var s = _slices[slice];
        if (BenchMode) { Close(); return; }   // the bench never launches anything
        switch (s.Tool)
        {
            case Tool.Clips:
                EnterClips();
                return;
            case Tool.Screenshot:
                Close();
                _capture.Begin(CaptureMode.Screenshot);
                return;
            case Tool.ColorPicker:
                Close();
                _capture.Begin(CaptureMode.ColorPicker);
                return;
            default:
                Close();
                Actions.Launch(s);
                return;
        }
    }

    // ───────────────────────── clips (image history) ─────────────────────────

    private void EnterClips()
    {
        _view.Clips = true;
        _view.Page = 0;
        LoadClipThumbs();
        _view.Hover = -1;
        KeyHook.SetKeys(ClipsKeys);
        Renderer.Draw(_view);
        Renderer.AnimateIn();
        Renderer.Commit();
    }

    private void ExitClips()
    {
        _view.Clips = false;
        _view.Hover = -1;
        KeyHook.SetKeys(WheelKeys);
        Renderer.Draw(_view);
        Renderer.AnimateIn();
        Renderer.Commit();
    }

    private void TurnPage(int dir)
    {
        if (!_view.Clips || _view.Pages <= 1) return;
        _view.Page = (_view.Page + dir + _view.Pages) % _view.Pages;
        LoadClipThumbs();
        Renderer.Draw(_view);
        Renderer.AnimateIn();
        Renderer.Commit();
    }

    private void DeleteHoveredClip()
    {
        if (!_view.Clips || _view.Hover < 0) return;
        int idx = _view.Page * _slices.Length + _view.Hover;
        if (idx >= _clipEntries.Count) return;
        var e = _clipEntries[idx];
        _clips.Remove(e);
        if (_thumbCache.Remove(e.Hash, out var th)) ReleaseThumb(th);
        LoadClipThumbs();
        Renderer.Draw(_view);
    }

    /// <summary>Fills the 8 slots for the current page, decoding (and caching) thumbnails as needed.</summary>
    private void LoadClipThumbs()
    {
        _clipEntries = _clips.Entries;
        int per = _slices.Length;
        _view.ClipCount = _clipEntries.Count;
        _view.Pages = Math.Max(1, (_clipEntries.Count + per - 1) / per);
        if (_view.Page >= _view.Pages) _view.Page = _view.Pages - 1;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < per; i++)
        {
            int idx = _view.Page * per + i;
            if (idx >= _clipEntries.Count) { _view.Thumbs[i] = default; continue; }
            var e = _clipEntries[idx];
            if (!_thumbCache.TryGetValue(e.Hash, out var th))
            {
                var bytes = _clips.ReadThumb(e);
                if (bytes == null) { _view.Thumbs[i] = default; continue; }
                var px = Img.Decode(bytes, premultiplied: true);
                fixed (byte* d = px.Data) th.Bitmap = Gfx.Bitmap(d, px.Width, px.Height, px.Stride);
                th.Brush = Gfx.BitmapBrush(th.Bitmap);
                th.Width = px.Width;
                th.Height = px.Height;
                _thumbCache[e.Hash] = th;
            }
            th.Info = $"{e.Width}×{e.Height} · {ClipStore.Age(e.Time, now)}";
            _view.Thumbs[i] = th;
        }
    }

    private void ReleaseThumbs()
    {
        foreach (var th in _thumbCache.Values) ReleaseThumb(th);
        _thumbCache.Clear();
        Array.Clear(_view.Thumbs);
    }

    private static void ReleaseThumb(ClipThumb th)
    {
        Gfx.Release(th.Brush);
        Gfx.Release(th.Bitmap);
    }

    /// <summary>Puts a history image back on the clipboard and (unless copy-only) pastes it into the app you were in.</summary>
    private void PasteClip(ClipEntry e, bool paste)
    {
        var png = _clips.ReadImage(e);
        if (png == null) { Balloon("Image unavailable", "That image could not be read back from history."); return; }
        var px = Img.Decode(png);
        if (!ClipboardIO.SetImage(_tray, png, px, markOwn: true)) return;
        _clips.TryBump(e.Hash, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (paste) KeyHook.SendPaste();
        else _toast.ShowImage(Img.Decode(Img.Thumbnail(px, 160), premultiplied: true), "Image copied", $"{e.Width} × {e.Height} · press Ctrl+V to paste");
    }

    private void OnClipboardSettled()
    {
        PInvoke.KillTimer(_tray, ClipTimerId);
        if (PInvoke.GetClipboardSequenceNumber() == ClipboardIO.OwnSequence) return;   // our own paste-from-history
        var img = ClipboardIO.TryReadImage(_tray, _paused, out bool busy);
        if (busy && ++_clipRetries < 5) { PInvoke.SetTimer(_tray, ClipTimerId, 120, null); return; }
        if (img != null) _clipWorker.Enqueue(img);
    }

    // ───────────────────────── tool results ─────────────────────────

    public void OnScreenshot(Pixels px)
    {
        byte[] png = Img.EncodePng(px);
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        string path = Path.Combine(dir, $"Spoke {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Screenshot not saved: " + ex.Message);
            path = "";
        }
        ClipboardIO.SetImage(_tray, png, px, markOwn: false);   // the clipboard watcher adds it to Clips
        _toast.ShowImage(Img.Decode(Img.Thumbnail(px, 160), premultiplied: true), "Screenshot copied",
            path.Length > 0 ? $"{px.Width} × {px.Height} · saved to Pictures\\Screenshots" : $"{px.Width} × {px.Height}");
        Log.Write($"Screenshot {px.Width}x{px.Height} → {path}");
    }

    public void OnColorPicked(Color c, string formatName, string text, int formats)
    {
        ClipboardIO.SetText(_tray, text);
        _toast.ShowColor(c, $"Copied {text}", formats > 1 ? $"{formatName} · keys 1–{formats} in the picker copy other formats" : formatName);
    }

    // ───────────────────────── hotkey + tray ─────────────────────────

    /// <summary>The shortcut Spoke actually holds (the first free one from the candidate list).</summary>
    public Hotkey ActiveHotkey { get; private set; }

    private static readonly string[] FallbackHotkeys = ["Ctrl+Alt+Space", "Ctrl+Alt+Z", "Ctrl+Shift+Alt+Space", "Ctrl+Alt+`"];

    private (string Title, string Text)? RegisterHotkey(string? preferred)
    {
        (string, string)? notice = null;
        var taken = new List<string>();
        foreach (var text in preferred is null ? FallbackHotkeys : [preferred, .. FallbackHotkeys])
        {
            if (!Hotkey.TryParse(text, out var hk)) continue;
            if (PInvoke.RegisterHotKey(Overlay, HotkeyId, (HOT_KEY_MODIFIERS)(uint)hk.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT, hk.VirtualKey))
            {
                ActiveHotkey = hk;
                _hotkeyOk = true;
                break;
            }
            taken.Add(hk.ToString());
        }
        if (taken.Count > 0) Log.Write($"Shortcut(s) already used by another app: {string.Join(", ", taken)}.");
        if (!_hotkeyOk)
            notice = ("No free shortcut", "Every Spoke shortcut is used by another app. Open the wheel from the tray icon.");
        else if (taken.Count > 0)
            notice = ("Shortcut changed", $"{taken[0]} is used by another app, so Spoke is using {ActiveHotkey}.");
        Log.Write($"Hotkey: {(_hotkeyOk ? ActiveHotkey.ToString() : "none")}");
        return notice;
    }

    private void AddTrayIcon()
    {
        if (_icon.IsNull)
        {
            HICON small;
            fixed (char* exe = Environment.ProcessPath ?? "")
                PInvoke.ExtractIconEx(exe, 0, null, &small, 1);
            _icon = small;
        }
        var nid = TrayData();
        nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP;
        nid.uCallbackMessage = WM_APP_TRAY;
        nid.hIcon = _icon;
        nid.szTip = _hotkeyOk ? $"Spoke · {ActiveHotkey}" : "Spoke";
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &nid);
    }

    private NOTIFYICONDATAW TrayData() => new() { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _tray, uID = 1 };

    private void Balloon(string title, string text)
    {
        var nid = TrayData();
        nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_INFO;
        nid.szInfoTitle = title;
        nid.szInfo = text;
        nid.dwInfoFlags = NOTIFY_ICON_INFOTIP_FLAGS.NIIF_WARNING;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &nid);
    }

    private void OnTray(uint mouseMsg)
    {
        if (mouseMsg == PInvoke.WM_LBUTTONDBLCLK) { Open(Stopwatch.GetTimestamp()); return; }
        if (mouseMsg != PInvoke.WM_RBUTTONUP && mouseMsg != PInvoke.WM_CONTEXTMENU) return;

        HMENU menu = PInvoke.CreatePopupMenu();
        Append(menu, CmdOpen, _hotkeyOk ? $"Open wheel\t{ActiveHotkey}" : "Open wheel");
        Append(menu, CmdClips, "Open Clips (copied images)");
        HMENU themes = PInvoke.CreatePopupMenu();
        for (int i = 0; i < Theme.All.Length; i++) Append(themes, CmdTheme0 + i, Theme.All[i].Name);
        int current = Array.IndexOf(Theme.All, Renderer.Theme);
        PInvoke.CheckMenuRadioItem(themes, (uint)CmdTheme0, (uint)(CmdTheme0 + Theme.All.Length - 1), (uint)(CmdTheme0 + current), 0 /* MF_BYCOMMAND */);
        fixed (char* t = "Theme") PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_POPUP, (nuint)(nint)themes.Value, t);
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null);
        int n = _clips.Count;
        Append(menu, CmdClearClips, n > 0 ? $"Clear image history ({n})" : "Clear image history", enabled: n > 0);
        Append(menu, CmdPause, _paused ? "Resume" : "Pause (wheel and image history)");
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null);
        Append(menu, CmdQuit, "Quit Spoke");
        PInvoke.GetCursorPos(out var pt);
        PInvoke.SetForegroundWindow(_tray);   // required so the menu closes when clicking elsewhere
        int cmd = PInvoke.TrackPopupMenu(menu, TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY,
            pt.X, pt.Y, 0, _tray, null).Value;
        PInvoke.DestroyMenu(menu);   // destroys the submenu too
        if (cmd != 0) OnCommand(cmd);
    }

    private static void Append(HMENU menu, int id, string text, bool enabled = true)
    {
        fixed (char* t = text) PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_STRING | (enabled ? 0 : MENU_ITEM_FLAGS.MF_GRAYED), (nuint)id, t);
    }

    private void OnCommand(int id)
    {
        switch (id)
        {
            case CmdOpen: Open(Stopwatch.GetTimestamp()); break;
            case CmdClips: Open(Stopwatch.GetTimestamp(), clips: true); break;
            case CmdPause: _paused = !_paused; if (_paused) Close(); break;
            case CmdClearClips:
                _clips.Clear();
                ReleaseThumbs();
                break;
            case CmdQuit: Quit(); break;
            case >= CmdTheme0 when id < CmdTheme0 + Theme.All.Length:
                var theme = Theme.All[id - CmdTheme0];
                Renderer.SetTheme(theme);
                _settings.Theme = theme.Name;
                _settings.Save();
                Open(Stopwatch.GetTimestamp());   // show it
                break;
        }
    }

    public void Quit()
    {
        Close();
        _capture.End();
        PInvoke.RemoveClipboardFormatListener(_tray);
        var nid = TrayData();
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, &nid);
        if (_hotkeyOk) PInvoke.UnregisterHotKey(Overlay, HotkeyId);
        PInvoke.PostQuitMessage(0);
    }

    public static void RunMessageLoop()
    {
        MSG msg;
        while (PInvoke.GetMessage(&msg, HWND.Null, 0, 0).Value > 0)
        {
            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }
    }
}
