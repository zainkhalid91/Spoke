using System.Diagnostics;
using Spoke.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Power;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Spoke.Daemon;

/// <summary>The built-in utility wheels: Files (convert, resize, PDF, Office), Text tools, Generate and System.</summary>
internal sealed unsafe partial class App
{
    // ───────────────────────── sub-wheel navigation ─────────────────────────

    private readonly List<(string Name, SliceDef[] Slices)> _subStack = [];

    /// <summary>subMenus "ring": options open as an outer band around their slice instead of a new wheel.</summary>
    private bool UseRing = true;
    /// <summary>The bands opened so far (Files ▸ Convert ▸ …): the last one is showing. All share one parent slice.</summary>
    private readonly List<SubWheel> _bandStack = [];
    private int _bandParent = -1;

    /// <summary>The configured wheel (what icons and settings apply to), even while a sub-wheel is showing.</summary>
    private SliceDef[] RootSlices => _subStack.Count > 0 ? _subStack[0].Slices : _slices;

    /// <summary>Shows a tool's options: as an outer band around <paramref name="parent"/>, or as a new wheel.</summary>
    private void EnterSub(SubWheel w, int parent = -1)
    {
        if (UseRing && (parent >= 0 || _bandStack.Count > 0) && w.Items.Length is >= 1 and <= RingLayout.MaxItems)
        {
            if (_bandStack.Count == 0) _bandParent = parent;
            _bandStack.Add(w);
            ShowBand(w);
            return;
        }
        if (w.Items.Length < ConfigLoader.MinSlices) return;
        _subStack.Add((Renderer.WheelName, _slices));
        ShowSlices(w.Items, w.Title);
    }

    private void ShowBand(SubWheel w)
    {
        Renderer.OpenRing(_bandParent, w.Items, _bandStack.Count > 1 ? $"{_bandStack[^2].Title} ▸ {w.Title}" : w.Title);
        _view.Hover = -1;
        _view.RingHover = -1;
        Renderer.Draw(_view);
        Renderer.Commit();
    }

    /// <summary>Folds every band (another slice was chosen, or the wheel closes).</summary>
    private void CloseBand()
    {
        if (_bandStack.Count == 0) return;
        _bandStack.Clear();
        _bandParent = -1;
        Renderer.CloseRing();
        _view.RingHover = -1;
    }

    private void RunRingItem(int item)
    {
        var band = Renderer.Ring;
        if (band is null || item < 0 || item >= band.Items.Length) return;
        RunGenerated(band.Items[item]);
    }

    private void SetHovers(int slice, int item)
    {
        if (slice == _view.Hover && item == _view.RingHover) return;
        _view.Hover = slice;
        _view.RingHover = item;
        Renderer.Draw(_view);
        Renderer.Commit();
    }

    private int RingHitClient(LPARAM lParam)
    {
        if (Renderer.Ring is not { } band) return -1;
        int x = (short)(lParam.Value & 0xFFFF) + _work.left, y = (short)((lParam.Value >> 16) & 0xFFFF) + _work.top;
        float scale = 96f / Renderer.Dpi;
        return band.Layout.HitTest((x - _centerX) * scale, (y - _centerY) * scale);
    }

    /// <summary>Keys while a band is open: arrows walk the band, Enter or 1–9/0 picks, Esc/Backspace goes back.</summary>
    private void OnRingKey(VIRTUAL_KEY vk, WheelRenderer.RingBand band)
    {
        switch (vk)
        {
            case VIRTUAL_KEY.VK_ESCAPE or VIRTUAL_KEY.VK_BACK:
                ExitSub();
                return;
            case VIRTUAL_KEY.VK_RETURN:
                if (_view.RingHover >= 0) RunRingItem(_view.RingHover);
                return;
            case VIRTUAL_KEY.VK_RIGHT or VIRTUAL_KEY.VK_DOWN:
                SetHovers(-1, band.Layout.Step1(_view.RingHover, 1));
                return;
            case VIRTUAL_KEY.VK_LEFT or VIRTUAL_KEY.VK_UP:
                SetHovers(-1, band.Layout.Step1(_view.RingHover, -1));
                return;
            default:
            {
                int n = KeyHook.DigitOf(vk) - 1;
                if (n >= 0 && n < band.Items.Length) RunRingItem(n);
                return;
            }
        }
    }

