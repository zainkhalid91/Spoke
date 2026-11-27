using Spoke.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Cryptography;
using Windows.Win32.System.Memory;

namespace Spoke.Daemon;

/// <summary>Raw image bytes taken off the clipboard (copied out quickly so the clipboard is released fast).</summary>
internal sealed class ClipboardImage
{
    public required byte[] Bytes;
    public required bool IsPng;   // else a CF_DIB/CF_DIBV5 blob
}

internal static unsafe class ClipboardIO
{
    private const uint CF_DIB = 8, CF_DIBV5 = 17, CF_UNICODETEXT = 13;
    private static uint _png, _exclude, _canInclude, _viewerIgnore;

    private static void EnsureFormats()
    {
        if (_png != 0) return;
        _png = Register("PNG");
        _exclude = Register("ExcludeClipboardContentFromMonitorProcessing");
        _canInclude = Register("CanIncludeInClipboardHistory");
        _viewerIgnore = Register("Clipboard Viewer Ignore");
    }

    private static uint Register(string name)
    {
        fixed (char* n = name) return PInvoke.RegisterClipboardFormat(n);
    }

    /// <summary>Sequence number after Spoke's own last write, so the watcher can skip it.</summary>
    public static uint OwnSequence;

    /// <summary>
    /// Reads an image from the clipboard if there is one and it may be stored. Returns null for no image,
    /// an excluded copy (password managers, apps that opt out of history) or when the clipboard is busy.
    /// </summary>
    public static ClipboardImage? TryReadImage(HWND owner, bool paused, out bool busy)
    {
        EnsureFormats();
        busy = false;
        bool hasPng = PInvoke.IsClipboardFormatAvailable(_png), hasV5 = PInvoke.IsClipboardFormatAvailable(CF_DIBV5), hasDib = PInvoke.IsClipboardFormatAvailable(CF_DIB);
        if (!hasPng && !hasV5 && !hasDib) return null;
        if (!PInvoke.OpenClipboard(owner)) { busy = true; return null; }
        try
        {
            uint? canInclude = null;
            if (PInvoke.IsClipboardFormatAvailable(_canInclude))
            {
                var v = Copy(_canInclude);
                if (v is { Length: >= 4 }) canInclude = BitConverter.ToUInt32(v);
            }
            if (!ClipboardPolicy.MayStore(paused, PInvoke.IsClipboardFormatAvailable(_exclude), canInclude, PInvoke.IsClipboardFormatAvailable(_viewerIgnore)))
                return null;

            if (hasPng && Copy(_png) is { Length: > 8 } png) return new ClipboardImage { Bytes = png, IsPng = true };
            if (Copy(hasV5 ? CF_DIBV5 : CF_DIB) is { Length: > 40 } dib) return new ClipboardImage { Bytes = dib, IsPng = false };
            return null;
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    private static byte[]? Copy(uint format)
    {
        HANDLE h = PInvoke.GetClipboardData(format);
        if (h.IsNull) return null;
        var g = (HGLOBAL)(nint)h.Value;
        nuint size = PInvoke.GlobalSize(g);
        void* p = PInvoke.GlobalLock(g);
        if (p == null) return null;
        try { return new ReadOnlySpan<byte>(p, (int)size).ToArray(); }
        finally { PInvoke.GlobalUnlock(g); }
    }

    /// <summary>Puts an image on the clipboard as PNG (keeps transparency) and CF_DIB (for every other app).</summary>
    public static bool SetImage(HWND owner, byte[] png, Pixels pixels, bool markOwn)
    {
        EnsureFormats();
        if (!OpenClipboard(owner)) return false;
        try
        {
            PInvoke.EmptyClipboard();
            Put(_png, png);
            Put(CF_DIB, Imaging.ToDib(pixels));
            // Keep our own re-copy out of Windows' clipboard history (Win+V); the image is already in Spoke's.
            if (markOwn) Put(_canInclude, BitConverter.GetBytes(0u));
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
        if (markOwn) OwnSequence = PInvoke.GetClipboardSequenceNumber();
        return true;
    }

    /// <summary>Puts text on the clipboard. <paramref name="markOwn"/>: a transient Spoke write, kept out of Win+V history.</summary>
    public static bool SetText(HWND owner, string text, bool markOwn = false)
    {
        EnsureFormats();
        if (!OpenClipboard(owner)) return false;
        try
        {
            PInvoke.EmptyClipboard();
            var bytes = new byte[(text.Length + 1) * 2];
            System.Text.Encoding.Unicode.GetBytes(text, bytes);
            Put(CF_UNICODETEXT, bytes);
            if (markOwn) Put(_canInclude, BitConverter.GetBytes(0u));
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
        if (markOwn) OwnSequence = PInvoke.GetClipboardSequenceNumber();
        return true;
    }

    /// <summary>The clipboard's text, or null if it holds none (for the {clipboard} token).</summary>
    public static string? GetText(HWND owner)
    {
        if (!PInvoke.IsClipboardFormatAvailable(CF_UNICODETEXT) || !OpenClipboard(owner)) return null;
        try
        {
            var bytes = Copy(CF_UNICODETEXT);
            if (bytes == null) return null;
            string s = System.Text.Encoding.Unicode.GetString(bytes);
            int nul = s.IndexOf('\0');
            return nul >= 0 ? s[..nul] : s;
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>Paths of files copied in Explorer (CF_HDROP), or none.</summary>
    public static string[] GetFiles(HWND owner)
    {
        const uint CF_HDROP = 15;
        if (!PInvoke.IsClipboardFormatAvailable(CF_HDROP) || !OpenClipboard(owner)) return [];
        try
        {
            var h = PInvoke.GetClipboardData(CF_HDROP);
            if (h.IsNull) return [];
            var drop = (Windows.Win32.UI.Shell.HDROP)(nint)h.Value;
            uint n = PInvoke.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
            var files = new List<string>((int)Math.Min(n, 500));
            char* buf = stackalloc char[1024];
            for (uint i = 0; i < n && i < 500; i++)
            {
                uint len = PInvoke.DragQueryFile(drop, i, buf, 1024);
                if (len > 0) files.Add(new string(buf, 0, (int)len));
            }
            return [.. files];
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    // Formats whose data is a GDI/metafile handle, not memory; they can't be copied byte-for-byte (Windows
    // re-synthesises CF_BITMAP and friends from CF_DIB anyway).
    private static readonly HashSet<uint> HandleFormats = [2 /* BITMAP */, 3 /* METAFILEPICT */, 9 /* PALETTE */, 14 /* ENHMETAFILE */, 0x80, 0x82, 0x83, 0x8E];

    /// <summary>
    /// Copies every memory-backed format off the clipboard (up to <paramref name="maxBytes"/> in total) so a
    /// temporary paste can put the user's clipboard back afterwards. Null if the clipboard couldn't be read.
    /// </summary>
    public static List<(uint Format, byte[] Data)>? Snapshot(HWND owner, long maxBytes = 64L << 20)
    {
        if (!OpenClipboard(owner)) return null;
        var list = new List<(uint, byte[])>();
        try
        {
            long total = 0;
            for (uint f = PInvoke.EnumClipboardFormats(0); f != 0; f = PInvoke.EnumClipboardFormats(f))
            {
                if (HandleFormats.Contains(f)) continue;
                var data = Copy(f);
                if (data == null) continue;
                total += data.Length;
                if (total > maxBytes) return null;   // too big to hold on to: better not to touch it at all
                list.Add((f, data));
            }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
        return list;
    }

    public static void Restore(HWND owner, List<(uint Format, byte[] Data)> snapshot)
    {
        if (!OpenClipboard(owner)) return;
        try
        {
            PInvoke.EmptyClipboard();
            foreach (var (f, d) in snapshot) Put(f, d);
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
        OwnSequence = PInvoke.GetClipboardSequenceNumber();   // not a new copy: the image watcher ignores it
    }

    /// <summary>Another app can hold the clipboard for a moment; retry briefly.</summary>
    private static bool OpenClipboard(HWND owner)
    {
        for (int i = 0; i < 10; i++)
        {
            if (PInvoke.OpenClipboard(owner)) return true;
            Thread.Sleep(15);
        }
        Log.Write("Clipboard busy: could not open it");
        return false;
    }

    private static void Put(uint format, ReadOnlySpan<byte> data)
    {
        HGLOBAL g = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)data.Length);
        if (g.IsNull) return;
        void* p = PInvoke.GlobalLock(g);
        data.CopyTo(new Span<byte>(p, data.Length));
        PInvoke.GlobalUnlock(g);
        if (PInvoke.SetClipboardData(format, (HANDLE)(nint)g.Value).IsNull) PInvoke.GlobalFree(g);   // on success the clipboard owns it
    }
}

/// <summary>DPAPI, current-user scope: only this Windows account on this PC can read the stored images.</summary>
internal sealed unsafe class DpapiProtector : IBlobProtector
{
    public byte[] Protect(byte[] data) => Run(data, protect: true);
    public byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[] Run(byte[] data, bool protect)
    {
        fixed (byte* p = data)
        {
            var input = new CRYPT_INTEGER_BLOB { cbData = (uint)data.Length, pbData = p };
            CRYPT_INTEGER_BLOB output;
            const uint UiForbidden = 0x1;
            BOOL ok = protect
                ? PInvoke.CryptProtectData(&input, null, null, null, null, UiForbidden, &output)
                : PInvoke.CryptUnprotectData(&input, null, null, null, null, UiForbidden, &output);
            if (!ok) throw new System.Security.Cryptography.CryptographicException($"DPAPI {(protect ? "protect" : "unprotect")} failed");
            try { return new ReadOnlySpan<byte>(output.pbData, (int)output.cbData).ToArray(); }
            finally { PInvoke.LocalFree((HLOCAL)(nint)output.pbData); }
        }
    }
}
