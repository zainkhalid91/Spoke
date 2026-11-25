using System.Globalization;

namespace Spoke.Core;

/// <summary>Encrypts blobs at rest. The daemon uses DPAPI (current Windows user); tests use a pass-through.</summary>
public interface IBlobProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

/// <summary>One stored image. <see cref="Time"/> is Unix milliseconds of the last copy.</summary>
public sealed record ClipEntry(long Time, ulong Hash, int Width, int Height)
{
    public string Id => string.Create(CultureInfo.InvariantCulture, $"{Time:D13}_{Hash:x16}_{Width}x{Height}");

    public static bool TryParse(string id, out ClipEntry entry)
    {
        entry = null!;
        var p = id.Split('_');
        if (p.Length != 3) return false;
        var wh = p[2].Split('x');
        if (wh.Length != 2
            || !long.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out long t)
            || !ulong.TryParse(p[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h)
            || !int.TryParse(wh[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w)
            || !int.TryParse(wh[1], NumberStyles.None, CultureInfo.InvariantCulture, out int hh)) return false;
        entry = new ClipEntry(t, h, w, hh);
        return true;
    }
}

/// <summary>
/// History of copied images, newest first. Each entry is two encrypted files: the full PNG (.img) and a
/// small PNG thumbnail (.thb). The file name carries time, pixel hash and size, so no index file is needed.
/// Copying an image that is already stored moves it to the top instead of storing it twice.
/// Thread-safe: the clipboard worker adds, the UI thread reads and removes.
/// </summary>
public sealed class ClipStore
{
    private const string ImgExt = ".img", ThumbExt = ".thb";
    private readonly string _dir;
    private readonly IBlobProtector _protector;
    private readonly object _lock = new();
    private readonly List<ClipEntry> _entries = [];

    public int Limit { get; set; }

    public ClipStore(string dir, IBlobProtector protector, int limit = 40)
    {
        _dir = dir;
        _protector = protector;
        Limit = limit;
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(dir, "*" + ImgExt))
            if (ClipEntry.TryParse(Path.GetFileNameWithoutExtension(f), out var e) && File.Exists(PathOf(e, ThumbExt)))
                _entries.Add(e);
        _entries.Sort((a, b) => b.Time.CompareTo(a.Time));
        Prune();
    }

    /// <summary>Snapshot, newest first.</summary>
    public IReadOnlyList<ClipEntry> Entries { get { lock (_lock) return _entries.ToArray(); } }

    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>If an image with this hash is stored, moves it to the top and returns true.</summary>
    public bool TryBump(ulong hash, long time)
    {
        lock (_lock)
        {
            int i = _entries.FindIndex(e => e.Hash == hash);
            if (i < 0) return false;
            var old = _entries[i];
            var moved = old with { Time = Math.Max(time, old.Time) };
            if (moved.Time != old.Time)
            {
                File.Move(PathOf(old, ImgExt), PathOf(moved, ImgExt), true);
                File.Move(PathOf(old, ThumbExt), PathOf(moved, ThumbExt), true);
            }
            _entries.RemoveAt(i);
            _entries.Insert(0, moved);
            return true;
        }
    }

    public ClipEntry Add(byte[] png, byte[] thumbPng, int width, int height, ulong hash, long time)
    {
        if (TryBump(hash, time)) { lock (_lock) return _entries[0]; }
        var e = new ClipEntry(time, hash, width, height);
        // Write outside the lock (DPAPI + disk), then publish.
        File.WriteAllBytes(PathOf(e, ThumbExt), _protector.Protect(thumbPng));
        File.WriteAllBytes(PathOf(e, ImgExt), _protector.Protect(png));
        lock (_lock)
        {
            _entries.Insert(0, e);
            Prune();
        }
        return e;
    }

    public byte[]? ReadImage(ClipEntry e) => Read(e, ImgExt);
    public byte[]? ReadThumb(ClipEntry e) => Read(e, ThumbExt);

    private byte[]? Read(ClipEntry e, string ext)
    {
        try { return _protector.Unprotect(File.ReadAllBytes(PathOf(e, ext))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { return null; }
    }

    public void Remove(ClipEntry e)
    {
        lock (_lock)
        {
            _entries.RemoveAll(x => x.Hash == e.Hash);
            Delete(e);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var e in _entries) Delete(e);
            _entries.Clear();
        }
    }

    private void Prune()
    {
        while (_entries.Count > Limit)
        {
            Delete(_entries[^1]);
            _entries.RemoveAt(_entries.Count - 1);
        }
    }

    private void Delete(ClipEntry e)
    {
        File.Delete(PathOf(e, ImgExt));
        File.Delete(PathOf(e, ThumbExt));
    }

    private string PathOf(ClipEntry e, string ext) => Path.Combine(_dir, e.Id + ext);

    /// <summary>FNV-1a 64 over the pixels plus the size, so the same picture dedupes whichever format it was copied in.</summary>
    public static ulong HashPixels(ReadOnlySpan<byte> pixels, int width, int height)
    {
        ulong h = 14695981039346656037UL;
        h = (h ^ (uint)width) * 1099511628211UL;
        h = (h ^ (uint)height) * 1099511628211UL;
        // Hash 8 bytes at a time; exact FNV per byte isn't needed, only a stable, well-mixed value.
        var longs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(pixels);
        foreach (ulong v in longs) h = (h ^ v) * 1099511628211UL;
        for (int i = longs.Length * 8; i < pixels.Length; i++) h = (h ^ pixels[i]) * 1099511628211UL;
        return h;
    }

    /// <summary>"now", "2 min", "3 h", "2 d" for the hub.</summary>
    public static string Age(long time, long now)
    {
        long s = Math.Max(0, (now - time) / 1000);
        return s < 45 ? "now" : s < 3600 ? $"{(s + 30) / 60} min" : s < 86400 ? $"{s / 3600} h" : $"{s / 86400} d";
    }
}

/// <summary>Decides whether a clipboard change may be stored, honouring the formats apps use to opt out.</summary>
public static class ClipboardPolicy
{
    /// <param name="excludeFromMonitoring">"ExcludeClipboardContentFromMonitorProcessing" present (password managers).</param>
    /// <param name="canIncludeInHistory">Value of "CanIncludeInClipboardHistory" if present (0 = don't keep).</param>
    /// <param name="viewerIgnore">"Clipboard Viewer Ignore" present.</param>
    public static bool MayStore(bool paused, bool excludeFromMonitoring, uint? canIncludeInHistory, bool viewerIgnore) =>
        !paused && !excludeFromMonitoring && !viewerIgnore && canIncludeInHistory != 0;
}