    /// <summary>Back one level. False when already on the main wheel.</summary>
    private bool ExitSub()
    {
        if (_bandStack.Count > 0)
        {
            _bandStack.RemoveAt(_bandStack.Count - 1);
            if (_bandStack.Count > 0) ShowBand(_bandStack[^1]);
            else
            {
                CloseBand();
                _view.Hover = -1;
                Renderer.Draw(_view);
                Renderer.Commit();
            }
            return true;
        }
        if (_subStack.Count == 0) return false;
        var (name, slices) = _subStack[^1];
        _subStack.RemoveAt(_subStack.Count - 1);
        ShowSlices(slices, name);
        return true;
    }

    private void ShowSlices(SliceDef[] slices, string title)
    {
        Renderer.SetSlices(slices);
        Renderer.WheelName = title;
        _view.Hover = -1;
        Renderer.Draw(_view);
        Renderer.AnimateIn();
        Renderer.Commit();
    }

    /// <summary>On close: back to the main wheel, and re-cache it now so the next open stays one blit.</summary>
    private void ResetSubs()
    {
        CloseBand();
        if (_subStack.Count == 0) return;
        Renderer.SetSlices(_subStack[0].Slices);
        Renderer.WheelName = _subStack[0].Name;
        _subStack.Clear();
        Renderer.Warm();
    }

    /// <summary>A generated item was chosen.</summary>
    private void RunGenerated(SliceDef s)
    {
        if (s.OnOpen != null)
        {
            if (s.OnOpen() is { } w) EnterSub(w);
            return;
        }
        if (s.KeepOpen) { s.OnRun!(); return; }
        Close();
        s.OnRun!();
    }

    private SubWheel? ToolWheel(ToolKind tool) => tool switch
    {
        ToolKind.Files => FilesWheel(),
        ToolKind.TextTools => TextWheel(),
        ToolKind.Generate => GenerateWheel(),
        ToolKind.System => SystemWheel(),
        _ => null,
    };

    // ───────────────────────── Files ─────────────────────────

    private SubWheel? FilesWheel()
    {
        var files = ExplorerSelection.Get(_fgAtOpen, _tray, out string source);
        if (files.Length == 0)
        {
            Notify("No files selected", "Select files in File Explorer (or on the desktop), or copy them with Ctrl+C, then open Files again.", true);
            return null;
        }
        var kind = FileKinds.Common(files);
        string title = files.Length == 1 ? Ellipsize(Path.GetFileName(files[0]), 22) : $"{files.Length} {FileKinds.Noun(kind, files.Length)}";
        Log.Write($"Files: {files.Length} from {source} ({kind})");
        var items = new List<SliceDef>();

        if (kind == FileKind.Image)
        {
            items.Add(SliceDef.Sub("Convert", "PNG, JPEG, WebP, ICO…", '', () => ConvertWheel(files)));
            items.Add(SliceDef.Sub("Resize", "Smaller copies", '', () => ResizeWheel(files)));
            items.Add(SliceDef.Item("Compress", "JPEG at 75%, max 4K", '', () => Job("compress", "", files, "Compressed")));
            items.Add(SliceDef.Item("Remove metadata", "Location, camera, dates", '', () => Job("strip", "", files, "Metadata removed")));
            items.Add(SliceDef.Item(files.Length == 1 ? "Save as PDF" : "Combine into PDF", files.Length == 1 ? "One A4 page" : $"{files.Length} pages, A4",
                '', () => Job("combinePdf", "", files, "PDF saved")));
        }
        else if (kind is FileKind.Pdf)
        {
            if (Office.CanConvert(kind)) items.Add(SliceDef.Item("To Word", "Editable .docx (needs Word)", '', () => Job("office", "docx", files, "Converted to Word")));
        }
        else if (kind is FileKind.WordDoc or FileKind.Text or FileKind.Spreadsheet or FileKind.Presentation)
        {
            if (Office.CanConvert(kind))
            {
                items.Add(SliceDef.Item("To PDF", "Same name, .pdf", '', () => Job("office", "pdf", files, "Saved as PDF")));
                string? modern = kind switch { FileKind.WordDoc => "docx", FileKind.Spreadsheet => "xlsx", FileKind.Presentation => "pptx", _ => null };
                if (modern != null && files.Any(f => !f.EndsWith("." + modern, StringComparison.OrdinalIgnoreCase)))
                    items.Add(SliceDef.Item($"To .{modern}", "Modern Office format", '', () => Job("office", modern, files.Where(f => !f.EndsWith("." + modern, StringComparison.OrdinalIgnoreCase)).ToList(), "Converted")));
            }
        }
        else if (files.All(f => FileKinds.Of(f) is FileKind.WordDoc or FileKind.Text or FileKind.Spreadsheet or FileKind.Presentation) && files.All(f => Office.CanConvert(FileKinds.Of(f))))
        {
            items.Add(SliceDef.Item("All to PDF", "Each file, same name", '', () => Job("office", "pdf", files, "Saved as PDF")));
        }

        items.Add(SliceDef.Item("Copy paths", files.Length == 1 ? "Full path" : $"{files.Length} lines", '', () =>
        {
            if (ClipboardIO.SetText(_tray, string.Join(Environment.NewLine, files))) Notify("Copied", files.Length == 1 ? files[0] : $"{files.Length} paths", false);
        }));
        items.Add(SliceDef.Item("Copy names", "Without folders", '', () =>
        {
            if (ClipboardIO.SetText(_tray, string.Join(Environment.NewLine, files.Select(Path.GetFileName)))) Notify("Copied", $"{files.Length} {(files.Length == 1 ? "name" : "names")}", false);
        }));
        if (items.Count == 2 && kind is FileKind.Pdf or FileKind.WordDoc or FileKind.Spreadsheet or FileKind.Presentation)
            Notify("Office not found", "Converting documents needs Microsoft Office or LibreOffice.", true);
        return new SubWheel(title, [.. items]);
    }

