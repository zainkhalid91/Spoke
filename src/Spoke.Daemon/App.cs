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
internal sealed unsafe partial class App
{
    public static App? Instance;

    // Private window messages (WM_APP + 1/2 are KeyHook's).
    public const uint WM_APP_TRAY = PInvoke.WM_APP + 3;    // tray icon callback
    public const uint WM_APP_QUIT = PInvoke.WM_APP + 4;
    public const uint WM_APP_CLIPS = PInvoke.WM_APP + 5;   // image history changed (from the worker)
    public const uint WM_APP_CONFIG = PInvoke.WM_APP + 6;  // settings.json changed on disk
    public const uint WM_APP_ICONS = PInvoke.WM_APP + 7;   // app icons extracted
    public const uint WM_APP_NOTICE = PInvoke.WM_APP + 8;  // a toast from the action thread
    public const uint WM_APP_PIPE = PInvoke.WM_APP + 9;    // a request from Spoke Settings

    private const int HotkeyId = 1;
    /// <summary>Hotkey ids for wheels 2…n are this plus the wheel's index.</summary>
    private const int WheelHotkeyBase = 10;
    private const nuint TrimTimerId = 7, ClipTimerId = 8, ConfigTimerId = 9;
    private const uint TrimAfterMs = 60_000;   // give memory back to Windows a minute after the wheel closes
    private const int CmdOpen = 100, CmdPause = 101, CmdQuit = 102, CmdClips = 103, CmdClearClips = 104, CmdEditSettings = 105, CmdAutostart = 106, CmdSettings = 107, CmdTheme0 = 110, CmdThemeAuto = 115, CmdAccentTheme = 120, CmdAccentWindows = 121, CmdSize0 = 130, CmdWheel0 = 140;
    private const uint WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;

    public HWND Overlay { get; private set; }
    private readonly HWND _tray;
    private uint _taskbarCreated;
    private HICON _icon;

