using System.Diagnostics;
using Spoke.Core;
using Windows.Win32;

namespace Spoke.Daemon;

/// <summary>
/// Drop mode: drag files from Explorer, press the wheel shortcut while still holding the mouse button, and the wheel
/// opens under the pointer as a <b>format wheel</b>. Each slice shows the estimated output size (a worker samples
/// the image in the background), and dropping on a slice converts the files beside the originals.
/// </summary>
internal sealed unsafe partial class App : IDropHandler
{
    private const nuint DropTimerId = 5;
    public const uint WM_APP_ESTIMATE = PInvoke.WM_APP + 10;   // a size estimate finished (from its thread)

    /// <summary>The wheel was opened for a drag that is still going (cleared by a drop or a close).</summary>
    private bool _dragging;
    private string[] _dropFiles = [];
    private int _dropGen;
    private int _dwellSlice = -1;
    private long _dwellSince;
    private int _buttonUpTicks;
    private (int W, int H) _dropSize;
    private Process? _estimator;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(int Gen, FileJobResult? Result)> _estimates = new();

    /// <summary>Hovering a slice that opens more choices (Resize) this long while dragging opens them.</summary>
    private const int DwellMs = 650;

    /// <summary>The shortcut was pressed: with the mouse button held, a drag is under way, so open in drop mode.</summary>
    private bool TryOpenDrop()
    {
        if (_paused || !DropTarget.PrimaryDown()) return false;
        // Usually the dragged files are the Explorer selection, so the estimates can start before the pointer moves;
        // the drag's own file list replaces it as soon as it reaches the wheel.
        var files = ExplorerSelection.Get(PInvoke.GetForegroundWindow(), _tray, out string source, clipboard: false);
        Log.Write($"Drop mode: {files.Length} file(s) from {(files.Length > 0 ? source : "nowhere yet")}");
        _dragging = true;
        _buttonUpTicks = 0;
        _view.Drop = true;
        _subStack.Add((Renderer.WheelName, _slices));
        ShowDropWheel(files, animate: false);
        Open(Stopwatch.GetTimestamp());
        PInvoke.SetTimer(_tray, DropTimerId, 100, null);
        return true;
    }

    /// <summary>Shows the wheel for these files (and starts estimating). The caller has pushed the main wheel.</summary>
    private void ShowDropWheel(string[] files, bool animate)
    {
        _dropFiles = files;
        _dropSize = default;
        _dwellSlice = -1;
        var kind = files.Length > 0 ? FileKinds.Common(files) : FileKind.Image;
        var wheel = kind == FileKind.Image ? DropMenus.Images(files, Job, ResizeWheel) : FilesWheelFor(files);
        if (wheel.Items.Length < ConfigLoader.MinSlices) wheel = new SubWheel(wheel.Title, [.. wheel.Items, SliceDef.Item("Cancel", "Drop here to cancel", '', () => { })]);
        Renderer.SetSlices(wheel.Items);
        Renderer.WheelName = files.Length == 0 ? "Drop files" : "Drop to convert";
        long bytes = files.Sum(f => File.Exists(f) ? new FileInfo(f).Length : 0);
        Renderer.HubFrom = bytes > 0 ? OutputNames.Size(bytes) : null;
        Renderer.HubHint = files.Length switch
        {
            0 => "onto a slice · Esc",
            1 => $"{Ellipsize(Path.GetFileName(files[0]), 16)} · {OutputNames.Size(bytes)}",
            _ => $"{files.Length} {FileKinds.Noun(kind, files.Length)} · {OutputNames.Size(bytes)}",
        };
        _view.Hover = -1;
        if (IsOpen)
        {
            Renderer.Draw(_view);
            if (animate) Renderer.AnimateIn();
            Renderer.Commit();
        }
        StartEstimate(wheel.Items, files);
    }

    // ───────────────────────── estimates ─────────────────────────

    private void StartEstimate(SliceDef[] items, string[] files)
    {
        int gen = ++_dropGen;
        StopEstimate();
        string formats = string.Join(',', items.Where(i => i.Key != null).Select(i => i.Key));
        if (files.Length == 0 || formats.Length == 0) return;
        var job = new FileJob { Op = "estimate", Arg = formats, Inputs = [.. files] };
        var t = new Thread(() =>
        {
            var r = FileJobs.RunForResult(job, 60_000, p => _estimator = p);
            _estimates.Enqueue((gen, r));
            PInvoke.PostMessage(_tray, WM_APP_ESTIMATE, default, default);
        }) { IsBackground = true, Name = "Spoke estimate" };
        t.Start();
    }

