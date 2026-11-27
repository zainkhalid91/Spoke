using System.Text;
using System.Text.Json.Serialization;

namespace Spoke.Core;

public enum FileKind { Other, Image, Pdf, WordDoc, Spreadsheet, Presentation, Text }

/// <summary>What a file is, judged by extension (the conversion itself finds out whether it can really be read).</summary>
public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = FileKind.Image, [".jpg"] = FileKind.Image, [".jpeg"] = FileKind.Image, [".jfif"] = FileKind.Image, [".bmp"] = FileKind.Image,
        [".gif"] = FileKind.Image, [".tif"] = FileKind.Image, [".tiff"] = FileKind.Image, [".webp"] = FileKind.Image, [".heic"] = FileKind.Image,
        [".heif"] = FileKind.Image, [".avif"] = FileKind.Image, [".jxl"] = FileKind.Image, [".ico"] = FileKind.Image, [".dng"] = FileKind.Image,
        [".cr2"] = FileKind.Image, [".nef"] = FileKind.Image, [".arw"] = FileKind.Image, [".jxr"] = FileKind.Image, [".wdp"] = FileKind.Image,
        [".pdf"] = FileKind.Pdf,
        [".doc"] = FileKind.WordDoc, [".docx"] = FileKind.WordDoc, [".rtf"] = FileKind.WordDoc, [".odt"] = FileKind.WordDoc, [".docm"] = FileKind.WordDoc,
        [".xls"] = FileKind.Spreadsheet, [".xlsx"] = FileKind.Spreadsheet, [".xlsm"] = FileKind.Spreadsheet, [".ods"] = FileKind.Spreadsheet, [".csv"] = FileKind.Spreadsheet,
        [".ppt"] = FileKind.Presentation, [".pptx"] = FileKind.Presentation, [".odp"] = FileKind.Presentation, [".pps"] = FileKind.Presentation, [".ppsx"] = FileKind.Presentation,
        [".txt"] = FileKind.Text, [".md"] = FileKind.Text,
    };

    public static FileKind Of(string path) => ByExt.TryGetValue(Path.GetExtension(path), out var k) ? k : FileKind.Other;

    /// <summary>The kind shared by every file, or Other when they differ (or none).</summary>
    public static FileKind Common(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return FileKind.Other;
        var k = Of(paths[0]);
        return paths.All(p => Of(p) == k) ? k : FileKind.Other;
    }

    public static string Noun(FileKind k, int n) => (k, n == 1) switch
    {
        (FileKind.Image, true) => "image", (FileKind.Image, false) => "images",
        (FileKind.Pdf, true) => "PDF", (FileKind.Pdf, false) => "PDFs",
        (FileKind.WordDoc, true) => "document", (FileKind.WordDoc, false) => "documents",
        (FileKind.Spreadsheet, true) => "spreadsheet", (FileKind.Spreadsheet, false) => "spreadsheets",
        (FileKind.Presentation, true) => "presentation", (FileKind.Presentation, false) => "presentations",
        (_, true) => "file", _ => "files",
    };
}

// ───────────────────────── worker jobs ─────────────────────────

/// <summary>A conversion job, written by the daemon and run by <c>Spoke.exe --worker job.json</c>.</summary>
public sealed class FileJob
{
    /// <summary>convert | resize | compress | strip | combinePdf | office</summary>
    public string Op { get; set; } = "";
    /// <summary>convert/office: target extension without dot (png, jpg, pdf, docx…). resize: "50%" or "1280w".</summary>
    public string Arg { get; set; } = "";
    public List<string> Inputs { get; set; } = [];
    /// <summary>Where the worker writes its <see cref="FileJobResult"/>.</summary>
    public string ResultPath { get; set; } = "";
}