    private SubWheel ConvertWheel(string[] files)
    {
        bool ffmpeg = Worker.FfmpegPath != null;
        string? same = files.Select(f => Imaging.FormatOf(Path.GetExtension(f))).Distinct().Count() == 1 ? Imaging.FormatOf(Path.GetExtension(files[0]))?.ToString() : null;
        var formats = new List<(ImageFormat F, string Detail)>
        {
            (ImageFormat.Png, "Lossless, transparency"), (ImageFormat.Jpeg, "Photos, small"), (ImageFormat.WebP, ffmpeg ? "Web, small" : ""),
            (ImageFormat.Avif, ffmpeg ? "Smallest, modern" : ""), (ImageFormat.Gif, "256 colours"), (ImageFormat.Bmp, "Uncompressed"),
            (ImageFormat.Tiff, "Print, lossless"), (ImageFormat.Ico, "Windows icon, 16–256"),
        };
        var items = formats
            .Where(f => f.Detail.Length > 0 && f.F.ToString() != same)
            .Select(f => SliceDef.Item(f.F == ImageFormat.Jpeg ? "JPEG" : f.F.ToString().ToUpperInvariant(), f.Detail, '',
                () => Job("convert", Imaging.ExtOf(f.F), files, $"Converted to {Imaging.ExtOf(f.F).ToUpperInvariant()}")))
            .ToArray();
        return new SubWheel("Convert to", items);
    }

    private SubWheel ResizeWheel(string[] files)
    {
        (string Spec, string Label, string Detail)[] sizes =
        [
            ("25%", "25%", "Quarter size"), ("50%", "50%", "Half size"), ("75%", "75%", "Three quarters"),
            ("1920w", "1920 wide", "Full HD width"), ("1280w", "1280 wide", "HD width"), ("800w", "800 wide", "Web, email"),
            ("512", "512 px", "Long side, thumbnails"),
        ];
        return new SubWheel("Resize", [.. sizes.Select(s => SliceDef.Item(s.Label, s.Detail, '', () => Job("resize", s.Spec, files, $"Resized to {s.Label}")))]);
    }

    private void Job(string op, string arg, IReadOnlyList<string> files, string what) =>
        FileJobs.Start(new FileJob { Op = op, Arg = arg, Inputs = [.. files] }, what, Notify);

