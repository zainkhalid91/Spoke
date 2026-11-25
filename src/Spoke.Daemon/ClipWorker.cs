using System.Collections.Concurrent;
using Spoke.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Threading;

namespace Spoke.Daemon;

/// <summary>
/// Turns copied images into history entries on a low-priority background thread: decode, hash, thumbnail,
/// encrypt, store. The UI thread only copies the bytes off the clipboard, so a big image can never delay
/// the wheel. Posts <see cref="App.WM_APP_CLIPS"/> to the tray window when the history changed.
/// </summary>
internal sealed class ClipWorker
{
    public const int ThumbSide = 320;
    private const long MaxPixels = 80_000_000;   // ignore absurdly large images

    private readonly BlockingCollection<ClipboardImage> _queue = new(boundedCapacity: 4);
    private readonly ClipStore _store;
    private readonly HWND _notify;

    public ClipWorker(ClipStore store, HWND notify)
    {
        _store = store;
        _notify = notify;
    }

    private Thread? _thread;

    /// <summary>
    /// Drops the item if the worker is still busy with several others (rapid repeated copies). The thread
    /// (and its WIC factory) starts with the first copied image, not at launch.
    /// </summary>
    public void Enqueue(ClipboardImage img)
    {
        if (_thread == null)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Spoke clips" };
            _thread.Start();
        }
        _queue.TryAdd(img);
    }

    private unsafe void Run()
    {
        PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
        PInvoke.SetThreadPriority(PInvoke.GetCurrentThread(), THREAD_PRIORITY.THREAD_PRIORITY_BELOW_NORMAL);
        var imaging = new Imaging();
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                Process(imaging, item);
            }
            catch (Exception ex)
            {
                Log.Write($"Clip skipped: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void Process(Imaging imaging, ClipboardImage item)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        byte[] container = item.IsPng ? item.Bytes : Imaging.DibToBmpFile(item.Bytes);
        var px = imaging.Decode(container);
        if ((long)px.Width * px.Height > MaxPixels) return;

        // Clipboard DIBs from screenshots often carry alpha = 0; treat an all-transparent image as opaque.
        bool opaque = !item.IsPng && AllTransparent(px.Data);
        if (opaque) for (int i = 3; i < px.Data.Length; i += 4) px.Data[i] = 255;

        ulong hash = ClipStore.HashPixels(px.Data, px.Width, px.Height);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!_store.TryBump(hash, now))
        {
            byte[] png = item.IsPng ? item.Bytes : imaging.EncodePng(px);
            byte[] thumb = imaging.Thumbnail(px, ThumbSide);
            _store.Add(png, thumb, px.Width, px.Height, hash, now);
            Log.Write($"Clip stored {px.Width}x{px.Height} in {Log.Ms(t0, System.Diagnostics.Stopwatch.GetTimestamp()):F0} ms ({_store.Count} in history)");
        }
        PInvoke.PostMessage(_notify, App.WM_APP_CLIPS, default, default);
    }

    private static bool AllTransparent(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return false;
        return true;
    }
}
