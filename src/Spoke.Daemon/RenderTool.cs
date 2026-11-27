using Spoke.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Spoke.Daemon;

/// <summary>
/// <c>Spoke.exe --render out.png [--theme Onyx] [--hover 2] [--ring 3] [--ring-hover 1] [--wheel] [--scale 1.5]</c>:
/// draws the wheel from the current settings.json into a PNG, offscreen. For checking looks and layout
/// without opening anything on screen. <c>--ring N</c> opens a sample band (the text tools) on slice N.
/// </summary>
internal static unsafe class RenderTool
{
    public static int Run(string[] args)
    {
        string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        int Int(string name, int def) => int.TryParse(Arg(name), out int v) ? v : def;

        string output = Arg("--render") ?? "wheel.png";
        ConfigResult cfg;
        if (Arg("--config") is { } file) cfg = ConfigLoader.Parse(File.ReadAllText(file));   // a test file instead of settings.json
        else { var settings = new Settings(); settings.Load(out _); cfg = settings.Current; }
        if (!cfg.Ok) { Console.Error.WriteLine(cfg.Errors[0]); return 1; }
        int show = Math.Clamp(Int("--show", 1) - 1, 0, cfg.Wheels.Count - 1);
        var shown = cfg.Wheels[show];
        var look = cfg.Appearance;

        var gfx = new Gfx(forceWarp: false);
        HWND hwnd;
        fixed (char* cls = "STATIC")
        fixed (char* title = "Spoke render")
            hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, cls, title,
                WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, HWND.Null, HMENU.Null, HINSTANCE.Null, null);

        var r = new WheelRenderer(gfx, hwnd, SliceDef.From(shown.Slices)) { WheelName = shown.Name };
        r.SetRow([.. cfg.Wheels.Select(w => w.Name)], show);
        var theme = Theme.ByName(Arg("--theme") ?? look.ThemeFor(SystemLook.AppsUseLightTheme));
        if (look.Accent != AccentMode.Theme) theme = theme.WithAccent(look.Accent == AccentMode.Custom ? look.AccentRgb : SystemLook.AccentRgb);
        r.SetTheme(theme);
        r.SetRingMode(!args.Contains("--wheel"));
        r.Scale = float.TryParse(Arg("--scale"), System.Globalization.CultureInfo.InvariantCulture, out float sc) ? sc : 1.25f;
        r.SetDpi(96);

        var view = new WheelView { Hover = Int("--hover", -1), RowHover = Int("--row-hover", 0) - 1 };
        if (Arg("--drop") is { } dropped)
        {
            // Drop mode's format wheel for this image, with real size estimates (computed here, in-process).
            string[] files = [dropped];
            var wheel = DropMenus.Images(files, (_, _, _, _) => { }, _ => new SubWheel("Resize", []));
            var img = new Imaging();
            var px = img.LoadUpright(dropped);
            var est = Worker.EstimateOne(img, px, wheel.Items.Where(i => i.Key != null).Select(i => i.Key!)).ToDictionary(e => e.Fmt, e => e.Bytes);
            foreach (var it in wheel.Items)
                if (it.Key != null) it.Badge = est[it.Key] < 0 ? DropMenus.Missing(it.Key) : SizeEstimate.Label(est[it.Key]);
            r.SetSlices(wheel.Items);
            r.WheelName = "Drop to convert";
            long bytes = new FileInfo(dropped).Length;
            r.HubFrom = OutputNames.Size(bytes);
            r.HubHint = $"{Path.GetFileName(dropped)} · {OutputNames.Size(bytes)}";
            view.Drop = true;
        }
        int ring = Int("--ring", -1);
        if (ring >= 0 && ring < shown.Slices.Count)
        {
            var slices = SliceDef.From(shown.Slices);
            var items = slices[ring].Children.Length > 0 ? slices[ring].Children : TextTools.All.Where(o => o.Op != TextTools.Op.Sentence)
                .Select(o => SliceDef.Item(o.Label, o.Detail, o.Glyph, () => { })).ToArray();
            r.OpenRing(ring, items, slices[ring].Children.Length > 0 ? slices[ring].Label : "Text tools");
            view.RingHover = Int("--ring-hover", -1);
        }

        if (Int("--switch-bench", 0) is int runs and > 0 && cfg.Wheels.Count > 1)
        {
            // Swap wheels like Tab does (new slices, row, a full frame) and time it.
            var sets = cfg.Wheels.Select(w => SliceDef.From(w.Slices)).ToArray();
            var names = cfg.Wheels.Select(w => w.Name).ToArray();
            var times = new List<double>();
            for (int i = 0; i < runs; i++)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                int w = (i + 1) % sets.Length;
                r.SetSlices(sets[w]);
                r.WheelName = names[w];
                r.SetRow(names, w);
                r.Draw(new WheelView());
                r.Commit();
                times.Add(Log.Ms(t0, System.Diagnostics.Stopwatch.GetTimestamp()));
            }
            times.Sort();
            Console.WriteLine($"switch x{runs}: p50 {times[runs / 2]:0.00} ms, p95 {times[(int)(runs * 0.95)]:0.00} ms, max {times[^1]:0.00} ms");
        }

        var pixels = r.Snapshot(view, theme.Light ? 0xC9CED6u : 0x2B3440u);
        var png = new Imaging().EncodePng(pixels);
        File.WriteAllBytes(output, png);
        Console.WriteLine($"{output}: {pixels.Width}×{pixels.Height}, {theme.Name}{(ring >= 0 ? $", band on slice {ring + 1}" : "")}");
        PInvoke.DestroyWindow(hwnd);
        return 0;
    }
}