public sealed class FileJobResult
{
    public List<string> Outputs { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public long BytesIn { get; set; }
    public long BytesOut { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(FileJob))]
[JsonSerializable(typeof(FileJobResult))]
public sealed partial class FileJobJson : JsonSerializerContext;

/// <summary>Where results go: beside the original, never overwriting anything.</summary>
public static class OutputNames
{
    /// <summary>
    /// "photo.heic" → "photo.jpg" (tag null) or "photo (1280w).jpg" (tag "1280w"); if that exists, "photo (2).jpg" /
    /// "photo (1280w 2).jpg". <paramref name="exists"/> is injectable for tests.
    /// </summary>
    public static string For(string input, string? tag, string newExt, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        string dir = Path.GetDirectoryName(input) ?? "";
        string name = Path.GetFileNameWithoutExtension(input);
        string ext = newExt.StartsWith('.') ? newExt : "." + newExt;
        string first = tag == null ? name + ext : $"{name} ({tag}){ext}";
        string path = Path.Combine(dir, first);
        if (!exists(path) && !string.Equals(path, input, StringComparison.OrdinalIgnoreCase)) return path;
        for (int i = 2; i < 10000; i++)
        {
            path = Path.Combine(dir, tag == null ? $"{name} ({i}){ext}" : $"{name} ({tag} {i}){ext}");
            if (!exists(path)) return path;
        }
        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    /// <summary>"12.4 MB", "830 KB", "512 bytes".</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };
}

/// <summary>Resize presets: "50%" scales, "1280w" sets the width (height follows), "512" sets the long side.</summary>
public static class ResizeSpec
{
    /// <summary>Target size for an image. "50%" → half; "1280w" → 1280 wide (never enlarges); "512" → long side 512.</summary>
    public static (int W, int H)? Target(string spec, int w, int h)
    {
        spec = spec.Trim().ToLowerInvariant();
        if (spec.EndsWith('%') && float.TryParse(spec[..^1], System.Globalization.CultureInfo.InvariantCulture, out float pct) && pct is > 0 and < 100)
            return (Math.Max(1, (int)MathF.Round(w * pct / 100)), Math.Max(1, (int)MathF.Round(h * pct / 100)));
        if (spec.EndsWith('w') && int.TryParse(spec[..^1], out int tw) && tw > 0)
            return tw >= w ? null : (tw, Math.Max(1, (int)Math.Round((double)h * tw / w)));
        if (int.TryParse(spec, out int side) && side > 0)
        {
            int longSide = Math.Max(w, h);
            if (side >= longSide) return null;
            double s = (double)side / longSide;
            return (Math.Max(1, (int)Math.Round(w * s)), Math.Max(1, (int)Math.Round(h * s)));
        }
        return null;
    }
}

// ───────────────────────── small file writers (no dependencies) ─────────────────────────

/// <summary>Writes a PDF with one page per JPEG image: A4, oriented like the image, the image fitted with a margin.</summary>
public static class PdfWriter
{
    public sealed record Page(byte[] Jpeg, int Width, int Height, bool Gray = false);

    public static byte[] Build(IReadOnlyList<Page> pages, string title = "Spoke")
    {
        var ms = new MemoryStream();
        var offsets = new List<long>();
        void Raw(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b); }
        void Obj(int n, string body) { offsets.Add(ms.Position); Raw($"{n} 0 obj\n{body}\nendobj\n"); }

        Raw("%PDF-1.4\n%");
        ms.Write([0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);   // binary marker so tools treat the file as binary
        int n = pages.Count;
        // Object numbers: 1 catalog, 2 pages, 3 info, then per page: page, content, image.
        var kids = string.Join(" ", Enumerable.Range(0, n).Select(i => $"{4 + i * 3} 0 R"));
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, $"<< /Type /Pages /Count {n} /Kids [{kids}] >>");
        Obj(3, $"<< /Title ({Escape(title)}) /Producer (Spoke) >>");
        for (int i = 0; i < n; i++)
        {
            var p = pages[i];
            bool landscape = p.Width > p.Height;
            float pw = landscape ? 842 : 595, ph = landscape ? 595 : 842, margin = 24;
            float s = Math.Min((pw - 2 * margin) / p.Width, (ph - 2 * margin) / p.Height);
            float dw = p.Width * s, dh = p.Height * s, dx = (pw - dw) / 2, dy = (ph - dh) / 2;
            int pageObj = 4 + i * 3, contentObj = pageObj + 1, imageObj = pageObj + 2;
            Obj(pageObj, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pw:0} {ph:0}] /Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentObj} 0 R >>");
            string draw = FormattableString.Invariant($"q {dw:0.###} 0 0 {dh:0.###} {dx:0.###} {dy:0.###} cm /Im0 Do Q");
            Obj(contentObj, $"<< /Length {draw.Length} >>\nstream\n{draw}\nendstream");
            offsets.Add(ms.Position);
            Raw($"{imageObj} 0 obj\n<< /Type /XObject /Subtype /Image /Width {p.Width} /Height {p.Height} /ColorSpace /{(p.Gray ? "DeviceGray" : "DeviceRGB")} /BitsPerComponent 8 /Filter /DCTDecode /Length {p.Jpeg.Length} >>\nstream\n");
            ms.Write(p.Jpeg);
            Raw("\nendstream\nendobj\n");
        }
        long xref = ms.Position;
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R /Info 3 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Raw(sb.ToString());
        return ms.ToArray();
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s)
        {
            if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
            else if (c is >= ' ' and < (char)127) sb.Append(c);
            else sb.Append('?');
        }
        return sb.ToString();
    }
}

/// <summary>Writes a Windows .ico holding PNG-compressed images (Vista+ format; 256 px entries must be PNG anyway).</summary>
public static class IcoWriter
{
    public static readonly int[] StandardSizes = [16, 24, 32, 48, 64, 128, 256];

    public static byte[] Build(IReadOnlyList<(int Size, byte[] Png)> images)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
        int offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((ushort)1); w.Write((ushort)32);
            w.Write(png.Length); w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images) w.Write(png);
        return ms.ToArray();
    }
}