    private static string Ellipsize(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ───────────────────────── Text tools ─────────────────────────

    private SubWheel TextWheel()
    {
        bool terminal = IsTerminal(_fgAtOpen);
        var items = TextTools.All
            .Where(t => t.Op != TextTools.Op.Sentence)   // 10 fit comfortably; sentence case is rarely used
            .Select(t => SliceDef.Item(t.Label, t.Detail, t.Glyph, () =>
                _actions.Transform(t.Op, t.Label, KeyHook.IsDown(VIRTUAL_KEY.VK_SHIFT), terminal)))
            .ToArray();
        return new SubWheel(terminal ? "Text (clipboard)" : "Text tools", items);
    }

    /// <summary>Consoles and terminals: Ctrl+C there stops the running program, so text tools use the clipboard only.</summary>
    private static bool IsTerminal(HWND h)
    {
        char* buf = stackalloc char[64];
        int n = PInvoke.GetClassName(h, buf, 64);
        string cls = new(buf, 0, n);
        return cls is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "mintty" or "PuTTY" or "VirtualConsoleClass";
    }

    // ───────────────────────── Generate ─────────────────────────

    private SubWheel GenerateWheel() => new("Generate",
    [
        SliceDef.Item("UUID", "Random, lowercase", '', () => _actions.Type(Guid.NewGuid().ToString())),
        SliceDef.Item("Password", "20 characters, strong", '', () => _actions.Type(TextTools.Password(20))),
        SliceDef.Item("Date", "2026-11-27 style", '', () => _actions.Type(DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))),
        SliceDef.Item("Date + time", "2026-11-27 14:05", '', () => _actions.Type(DateTime.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture))),
        SliceDef.Item("ISO timestamp", "With time zone", '', () => _actions.Type(DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture))),
        SliceDef.Item("Unix time", "Seconds since 1970", '', () => _actions.Type(DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))),
        SliceDef.Item("Lorem ipsum", "Placeholder paragraph", '', () => _actions.Type(TextTools.Lorem)),
        SliceDef.Item("Random number", "1 to 100", '', () => _actions.Type(System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, 101).ToString(System.Globalization.CultureInfo.InvariantCulture))),
    ]);

    // ───────────────────────── System ─────────────────────────

    private bool _keepAwake;

    private SubWheel SystemWheel()
    {
        bool light = SystemLook.AppsUseLightTheme;
        return new("System",
        [
            SliceDef.Item("Volume up", "Stays open: press again", '', () => { KeyHook.Tap(VIRTUAL_KEY.VK_VOLUME_UP); KeyHook.Tap(VIRTUAL_KEY.VK_VOLUME_UP); }, keepOpen: true),
            SliceDef.Item("Volume down", "Stays open: press again", '', () => { KeyHook.Tap(VIRTUAL_KEY.VK_VOLUME_DOWN); KeyHook.Tap(VIRTUAL_KEY.VK_VOLUME_DOWN); }, keepOpen: true),
            SliceDef.Item("Mute", "Toggle sound", '', () => KeyHook.Tap(VIRTUAL_KEY.VK_VOLUME_MUTE)),
            SliceDef.Item("Play / pause", "Media", '', () => KeyHook.Tap(VIRTUAL_KEY.VK_MEDIA_PLAY_PAUSE)),
            SliceDef.Item("Next track", "Media", '', () => KeyHook.Tap(VIRTUAL_KEY.VK_MEDIA_NEXT_TRACK)),
            SliceDef.Item(light ? "Dark mode" : "Light mode", "Windows apps + taskbar", light ? '' : '', () =>
            {
                if (!SystemLook.SetLightMode(!light)) Notify("Couldn't switch", "Windows didn't allow the change.", true);
            }),
            SliceDef.Item(_keepAwake ? "Keep awake: on" : "Keep awake", _keepAwake ? "Choose to let the PC sleep" : "No sleep, screen stays on", '', ToggleKeepAwake),
            SliceDef.Item("Screen off", "Monitors sleep, PC keeps running", '', () =>
            {
                // A moment's delay so releasing the mouse/keys doesn't wake the screen straight back up.
                var t = new Thread(() => { Thread.Sleep(700); PInvoke.PostMessage(_tray, PInvoke.WM_SYSCOMMAND, PInvoke.SC_MONITORPOWER, 2); }) { IsBackground = true };
                t.Start();
            }),
            SliceDef.Item("Lock", "Lock this PC", '', () => PInvoke.LockWorkStation()),
            SliceDef.Item("Empty Recycle Bin", "Windows asks first", '', () => PInvoke.SHEmptyRecycleBin(_tray, (string?)null, 0)),
        ]);
    }

    private void ToggleKeepAwake()
    {
        _keepAwake = !_keepAwake;
        // The UI thread lives as long as Spoke, so its execution state holds until turned off (or Spoke quits).
        var state = _keepAwake
            ? EXECUTION_STATE.ES_CONTINUOUS | EXECUTION_STATE.ES_SYSTEM_REQUIRED | EXECUTION_STATE.ES_DISPLAY_REQUIRED
            : EXECUTION_STATE.ES_CONTINUOUS;
        PInvoke.SetThreadExecutionState(state);
        Notify(_keepAwake ? "Keep awake is on" : "Keep awake is off",
            _keepAwake ? "The PC won't sleep and the screen stays on until you turn this off (System ▸ Keep awake)." : "Windows can sleep again as usual.", false);
        UpdateTrayTip();
    }
}
