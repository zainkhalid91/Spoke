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
        var settings = new Settings();
        settings.Load(out _);
        var cfg = settings.Current;
        var look = cfg.Appearance;

        var gfx = new Gfx(forceWarp: false);
        HWND hwnd;
        fixed (char* cls = "STATIC")
        fixed (char* title = "Spoke render")
            hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, cls, title,
                WINDOW_STYLE.WS_POPUP, 0, 0, 1, 1, HWND.Null, HMENU.Null, HINSTANCE.Null, null);

        var r = new WheelRenderer(gfx, hwnd, SliceDef.From(cfg.Slices)) { WheelName = cfg.WheelName };
        var theme = Theme.ByName(Arg("--theme") ?? look.ThemeFor(SystemLook.AppsUseLightTheme));
        if (look.Accent == AccentMode.Custom && Arg("--theme") is null) theme = theme.WithAccent(look.AccentRgb);
        r.SetTheme(theme);
        r.SetRingMode(!args.Contains("--wheel"));
        r.Scale = float.TryParse(Arg("--scale"), System.Globalization.CultureInfo.InvariantCulture, out float sc) ? sc : 1.25f;
        r.SetDpi(96);

        var view = new WheelView { Hover = Int("--hover", -1) };
        int ring = Int("--ring", -1);
        if (ring >= 0 && ring < cfg.Slices.Count)
        {
            var items = TextTools.All.Where(o => o.Op != TextTools.Op.Sentence)
                .Select(o => SliceDef.Item(o.Label, o.Detail, o.Glyph, () => { })).ToArray();
            r.OpenRing(ring, items, "Text tools");
            view.RingHover = Int("--ring-hover", -1);
        }

        var pixels = r.Snapshot(view, theme.Light ? 0xC9CED6u : 0x2B3440u);
        var png = new Imaging().EncodePng(pixels);
        File.WriteAllBytes(output, png);
        Console.WriteLine($"{output}: {pixels.Width}×{pixels.Height}, {theme.Name}{(ring >= 0 ? $", band on slice {ring + 1}" : "")}");
        PInvoke.DestroyWindow(hwnd);
        return 0;
    }
}