    public readonly Gfx Gfx;
    public readonly WheelRenderer Renderer;
    private readonly CaptureOverlay _capture;
    private readonly Toast _toast;
    private SliceDef[] _slices => Renderer.Slices;
    /// <summary>Every configured wheel, and the one showing (or shown last).</summary>
    private List<WheelSet> _wheelSets = [];
    private int _wheel;
    /// <summary>Registered hotkeys of wheels 2…n: hotkey id → wheel index.</summary>
    private readonly Dictionary<int, int> _wheelHotkeys = [];
    private WheelLayout _layout => Renderer.Layout;
    private readonly Settings _settings;
    private PipeServer? _pipe;
    /// <summary>Problems in settings.json the daemon is ignoring right now (reported to Spoke Settings).</summary>
    private IReadOnlyList<string> _configErrors = [];
    private readonly Actions _actions;
    private readonly IconLoader _icons;
    private int _configGeneration;
    private readonly string? _hotkeyOverride;
    private Appearance _look = Appearance.Default;
    private string _themeKey = "";
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Title, string Detail, bool Error)> _notices = new();

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
        VIRTUAL_KEY.VK_ESCAPE, VIRTUAL_KEY.VK_RETURN, VIRTUAL_KEY.VK_LEFT, VIRTUAL_KEY.VK_RIGHT, VIRTUAL_KEY.VK_UP, VIRTUAL_KEY.VK_DOWN, VIRTUAL_KEY.VK_TAB, .. KeyHook.Digits,
    ];
    private static readonly VIRTUAL_KEY[] ClipsKeys =
    [
        .. WheelKeys, VIRTUAL_KEY.VK_BACK, VIRTUAL_KEY.VK_DELETE, VIRTUAL_KEY.VK_PRIOR, VIRTUAL_KEY.VK_NEXT,
    ];

    public App(bool forceWarp, string? preferredHotkey = null)
    {
        Instance = this;
        _hotkeyOverride = preferredHotkey;
        _settings = new Settings();
        _settings.Load(out var configErrors);
        var cfg = _settings.Current;

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
        _wheelSets = BuildWheels(cfg);
        Renderer = new WheelRenderer(Gfx, Overlay, _wheelSets[0].Slices);
        SelectWheel(0);
        ApplyAppearance(cfg.Appearance);
        _actions = new Actions(_tray, Notify);
        _icons = new IconLoader(_tray);
        _capture = new CaptureOverlay(Gfx, this, captureHwnd);
        _toast = new Toast(Gfx, this, toastHwnd);
        Log.Write(DropTarget.Register(Overlay, this) ? "Drop target ready (drag files, then press the shortcut)" : "Drop mode unavailable (couldn't register the overlay as a drop target).");

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
        var notice = RegisterHotkey(preferredHotkey ?? _wheelSets[0].Hotkey);
        notice ??= RegisterWheelHotkeys();
        AddTrayIcon();
        if (notice is { } n) Balloon(n.Title, n.Text);
        if (configErrors.Count > 0) ShowConfigErrors(configErrors);
        LoadIcons();
        _settings.Watch(_tray);
        if (!BenchMode) _pipe = new PipeServer(_tray);
        ScheduleTrim();
    }

    // ───────────────────────── settings.json ─────────────────────────

    /// <summary>Called on the UI thread when the file changes (debounced): applies a good file, reports a bad one.</summary>
    private void ReloadConfig(bool quiet = false)
    {
        if (!_settings.Load(out var errors))
        {
            if (errors.Count > 0)
            {
                if (quiet) _configErrors = errors; else ShowConfigErrors(errors);
            }
            return;
        }
        _configErrors = [];
        var cfg = _settings.Current;
        if (IsOpen) Close();
        ReleaseIcons();
        string showing = _wheelSets.Count > _wheel ? _wheelSets[_wheel].Name : "";
        _wheelSets = BuildWheels(cfg);
        SelectWheel(Math.Max(0, _wheelSets.FindIndex(w => w.Name == showing)));
        ApplyAppearance(cfg.Appearance);
        if (RegisterWheelHotkeys() is { } wn && !quiet) Notify(wn.Title, wn.Text, true);
        string? want = _hotkeyOverride ?? _wheelSets[0].Hotkey;
        if (want != null && Hotkey.TryParse(want, out var hk) && (!_hotkeyOk || hk != ActiveHotkey))
        {
            if (_hotkeyOk) PInvoke.UnregisterHotKey(Overlay, HotkeyId);
            _hotkeyOk = false;
            if (RegisterHotkey(want) is { } n && !quiet) Notify(n.Title, n.Text, true);
            UpdateTrayTip();
        }
        LoadIcons();
        Log.Write($"settings.json reloaded: {_wheelSets.Count} wheel(s), {cfg.Slices.Count} slices on the first");
        if (!quiet) Notify("Settings updated", _wheelSets.Count > 1
            ? $"{_wheelSets.Count} wheels · {(_hotkeyOk ? ActiveHotkey.ToString() : "no shortcut")} · Tab switches"
            : $"{cfg.WheelName} · {cfg.Slices.Count} slices · {(_hotkeyOk ? ActiveHotkey.ToString() : "no shortcut")}", false);
    }

    private static List<WheelSet> BuildWheels(ConfigResult cfg) =>
        [.. cfg.Wheels.Select(w => new WheelSet(w.Name, w.Hotkey, SliceDef.From(w.Slices)))];

    /// <summary>Makes wheel <paramref name="w"/> the one the renderer shows (no drawing).</summary>
    private void SelectWheel(int w)
    {
        _wheel = w;
        Renderer.SetSlices(_wheelSets[w].Slices);
        Renderer.WheelName = _wheelSets[w].Name;
        Renderer.SetRow([.. _wheelSets.Select(s => s.Name)], w);
    }

    /// <summary>Tab, a pill or a wheel's own shortcut while the wheel is open: swap wheels in place.</summary>
    private void SwitchWheel(int w)
    {
        if (w == _wheel || w < 0 || w >= _wheelSets.Count) return;
        long t0 = Stopwatch.GetTimestamp();
        if (_view.Query) { _view.Query = false; _view.QueryText = ""; KeyHook.TextMode = false; }
        ResetSubs();
        SelectWheel(w);
        _view.Hover = -1;
        _view.RowHover = -1;
        Renderer.Draw(_view);
        Renderer.Commit();
        Log.Write($"Wheel: {_wheelSets[w].Name} in {Log.Ms(t0, Stopwatch.GetTimestamp()):0.0} ms");
    }

    /// <summary>Shortcuts of wheels 2…n. Returns a notice naming any that another app holds.</summary>
    private (string Title, string Text)? RegisterWheelHotkeys()
    {
        foreach (int id in _wheelHotkeys.Keys) PInvoke.UnregisterHotKey(Overlay, id);
        _wheelHotkeys.Clear();
        var taken = new List<string>();
        for (int i = 1; i < _wheelSets.Count; i++)
        {
            if (_wheelSets[i].Hotkey is not { } text || !Hotkey.TryParse(text, out var hk)) continue;
            int id = WheelHotkeyBase + i;
            if (PInvoke.RegisterHotKey(Overlay, id, (HOT_KEY_MODIFIERS)(uint)hk.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT, hk.VirtualKey))
                _wheelHotkeys[id] = i;
            else taken.Add($"{hk} ({_wheelSets[i].Name})");
        }
        if (taken.Count == 0) return null;
        Log.Write($"Wheel shortcut(s) used by another app: {string.Join(", ", taken)}");
        return ("Shortcut in use", $"{string.Join(", ", taken)} is used by another app. Open that wheel with Tab from another wheel.");
    }

    /// <summary>
    /// Theme (or Windows light/dark for "Auto"), slice colours (theme palette, Windows accent or custom) and size.
    /// Re-run when settings change and when Windows' own colours change.
    /// </summary>
    private void ApplyAppearance(Appearance a)
    {
        _look = a;
        var t = Theme.ByName(a.ThemeFor(SystemLook.AppsUseLightTheme));
        uint rgb = a.Accent switch { AccentMode.Windows => SystemLook.AccentRgb, AccentMode.Custom => a.AccentRgb, _ => 0 };
        if (a.Accent != AccentMode.Theme) t = t.WithAccent(rgb);
        string key = $"{t.Name}|{a.Accent}|{rgb:X6}";
        if (key != _themeKey)
        {
            _themeKey = key;
            Renderer.SetTheme(t);
            Log.Write($"Look: {t.Name}{(a.FollowsSystem ? " (matches Windows)" : "")}, colours {a.Accent}{(rgb != 0 ? $" #{rgb:X6}" : "")}, size {a.SizeName}");
        }
        if (Renderer.Scale != a.Scale)
        {
            if (IsOpen) Close();
            Renderer.Scale = a.Scale;   // takes effect at the next open (SetDpi)
        }
        bool ring = a.SubMenus == SubMenuStyle.Ring;
        if (ring != UseRing)
        {
            if (IsOpen) Close();
            UseRing = ring;
            Renderer.SetRingMode(ring);
            Log.Write($"Sub-menus: {(ring ? "outer ring" : "new wheel")}");
        }
    }

    private void OnSystemColorsChanged()
    {
        if (_look.FollowsSystem || _look.Accent == AccentMode.Windows) ApplyAppearance(_look);
    }

    private void ShowConfigErrors(IReadOnlyList<string> errors)
    {
        _configErrors = errors;
        Notify(errors.Count == 1 ? "settings.json has a problem" : $"settings.json has {errors.Count} problems",
            errors[0] + " Spoke keeps using your last working settings.", true);
    }

    /// <summary>Every slice whose icon may load, in the order <see cref="IconLoader"/> results refer to.</summary>
    private SliceDef[] _iconSlices = [];

    private void LoadIcons()
    {
        int gen = ++_configGeneration;
        var apps = new List<(int, string)>();
        _iconSlices = [.. SliceDef.Flatten(_wheelSets.SelectMany(w => w.Slices))];
        for (int i = 0; i < _iconSlices.Length; i++)
            if (_iconSlices[i].Def.Kind == SliceKind.App && _iconSlices[i].Def.Icon == null) apps.Add((i, _iconSlices[i].Def.Target));
        _icons.Load(gen, apps);
    }

    private void OnIconsReady()
    {
        bool any = false;
        var root = _iconSlices;
        while (_icons.Results.TryDequeue(out var r))
        {
            if (r.Generation != _configGeneration || r.Index >= root.Length) continue;
            var s = root[r.Index];
            Gfx.Release(s.Icon);
            fixed (byte* d = r.Pixels.Data) s.Icon = Gfx.Bitmap(d, r.Pixels.Width, r.Pixels.Height, r.Pixels.Stride);
            any = true;
        }
        if (!any || _subStack.Count > 0) return;   // a sub-wheel is showing; the main wheel re-caches on close
        Renderer.ReleaseCache();
        if (IsOpen) Renderer.Draw(_view);
    }

    private void ReleaseIcons()
    {
        foreach (var s in SliceDef.Flatten(_wheelSets.SelectMany(w => w.Slices))) { Gfx.Release(s.Icon); s.Icon = null; }
    }

    /// <summary>Thread-safe: queue a toast for the UI thread.</summary>
    private void Notify(string title, string detail, bool error)
    {
        _notices.Enqueue((title, detail, error));
        PInvoke.PostMessage(_tray, WM_APP_NOTICE, default, default);
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

            case PInvoke.WM_HOTKEY when (int)wParam.Value == HotkeyId || _wheelHotkeys.ContainsKey((int)wParam.Value):
            {
                int w = (int)wParam.Value == HotkeyId ? 0 : _wheelHotkeys[(int)wParam.Value];
                if (IsOpen && (w == _wheel || _view.Clips)) Close();
                else if (IsOpen) SwitchWheel(w);
                else if (_capture!.IsOpen) _capture.End();
                else if (!TryOpenDrop()) Open(Stopwatch.GetTimestamp(), wheel: w);
                return true;
            }

            case PInvoke.WM_MOUSEMOVE when hwnd == Overlay && IsOpen && _view.Query:
                return true;   // hover is frozen while typing

            case PInvoke.WM_LBUTTONUP when hwnd == Overlay && IsOpen && _view.Query:
                if (IsOutsideClient(lParam)) Close(); else ExitQuery();
                return true;

            case PInvoke.WM_MOUSEMOVE when hwnd == Overlay && IsOpen && Renderer.Ring != null:
            {
                int item = RingHitClient(lParam);
                SetHovers(item >= 0 ? -1 : HitTestClient(lParam), item);
                return true;
            }

            case PInvoke.WM_LBUTTONUP when hwnd == Overlay && IsOpen && Renderer.Ring != null:
            {
                int item = RingHitClient(lParam);
                if (item >= 0) { RunRingItem(item); return true; }
                int s = HitTestClient(lParam);
                if (s == Renderer.Ring.Parent) ExitSub();               // the open slice again: fold the band
                else if (s >= 0) { CloseBand(); Run(s, copyOnly: (wParam.Value & 0x0004) != 0); }
                else if (IsOutsideClient(lParam)) Close();
                else ExitSub();                                          // the hub goes back
                return true;
            }

            case PInvoke.WM_MOUSEMOVE when hwnd == Overlay && IsOpen:
            {
                int row = RowHitClient(lParam);
                if (row != _view.RowHover)
                {
                    _view.RowHover = row;
                    _view.Hover = row >= 0 ? -1 : HitTestClient(lParam);
                    Renderer.Draw(_view);
                    return true;
                }
                SetHover(HitTestClient(lParam));
                return true;
            }

            case PInvoke.WM_LBUTTONUP when hwnd == Overlay && IsOpen && RowHitClient(lParam) is >= 0 and var pill:
                SwitchWheel(pill);
                return true;

            case PInvoke.WM_LBUTTONUP when hwnd == Overlay && IsOpen:
            {
                int s = HitTestClient(lParam);
                bool shift = (wParam.Value & 0x0004) != 0;   // MK_SHIFT
                if (s >= 0) Run(s, copyOnly: shift);
                else if (_view.Clips && !IsOutsideClient(lParam)) ExitClips();   // the hub goes back
                else if (IsOutsideClient(lParam) || !ExitSub()) Close();
                return true;
            }

            case PInvoke.WM_RBUTTONUP when hwnd == Overlay && IsOpen:
                if (_view.Query) ExitQuery(); else if (_view.Clips) ExitClips(); else if (!ExitSub()) Close();
                return true;

            case PInvoke.WM_MOUSEWHEEL when hwnd == Overlay && IsOpen && _view.Clips:
                TurnPage((short)((wParam.Value >> 16) & 0xFFFF) > 0 ? -1 : 1);
                return true;

            case KeyHook.WM_APP_KEY when hwnd == Overlay && IsOpen && _dragging:
                // Mid-drag only Esc counts (it closes the wheel; the drag carries on). Choosing is done by dropping.
                if ((VIRTUAL_KEY)(ushort)wParam.Value == VIRTUAL_KEY.VK_ESCAPE) Close();
                return true;

            case KeyHook.WM_APP_KEY when hwnd == Overlay && IsOpen:
                if (_view.Query) OnQueryKey((uint)wParam.Value, (uint)(lParam.Value & 0xFFFF));
                else OnKey((VIRTUAL_KEY)(ushort)wParam.Value);
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

            case PInvoke.WM_SETTINGCHANGE when hwnd == _tray:
            case WM_DWMCOLORIZATIONCOLORCHANGED when hwnd == _tray:
                OnSystemColorsChanged();
                return false;
            case WM_APP_CONFIG when hwnd == _tray:
                PInvoke.SetTimer(_tray, ConfigTimerId, 250, null);   // editors write in bursts
                return true;

            case PInvoke.WM_TIMER when hwnd == _tray && wParam.Value == ConfigTimerId:
                PInvoke.KillTimer(_tray, ConfigTimerId);
                ReloadConfig();
                return true;

            case WM_APP_PIPE when hwnd == _tray:
                ServePipe();
                return true;

            case WM_APP_ICONS when hwnd == _tray:
                OnIconsReady();
                return true;

            case WM_APP_NOTICE when hwnd == _tray:
                while (_notices.TryDequeue(out var nt)) _toast!.ShowMessage(nt.Title, nt.Detail, nt.Error);
                return true;

            case WM_APP_CLIPS when hwnd == _tray:
                if (IsOpen && _view.Clips) { LoadClipThumbs(); Renderer.Draw(_view); Renderer.Commit(); }
                return true;

            case PInvoke.WM_TIMER when hwnd == _tray && wParam.Value == DropTimerId:
                OnDropTimer();
                return true;

            case WM_APP_ESTIMATE when hwnd == _tray:
                OnEstimate();
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

    public void Open(long hotkeyTicks, bool clips = false, int wheel = -1)
    {
        if (_paused) return;
        if (wheel >= 0 && wheel != _wheel && wheel < _wheelSets.Count) SelectWheel(wheel);
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
        float reach = UseRing ? RingLayout.Reach(_layout.OuterRadius) : _layout.OuterRadius + WheelRenderer.Lift;
        if (Renderer.HasRow) reach = MathF.Max(reach, Renderer.RowBottom);
        int ringPx = (int)MathF.Ceiling((reach + 4) * Renderer.Dpi / 96f);
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
        EndDrop();
        ResetSubs();
        _view.Hover = -1;
        _view.RowHover = -1;
        _view.Clips = false;
        _view.Query = false;
        _view.QueryText = "";
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
        float dx = (x - _centerX) * scale, dy = (y - _centerY) * scale;
        if (Renderer.Ring is { } band && band.Layout.HitTest(dx, dy) >= 0) return false;
        return _layout.IsOutside(dx, dy);
    }

    private int RowHitClient(LPARAM lParam)
    {
        if (!Renderer.HasRow || _view.Clips || _view.Query || _view.Drop) return -1;
        int x = (short)(lParam.Value & 0xFFFF) + _work.left, y = (short)((lParam.Value >> 16) & 0xFFFF) + _work.top;
        float scale = 96f / Renderer.Dpi;
        return Renderer.RowHit((x - _centerX) * scale, (y - _centerY) * scale);
    }

    private int HitTestScreen(int x, int y)
    {
        float scale = 96f / Renderer.Dpi;
        return _layout.HitTest((x - _centerX) * scale, (y - _centerY) * scale);
    }

    private void OnKey(VIRTUAL_KEY vk)
    {
        if (vk == VIRTUAL_KEY.VK_TAB && !_view.Clips && !_view.Drop)
        {
            int n = _wheelSets.Count;
            if (n > 1) SwitchWheel((_wheel + (KeyHook.IsDown(VIRTUAL_KEY.VK_SHIFT) ? n - 1 : 1)) % n);
            return;
        }
        if (Renderer.Ring is { } band && !_view.Clips) { OnRingKey(vk, band); return; }
        switch (vk)
        {
            case VIRTUAL_KEY.VK_ESCAPE:
            case VIRTUAL_KEY.VK_BACK:
                if (_view.Clips) ExitClips(); else if (!ExitSub()) Close();
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
        if (s.OnRun != null || s.OnOpen != null) { RunGenerated(s); return; }
        RunDef(s, slice);
    }

    /// <summary>
    /// Runs a configured slice: one on the wheel (<paramref name="slice"/> is its index) or a group's child
    /// chosen from the band (<paramref name="slice"/> is the group's slice, which the search box then belongs to).
    /// </summary>
    private void RunDef(SliceDef s, int slice)
    {
        if (s.Def.Kind == SliceKind.Search) { CloseBand(); EnterQuery(slice, s.Def); return; }
        if (s.Def.Kind == SliceKind.Group)
        {
            if (s.Def.RunAll) { Close(); _actions.RunAll(s.Def); }
            else if (!UseRing && s.Children.Length < ConfigLoader.MinSlices) RunDef(s.Children[0], slice);   // one child: no wheel of one
            else EnterSub(new SubWheel(s.Label, s.Children), slice);
            return;
        }
        switch (s.Tool)
        {
            case ToolKind.Clips:
                CloseBand();
                EnterClips();
                return;
            case ToolKind.Screenshot:
                Close();
                _capture.Begin(CaptureMode.Screenshot);
                return;
            case ToolKind.ColorPicker:
                Close();
                _capture.Begin(CaptureMode.ColorPicker);
                return;
            case ToolKind.Files or ToolKind.TextTools or ToolKind.Generate or ToolKind.System:
                if (ToolWheel(s.Tool) is { } w) EnterSub(w, slice);
                return;
            default:
                Close();
                _actions.Run(s.Def);
                return;
        }
    }

    // ───────────────────────── search box ─────────────────────────

    private Slice? _queryDef;

    private void EnterQuery(int slice, Slice def)
    {
        _queryDef = def;
        _view.Hover = slice;
        _view.Query = true;
        _view.QueryText = "";
        _view.QueryEngine = SearchEngines.NameOf(def.SearchTemplate);
        KeyHook.TextMode = true;
        Renderer.Draw(_view);
    }

    private void ExitQuery()
    {
        _view.Query = false;
        _view.QueryText = "";
        KeyHook.TextMode = false;
        Renderer.Draw(_view);
    }

    private void OnQueryKey(uint vk, uint scan)
    {
        bool ctrl = KeyHook.IsDown(VIRTUAL_KEY.VK_CONTROL), alt = KeyHook.IsDown(VIRTUAL_KEY.VK_MENU);
        string q = _view.QueryText;
        switch ((VIRTUAL_KEY)vk)
        {
            case VIRTUAL_KEY.VK_ESCAPE:
                ExitQuery();
                return;
            case VIRTUAL_KEY.VK_RETURN:
                if (q.Trim().Length == 0) return;
                var slice = _queryDef!;
                Close();
                _actions.Search(slice, q);
                return;
            case VIRTUAL_KEY.VK_BACK:
                if (q.Length == 0) return;
                if (ctrl)
                {
                    // Ctrl+Backspace: delete the last word.
                    string t = q.TrimEnd();
                    int sp = t.LastIndexOf(' ');
                    q = sp < 0 ? "" : t[..(sp + 1)];
                }
                else q = q[..^(char.IsLowSurrogate(q[^1]) && q.Length > 1 ? 2 : 1)];
                break;
            case VIRTUAL_KEY.VK_V when ctrl && !alt:
                q += (ClipboardIO.GetText(_tray) ?? "").ReplaceLineEndings(" ");
                break;
            default:
                if (ctrl && !alt) return;   // other shortcuts do nothing here (AltGr = Ctrl+Alt still types)
                q += KeyHook.TextOf(vk, scan);
                break;
        }
        if (q.Length > 500) q = q[..500];
        if (q == _view.QueryText) return;
        _view.QueryText = q;
        Renderer.Draw(_view);
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
        nid.szTip = (_hotkeyOk ? $"Spoke · {ActiveHotkey}" : "Spoke") + (_keepAwake ? " · keeping awake" : "");
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &nid);
    }

    private void UpdateTrayTip()
    {
        var nid = TrayData();
        nid.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_TIP;
        nid.szTip = _hotkeyOk ? $"Spoke · {ActiveHotkey}" : "Spoke";
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &nid);
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
        if (mouseMsg == PInvoke.WM_LBUTTONDBLCLK) { OpenSettings(); return; }
        if (mouseMsg != PInvoke.WM_RBUTTONUP && mouseMsg != PInvoke.WM_CONTEXTMENU) return;

        HMENU menu = PInvoke.CreatePopupMenu();
        if (_wheelSets.Count > 1)
        {
            HMENU wheels = PInvoke.CreatePopupMenu();
            for (int i = 0; i < _wheelSets.Count; i++)
            {
                string? key = i == 0 ? (_hotkeyOk ? ActiveHotkey.ToString() : null)
                    : _wheelHotkeys.ContainsValue(i) && Hotkey.TryParse(_wheelSets[i].Hotkey, out var hk) ? hk.ToString() : null;
                Append(wheels, CmdWheel0 + i, key is null ? _wheelSets[i].Name : $"{_wheelSets[i].Name}\t{key}");
            }
            SubMenu(menu, wheels, "Open wheel");
        }
        else Append(menu, CmdOpen, _hotkeyOk ? $"Open wheel\t{ActiveHotkey}" : "Open wheel");
        Append(menu, CmdClips, "Open Clips (copied images)");
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null);
        HMENU themes = PInvoke.CreatePopupMenu();
        Append(themes, CmdThemeAuto, $"Match Windows ({_look.LightTheme} / {_look.DarkTheme})", check: _look.FollowsSystem);
        PInvoke.AppendMenu(themes, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null);
        for (int i = 0; i < Theme.All.Length; i++) Append(themes, CmdTheme0 + i, Theme.All[i].Name, check: !_look.FollowsSystem && _look.Theme == Theme.All[i].Name);
        SubMenu(menu, themes, "Theme");
        HMENU colours = PInvoke.CreatePopupMenu();
        Append(colours, CmdAccentTheme, "Theme colours", check: _look.Accent == AccentMode.Theme);
        Append(colours, CmdAccentWindows, "Windows accent colour", check: _look.Accent == AccentMode.Windows);
        if (_look.Accent == AccentMode.Custom) Append(colours, 0, $"Custom #{_look.AccentRgb:X6} (settings.json)", enabled: false, check: true);
        SubMenu(menu, colours, "Colours");
        HMENU sizes = PInvoke.CreatePopupMenu();
        for (int i = 0; i < Appearance.Sizes.Length; i++)
        {
            var (name, scale) = Appearance.Sizes[i];
            string label = name switch { "S" => "Small", "M" => "Medium", "L" => "Large", _ => "Extra large" };
            Append(sizes, CmdSize0 + i, $"{label}\t{scale * 100:0}%", check: MathF.Abs(_look.Scale - scale) < 0.001f);
        }
        SubMenu(menu, sizes, "Size");
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null);
        int n = _clips.Count;
        Append(menu, CmdClearClips, n > 0 ? $"Clear image history ({n})" : "Clear image history", enabled: n > 0);
        Append(menu, CmdPause, _paused ? "Resume" : "Pause (wheel and image history)");
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null);
        Append(menu, CmdSettings, "Settings…");
        Append(menu, CmdEditSettings, "Edit settings.json…");
        Append(menu, CmdAutostart, "Start with Windows", check: Autostart.IsEnabled);
        PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (PCWSTR)null);
        Append(menu, CmdQuit, "Quit Spoke");
        PInvoke.GetCursorPos(out var pt);
        PInvoke.SetForegroundWindow(_tray);   // required so the menu closes when clicking elsewhere
        int cmd = PInvoke.TrackPopupMenu(menu, TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_NONOTIFY,
            pt.X, pt.Y, 0, _tray, null).Value;
        PInvoke.DestroyMenu(menu);   // destroys the submenu too
        if (cmd != 0) OnCommand(cmd);
    }

    private static void SubMenu(HMENU menu, HMENU sub, string text)
    {
        fixed (char* t = text) PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_POPUP, (nuint)(nint)sub.Value, t);
    }

    /// <summary>A look change from the tray: apply now, save to settings.json, and show the wheel.</summary>
    private void SetLook(Appearance look, string key, string value)
    {
        ApplyAppearance(look);
        _settings.SaveValue(key, value);
        Open(Stopwatch.GetTimestamp());
    }

    private static void Append(HMENU menu, int id, string text, bool enabled = true, bool check = false)
    {
        var flags = MENU_ITEM_FLAGS.MF_STRING | (enabled ? 0 : MENU_ITEM_FLAGS.MF_GRAYED) | (check ? MENU_ITEM_FLAGS.MF_CHECKED : 0);
        fixed (char* t = text) PInvoke.AppendMenu(menu, flags, (nuint)id, t);
    }

    private void OnCommand(int id)
    {
        switch (id)
        {
            case CmdOpen: Open(Stopwatch.GetTimestamp(), wheel: 0); break;
            case >= CmdWheel0 when id < CmdWheel0 + ConfigLoader.MaxWheels: Open(Stopwatch.GetTimestamp(), wheel: id - CmdWheel0); break;
            case CmdClips: Open(Stopwatch.GetTimestamp(), clips: true); break;
            case CmdPause: _paused = !_paused; if (_paused) Close(); break;
            case CmdClearClips:
                _clips.Clear();
                ReleaseThumbs();
                break;
            case CmdQuit: Quit(); break;
            case CmdSettings: OpenSettings(); break;
            case CmdEditSettings:
                _actions.Run(new Slice(SliceKind.App, "settings.json", "", null, Target: "notepad.exe", Args: "\"" + Settings.FilePath + "\""));
                break;
            case CmdAutostart:
                if (!Autostart.Set(!Autostart.IsEnabled)) Notify("Couldn't change start-up", "Windows didn't allow the change.", true);
                break;
            case >= CmdTheme0 when id < CmdTheme0 + Theme.All.Length:
                string theme = Theme.All[id - CmdTheme0].Name;
                SetLook(_look with { Theme = theme }, "theme", theme);
                break;
            case CmdThemeAuto: SetLook(_look with { Theme = Appearance.Auto }, "theme", Appearance.Auto); break;
            case CmdAccentTheme: SetLook(_look with { Accent = AccentMode.Theme }, "accent", "theme"); break;
            case CmdAccentWindows: SetLook(_look with { Accent = AccentMode.Windows }, "accent", "windows"); break;
            case >= CmdSize0 when id < CmdSize0 + Appearance.Sizes.Length:
                var size = Appearance.Sizes[id - CmdSize0];
                SetLook(_look with { Scale = size.Scale }, "size", size.Name);
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
        foreach (int id in _wheelHotkeys.Keys) PInvoke.UnregisterHotKey(Overlay, id);
        _pipe?.Stop();
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