/// <summary>
/// Removes personal metadata without re-encoding: EXIF (camera, GPS, time), XMP, IPTC and comments from JPEG;
/// text, EXIF and time chunks from PNG. Colour profiles stay, so colours don't shift.
/// </summary>
public static class Metadata
{
    /// <summary>EXIF orientation (1–8) of a JPEG, 1 when absent.</summary>
    public static int JpegOrientation(ReadOnlySpan<byte> jpg)
    {
        if (jpg.Length < 4 || jpg[0] != 0xFF || jpg[1] != 0xD8) return 1;
        int i = 2;
        while (i + 4 <= jpg.Length && jpg[i] == 0xFF)
        {
            byte m = jpg[i + 1];
            if (m == 0xDA || m == 0xD9) break;
            int len = (jpg[i + 2] << 8) | jpg[i + 3];
            if (len < 2 || i + 2 + len > jpg.Length) break;
            if (m == 0xE1 && len >= 16 && jpg.Slice(i + 4, 6).SequenceEqual("Exif\0\0"u8))
                return TiffOrientation(jpg.Slice(i + 10, len - 8).ToArray());
            i += 2 + len;
        }
        return 1;
    }

    private static int TiffOrientation(byte[] t)
    {
        if (t.Length < 8) return 1;
        bool le = t[0] == 'I';
        int U16(int o) => o + 2 > t.Length ? 0 : le ? t[o] | (t[o + 1] << 8) : (t[o] << 8) | t[o + 1];
        int U32(int o) => o + 4 > t.Length ? 0 : le ? t[o] | (t[o + 1] << 8) | (t[o + 2] << 16) | (t[o + 3] << 24) : (t[o] << 24) | (t[o + 1] << 16) | (t[o + 2] << 8) | t[o + 3];
        int ifd = U32(4);
        if (ifd <= 0 || ifd + 2 > t.Length) return 1;
        int n = U16(ifd);
        for (int k = 0; k < n; k++)
        {
            int e = ifd + 2 + k * 12;
            if (e + 12 > t.Length) break;
            if (U16(e) == 0x0112) { int v = U16(e + 8); return v is >= 1 and <= 8 ? v : 1; }
        }
        return 1;
    }

    public static byte[]? StripJpeg(ReadOnlySpan<byte> jpg)
    {
        if (jpg.Length < 4 || jpg[0] != 0xFF || jpg[1] != 0xD8) return null;
        var o = new MemoryStream(jpg.Length);
        o.Write(jpg[..2]);
        int i = 2;
        while (i + 4 <= jpg.Length)
        {
            if (jpg[i] != 0xFF) return null;
            byte m = jpg[i + 1];
            if (m == 0xFF) { i++; continue; }   // fill byte
            if (m == 0xDA)
            {
                // Start of scan: copy through the first end-of-image marker (drops appended extra images such as
                // depth maps, which carry their own metadata). Entropy data escapes 0xFF as FF 00, so FF D9 is real.
                int end = i;
                while (end + 1 < jpg.Length && !(jpg[end] == 0xFF && jpg[end + 1] == 0xD9)) end++;
                o.Write(jpg[i..Math.Min(jpg.Length, end + 2)]);
                return o.ToArray();
            }
            if (m is >= 0xD0 and <= 0xD7 or 0x01) { o.Write(jpg.Slice(i, 2)); i += 2; continue; }
            int len = (jpg[i + 2] << 8) | jpg[i + 3];
            if (len < 2 || i + 2 + len > jpg.Length) return null;
            var seg = jpg.Slice(i, 2 + len);
            bool keep = m switch
            {
                0xE0 => true,                                                               // JFIF
                0xE2 => len >= 14 && seg.Slice(4, 12).SequenceEqual("ICC_PROFILE\0"u8),       // colour profile (not MPF)
                0xEE => true,                                                               // Adobe colour transform
                >= 0xE1 and <= 0xEF => false,                                               // EXIF, XMP, IPTC, vendor data
                0xFE => false,                                                              // comment
                _ => true,                                                                  // tables, frame header…
            };
            if (keep) o.Write(seg);
            i += 2 + len;
        }
        return null;
    }

    private static readonly HashSet<string> PngDrop = ["tEXt", "zTXt", "iTXt", "eXIf", "tIME"];

    public static byte[]? StripPng(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (png.Length < 8 || !png[..8].SequenceEqual(sig)) return null;
        var o = new MemoryStream(png.Length);
        o.Write(sig);
        int i = 8;
        while (i + 12 <= png.Length)
        {
            int len = (png[i] << 24) | (png[i + 1] << 16) | (png[i + 2] << 8) | png[i + 3];
            if (len < 0 || i + 12 + len > png.Length) return null;
            string type = Encoding.ASCII.GetString(png.Slice(i + 4, 4));
            if (!PngDrop.Contains(type)) o.Write(png.Slice(i, 12 + len));
            i += 12 + len;
            if (type == "IEND") return o.ToArray();
        }
        return null;
    }
}