    private void StopEstimate()
    {
        var p = _estimator;
        _estimator = null;
        try { if (p is { HasExited: false }) p.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>UI thread: a worker finished estimating. Fills in the badges if that wheel is still showing.</summary>
    private void OnEstimate()
    {
        while (_estimates.TryDequeue(out var e))
        {
            if (e.Gen != _dropGen || !IsOpen || !_view.Drop || e.Result is not { } r) continue;
            Log.Write($"Estimates ({r.Width}×{r.Height}): " + string.Join(", ", r.Estimates.Select(kv => $"{kv.Key} {(kv.Value < 0 ? "n/a" : OutputNames.Size(kv.Value))}")));
            _dropSize = (r.Width, r.Height);
            var slices = _subStack.Count > 1 ? _subStack[1].Slices : _slices;   // the format wheel, even if Resize is open on top
            foreach (var s in slices)
            {
                if (s.Key == null) continue;
                long b = r.Estimates.GetValueOrDefault(s.Key, -1);
                s.Unavailable = b < 0;
                s.Badge = b < 0 ? DropMenus.Missing(s.Key) : SizeEstimate.Label(b);
            }
            Renderer.SetSlices(_slices);   // re-caches the resting wheel with the new badges
            Renderer.Draw(_view);
            Renderer.Commit();
        }
    }

    // ───────────────────────── the drag (IDropHandler, UI thread) ─────────────────────────

    uint IDropHandler.DragEnter(string[] files, int x, int y)
    {
        if (!IsOpen || !_view.Drop) return DropTarget.DropEffectNone;
        if (files.Length > 0 && !files.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(_dropFiles.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
        {
            // Not the Explorer selection after all (or there was none): rebuild for what is really being dragged.
            Log.Write($"Drop mode: the drag holds {files.Length} file(s)");
            CloseBand();
            while (_subStack.Count > 1) _subStack.RemoveAt(_subStack.Count - 1);
            ShowDropWheel(files, animate: true);
        }
        else if (files.Length == 0)
        {
            Renderer.HubHint = "only files can be dropped";
            Renderer.Draw(_view);
            Renderer.Commit();
            return DropTarget.DropEffectNone;
        }
        return ((IDropHandler)this).DragOver(x, y);
    }

    uint IDropHandler.DragOver(int x, int y)
    {
        if (!IsOpen || !_view.Drop) return DropTarget.DropEffectNone;
        var (slice, item) = HitBoth(x, y);
        if (slice != _dwellSlice) { _dwellSlice = slice; _dwellSince = Stopwatch.GetTimestamp(); }
        SetHovers(slice, item);
        return Takes(slice, item) ? DropTarget.DropEffectCopy : DropTarget.DropEffectNone;
    }

    void IDropHandler.DragLeave()
    {
        // The drag left the overlay (onto the taskbar or another screen) or was cancelled: nothing to drop.
        if (_dragging) Close();
    }

    uint IDropHandler.Drop(string[] files, int x, int y)
    {
        if (!IsOpen || !_view.Drop) return DropTarget.DropEffectNone;
        _dragging = false;
        PInvoke.KillTimer(_tray, DropTimerId);
        var (slice, item) = HitBoth(x, y);
        if (item < 0 && slice >= 0 && slice < _slices.Length && _slices[slice].Unavailable)
        {
            var u = _slices[slice];
            Close();
            Notify("Can't convert to " + u.Label, u.Key == "heic" ? "Windows needs the HEIF and HEVC Video Extensions (Microsoft Store) to write HEIC." : "This PC has no encoder for it.", true);
            return DropTarget.DropEffectNone;
        }
        if (!Takes(slice, item)) { Close(); return DropTarget.DropEffectNone; }
        if (item >= 0) { RunRingItem(item); return DropTarget.DropEffectCopy; }
        var s = _slices[slice];
        if (s.OnOpen != null)
        {
            // Dropped on Resize: its sizes open, and the wheel stays up for a click (the drag is over now).
            if (s.OnOpen() is { } w) EnterSub(w, slice);
            return DropTarget.DropEffectCopy;
        }
        RunGenerated(s);
        return DropTarget.DropEffectCopy;
    }

    /// <summary>Whether a drop at this slice / band item does something.</summary>
    private bool Takes(int slice, int item)
    {
        if (item >= 0) return Renderer.Ring is { } band && item < band.Items.Length;
        return slice >= 0 && slice < _slices.Length && !_slices[slice].Unavailable && (_slices[slice].OnRun != null || _slices[slice].OnOpen != null);
    }

    private (int Slice, int Item) HitBoth(int x, int y)
    {
        float scale = 96f / Renderer.Dpi;
        float dx = (x - _centerX) * scale, dy = (y - _centerY) * scale;
        if (Renderer.Ring is { } band && band.Layout.HitTest(dx, dy) is >= 0 and var it) return (-1, it);
        return (_layout.HitTest(dx, dy), -1);
    }

    /// <summary>
    /// Every 100 ms while dragging: opens Resize after a short hover, and closes the wheel if the button was let go
    /// without a drop reaching us (the drag ended somewhere else, or the shortcut was pressed without a drag).
    /// </summary>
    private void OnDropTimer()
    {
        if (!IsOpen || !_dragging) { PInvoke.KillTimer(_tray, DropTimerId); return; }
        if (!DropTarget.PrimaryDown())
        {
            if (++_buttonUpTicks >= 3) Close();
            return;
        }
        _buttonUpTicks = 0;
        if (_dwellSlice >= 0 && _dwellSlice < _slices.Length && Renderer.Ring == null && _slices[_dwellSlice].OnOpen is { } open
            && Log.Ms(_dwellSince, Stopwatch.GetTimestamp()) >= DwellMs)
        {
            int s = _dwellSlice;
            _dwellSlice = -1;
            if (open() is { } w) EnterSub(w, s);
        }
    }

    /// <summary>Drop-mode clean-up when the wheel closes.</summary>
    private void EndDrop()
    {
        if (!_view.Drop) return;
        _view.Drop = false;
        _dragging = false;
        _dropFiles = [];
        _dropGen++;
        StopEstimate();
        PInvoke.KillTimer(_tray, DropTimerId);
        Renderer.HubHint = null;
        Renderer.HubFrom = null;
    }
}

/// <summary>The format wheel's items for images.</summary>
internal static class DropMenus
{
    private static bool? _heif;

    /// <summary>Whether Windows has a HEIF encoder (the HEIF extension); encoding also needs HEVC, which the estimate finds out.</summary>
    public static bool HeifEncoder => _heif ??= Imaging.HasEncoder(Windows.Win32.PInvoke.GUID_ContainerFormatHeif);

    /// <summary>The badge for a format this PC can't write.</summary>
    public static string Missing(string key) => key == "heic" ? "needs HEVC" : "not available";

    public static SubWheel Images(string[] files, Action<string, string, IReadOnlyList<string>, string> job, Func<string[], SubWheel> resize)
    {
        bool ffmpeg = Worker.FfmpegPath != null;
        var exts = files.Select(f => Imaging.FormatOf(Path.GetExtension(f))).Distinct().ToArray();
        ImageFormat? same = exts.Length == 1 ? exts[0] : null;
        (ImageFormat F, string Label, string Detail, char Glyph, bool Show)[] formats =
        [
            (ImageFormat.Png, "PNG", "Lossless, transparency", '', true),
            (ImageFormat.Jpeg, "JPEG", "Photos, small", '', true),
            (ImageFormat.WebP, "WebP", "Web, small", '', ffmpeg),
            (ImageFormat.Avif, "AVIF", "Smallest, modern", '', ffmpeg),
            (ImageFormat.Heic, "HEIC", "iPhone-style photos", '', HeifEncoder),
            (ImageFormat.Gif, "GIF", "256 colours", '', true),
            (ImageFormat.Tiff, "TIFF", "Print, lossless", '', true),
            (ImageFormat.Bmp, "BMP", "Uncompressed", '', true),
        ];
        var items = new List<SliceDef>();
        foreach (var f in formats)
        {
            if (!f.Show || f.F == same) continue;
            string ext = Imaging.ExtOf(f.F);
            var item = SliceDef.Item(f.Label, f.Detail, f.Glyph, () => job("convert", ext, files, $"Converted to {f.Label}"));
            item.Key = ext;
            item.Badge = files.Length > 0 ? "…" : null;
            items.Add(item);
        }
        var r = SliceDef.Sub("Resize", "Smaller copies", '', () => resize(files));
        r.Badge = files.Length > 0 ? "50% · 25% …" : null;
        items.Add(r);
        var c = SliceDef.Item("Compress", "JPEG at 75%, max 4K", '', () => job("compress", "", files, "Compressed"));
        c.Key = "compress";
        c.Badge = files.Length > 0 ? "…" : null;
        items.Add(c);
        return new SubWheel("Convert to", [.. items]);
    }
}
