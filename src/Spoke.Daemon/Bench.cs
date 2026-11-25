using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Spoke.Daemon;

/// <summary>
/// `Spoke.exe --bench N [--label text]`: drives the real input path N times (SendInput Ctrl+Alt+Space →
/// RegisterHotKey → WM_HOTKEY → draw/present/commit → DWM) against a separate focus-target process,
/// checks focus never moves, and writes a report to %LOCALAPPDATA%\Spoke\bench-*.md.
/// </summary>
internal static unsafe class Bench
{
    public static void Start(App app, int runs, string label, bool trim)
    {
        app.BenchMode = true;
        var t = new Thread(() =>
        {
            try { Run(app, runs, label, trim); }
            catch (Exception ex) { Log.Write("Bench failed: " + ex); }
            finally { PInvoke.PostMessage(app.Overlay, App.WM_APP_QUIT, default, default); }
        }) { IsBackground = true, Name = "bench" };
        t.Start();
    }

    private static void Run(App app, int runs, string label, bool trim)
    {
        // 1. A separate process with a focused text box: the "user's app" that must keep focus.
        var target = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--focus-target") { UseShellExecute = false })!;
        HWND targetWnd = default, edit = default;
        for (int i = 0; i < 100 && targetWnd.IsNull; i++)
        {
            Thread.Sleep(50);
            fixed (char* cls = FocusTarget.ClassName) targetWnd = PInvoke.FindWindow(cls, (PCWSTR)null);
        }
        Thread.Sleep(600);
        fixed (char* ecls = "Edit") edit = PInvoke.FindWindowEx(targetWnd, HWND.Null, ecls, (PCWSTR)null);

        Thread.Sleep(500);
        var lat = new List<(double sendToHotkey, double hotkeyToShown, double shownToDwm, double total)>();
        int focusLost = 0, misses = 0;
        HWND fgStart = PInvoke.GetForegroundWindow();
        string fgTitle = Title(fgStart);
        var rng = new Random(7);

        for (int i = 0; i < runs; i++)
        {
            app.Hidden.Wait(2000);
            HWND fgBefore = PInvoke.GetForegroundWindow();
            HWND focusBefore = Focus(fgBefore);

            if (trim) { TrimWorkingSet(); Thread.Sleep(30); }   // simulate being paged out under memory pressure
            long tSend = Stopwatch.GetTimestamp();
            SendHotkey(app.ActiveHotkey);
            if (!app.Shown.Wait(1000)) { misses++; continue; }

            lat.Add((Log.Ms(tSend, app.LastHotkeyTicks), Log.Ms(app.LastHotkeyTicks, app.LastShownTicks),
                     Log.Ms(app.LastShownTicks, app.LastDwmTicks), Log.Ms(tSend, app.LastDwmTicks)));

            HWND fgDuring = PInvoke.GetForegroundWindow();
            if (fgDuring != fgBefore || Focus(fgDuring) != focusBefore || fgDuring == app.Overlay) focusLost++;

            // Exercise the keyboard path while open: two arrow presses (redraw), then Esc (close).
            Thread.Sleep(25);
            SendKey(VIRTUAL_KEY.VK_RIGHT);
            Thread.Sleep(15);
            SendKey(VIRTUAL_KEY.VK_RIGHT);
            Thread.Sleep(15);
            SendKey(VIRTUAL_KEY.VK_ESCAPE);
            if (!app.Hidden.Wait(1000)) { misses++; }

            if (PInvoke.GetForegroundWindow() != fgBefore) focusLost++;
            Thread.Sleep(40 + rng.Next(80));   // jitter so we don't alias with vsync
        }

        // 2. Type into the target after all those opens: the text must land in its text box.
        const string marker = "spoke focus ok";
        HWND fgNow = PInvoke.GetForegroundWindow();
        Log.Write($"Bench focus: target={(nint)targetWnd.Value:X} edit={(nint)edit.Value:X} fg={(nint)fgNow.Value:X} ('{Title(fgNow)}') focus={(nint)Focus(fgNow).Value:X}");
        foreach (char c in marker) SendUnicode(c);
        Thread.Sleep(300);
        string typed = WindowText(edit);
        bool typedOk = typed.Contains(marker, StringComparison.Ordinal);
        Log.Write($"Bench typed text read back: '{typed}'");
        PInvoke.PostMessage(targetWnd, PInvoke.WM_CLOSE, default, default);

        // 3. Report.
        var info = new DWM_TIMING_INFO { cbSize = (uint)sizeof(DWM_TIMING_INFO) };
        double refreshMs = PInvoke.DwmGetCompositionTimingInfo(HWND.Null, &info).Succeeded ? info.qpcRefreshPeriod * 1000.0 / Stopwatch.Frequency : double.NaN;
        var mem = Memory();

        var sb = new StringBuilder();
        sb.AppendLine($"# Spoke bench {DateTime.Now:yyyy-MM-dd HH:mm:ss} {label}");
        sb.AppendLine();
        if (trim) sb.AppendLine("- **Worst case:** working set emptied before every open (as Windows does under memory pressure)");
        sb.AppendLine($"- Runs: {runs}, completed: {lat.Count}, misses: {misses}");
        sb.AppendLine($"- Renderer: {(app.Gfx.UsingWarp ? "WARP (CPU)" : "GPU (D3D11 hardware)")} on {app.Gfx.AdapterName}, DPI {app.Renderer.Dpi}, surface {app.Renderer.PixelSize}px");
        sb.AppendLine($"- Display refresh period: {refreshMs:F2} ms (the frame is on screen at the next vblank after DWM applies the commit)");
        sb.AppendLine($"- Foreground during test: \"{fgTitle}\"");
        sb.AppendLine($"- Focus changed while open/after close: **{focusLost}** times; text typed after {runs} opens landed in target: **{(typedOk ? "yes" : "NO")}**");
        sb.AppendLine();
        sb.AppendLine("| Stage (ms) | p50 | p95 | p99 | max |");
        sb.AppendLine("|---|---|---|---|---|");
        Row(sb, "SendInput → WM_HOTKEY (OS input path)", lat.Select(l => l.sendToHotkey));
        Row(sb, "WM_HOTKEY → shown (draw+present+commit+SetWindowPos)", lat.Select(l => l.hotkeyToShown));
        Row(sb, "shown → DWM applied commit", lat.Select(l => l.shownToDwm));
        Row(sb, "**SendInput → DWM applied (total)**", lat.Select(l => l.total));
        sb.AppendLine();
        int sc = Math.Max(1, app.StageCount);
        sb.AppendLine($"- Our open path, mean per stage: monitor/DPI/hit-test {app.StagePrep / sc:F2} ms · draw+present {app.StageDraw / sc:F2} ms · DComp commit {app.StageCommit / sc:F2} ms · SetWindowPos show {app.StageShow / sc:F2} ms");
        var r = app.Renderer;
        sb.AppendLine($"- Draw calls: {r.DrawCount}, mean {r.DrawMsTotal / Math.Max(1, r.DrawCount):F3} ms, max {r.DrawMsMax:F3} ms (CPU time to record + present)");
        sb.AppendLine($"- Arrow key → redraw presented: mean {app.KeyToFrameTotal / Math.Max(1, app.KeyToFrameCount):F3} ms, max {app.KeyToFrameMax:F3} ms over {app.KeyToFrameCount} presses");
        sb.AppendLine($"- Memory at end of bench: working set {mem.ws / 1048576.0:F1} MB, private {mem.priv / 1048576.0:F1} MB");

        Directory.CreateDirectory(Log.Dir);
        string path = Path.Combine(Log.Dir, $"bench-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        File.WriteAllText(path, sb.ToString());
        Log.Write("Bench report: " + path);
    }

    private static void Row(StringBuilder sb, string name, IEnumerable<double> xs)
    {
        var a = xs.OrderBy(x => x).ToArray();
        if (a.Length == 0) { sb.AppendLine($"| {name} | – | – | – | – |"); return; }
        double P(double p) => a[Math.Min(a.Length - 1, (int)Math.Ceiling(p * a.Length) - 1)];
        sb.AppendLine($"| {name} | {P(.50):F2} | {P(.95):F2} | {P(.99):F2} | {a[^1]:F2} |");
    }

    public static (ulong ws, ulong priv) Memory()
    {
        var c = new Windows.Win32.System.ProcessStatus.PROCESS_MEMORY_COUNTERS_EX { cb = (uint)sizeof(Windows.Win32.System.ProcessStatus.PROCESS_MEMORY_COUNTERS_EX) };
        PInvoke.GetProcessMemoryInfo(PInvoke.GetCurrentProcess(), (Windows.Win32.System.ProcessStatus.PROCESS_MEMORY_COUNTERS*)&c, c.cb);
        return (c.WorkingSetSize, c.PrivateUsage);
    }

    public static void TrimWorkingSet() => PInvoke.SetProcessWorkingSetSize(PInvoke.GetCurrentProcess(), unchecked((nuint)(nint)(-1)), unchecked((nuint)(nint)(-1)));

    private static HWND Focus(HWND fg)
    {
        if (fg.IsNull) return default;
        uint tid = PInvoke.GetWindowThreadProcessId(fg, null);
        var gti = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
        return PInvoke.GetGUIThreadInfo(tid, &gti) ? gti.hwndFocus : default;
    }

    private static string Title(HWND h) => WindowText(h);

    private static string WindowText(HWND h)
    {
        if (h.IsNull) return "";
        char* buf = stackalloc char[512];
        nint n = PInvoke.SendMessage(h, PInvoke.WM_GETTEXT, (WPARAM)512, (LPARAM)(nint)buf).Value;
        return new string(buf, 0, (int)Math.Clamp(n, 0, 511));
    }

    private static void SendChord(params VIRTUAL_KEY[] keys)
    {
        var inputs = stackalloc INPUT[keys.Length * 2];
        for (int i = 0; i < keys.Length; i++) inputs[i] = Key(keys[i], false);
        for (int i = 0; i < keys.Length; i++) inputs[keys.Length + i] = Key(keys[keys.Length - 1 - i], true);
        PInvoke.SendInput((uint)(keys.Length * 2), inputs, sizeof(INPUT));
    }

    private static void SendKey(VIRTUAL_KEY k) => SendChord(k);

    private static void SendHotkey(Spoke.Core.Hotkey hk)
    {
        var keys = new List<VIRTUAL_KEY>(5);
        if (hk.Modifiers.HasFlag(Spoke.Core.HotkeyModifiers.Ctrl)) keys.Add(VIRTUAL_KEY.VK_CONTROL);
        if (hk.Modifiers.HasFlag(Spoke.Core.HotkeyModifiers.Alt)) keys.Add(VIRTUAL_KEY.VK_MENU);
        if (hk.Modifiers.HasFlag(Spoke.Core.HotkeyModifiers.Shift)) keys.Add(VIRTUAL_KEY.VK_SHIFT);
        if (hk.Modifiers.HasFlag(Spoke.Core.HotkeyModifiers.Win)) keys.Add(VIRTUAL_KEY.VK_LWIN);
        keys.Add((VIRTUAL_KEY)hk.VirtualKey);
        SendChord([.. keys]);
    }

    private static void SendUnicode(char c)
    {
        var inputs = stackalloc INPUT[2];
        for (int i = 0; i < 2; i++)
        {
            inputs[i].type = INPUT_TYPE.INPUT_KEYBOARD;
            inputs[i].Anonymous.ki.wScan = c;
            inputs[i].Anonymous.ki.dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE | (i == 1 ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0);
        }
        PInvoke.SendInput(2, inputs, sizeof(INPUT));
        Thread.Sleep(5);
    }

    private static INPUT Key(VIRTUAL_KEY vk, bool up)
    {
        var i = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        i.Anonymous.ki.wVk = vk;
        i.Anonymous.ki.dwFlags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0;
        return i;
    }
}

/// <summary>`Spoke.exe --focus-target`: a plain window with a focused text box, used by the bench.</summary>
internal static unsafe class FocusTarget
{
    public const string ClassName = "SpokeFocusTarget";

    public static void Run()
    {
        var hinst = (HINSTANCE)(nint)PInvoke.GetModuleHandle((PCWSTR)null).Value;
        fixed (char* cls = ClassName)
        fixed (char* title = "Spoke focus target")
        fixed (char* edit = "Edit")
        {
            var wc = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &Proc, hInstance = hinst, lpszClassName = cls, hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW) };
            PInvoke.RegisterClassEx(&wc);
            HWND w = PInvoke.CreateWindowEx(0, cls, title, WINDOW_STYLE.WS_OVERLAPPEDWINDOW | WINDOW_STYLE.WS_VISIBLE, 200, 200, 640, 360, HWND.Null, HMENU.Null, hinst, null);
            HWND e = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_CLIENTEDGE, edit, (PCWSTR)null,
                WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_VISIBLE | (WINDOW_STYLE)PInvoke.ES_MULTILINE, 10, 10, 600, 290, w, HMENU.Null, hinst, null);

            // Take the foreground. Windows only lets the process that sent the last input do that, so tap a key
            // first. Alt is held with an F24 tap in between so releasing Alt doesn't open any window's menu.
            var k = stackalloc INPUT[4];
            VIRTUAL_KEY[] seq = [VIRTUAL_KEY.VK_MENU, VIRTUAL_KEY.VK_F24, VIRTUAL_KEY.VK_F24, VIRTUAL_KEY.VK_MENU];
            for (int i = 0; i < 4; i++)
            {
                k[i].type = INPUT_TYPE.INPUT_KEYBOARD;
                k[i].Anonymous.ki.wVk = seq[i];
                k[i].Anonymous.ki.dwFlags = i is 2 or 3 ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0;
            }
            PInvoke.SendInput(3, k, sizeof(INPUT));
            PInvoke.SetForegroundWindow(w);
            PInvoke.SendInput(1, k + 3, sizeof(INPUT));
            PInvoke.SetFocus(e);
        }
        App.RunMessageLoop();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT Proc(HWND h, uint msg, WPARAM w, LPARAM l)
    {
        if (msg == PInvoke.WM_DESTROY) { PInvoke.PostQuitMessage(0); return default; }
        return PInvoke.DefWindowProc(h, msg, w, l);
    }
}
