using System.Runtime.InteropServices;
using Spoke.Core;
using Windows.Win32;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;

namespace Spoke.Daemon;

/// <summary>Output formats Spoke can write, and how.</summary>
internal enum ImageFormat { Png, Jpeg, Bmp, Tiff, Gif, Ico, Heic, WebP, Avif }

/// <summary>Conversions for the worker process: decode any WIC-readable image upright, resize, encode to a format.</summary>
internal sealed unsafe partial class Imaging
{
    public static ImageFormat? FormatOf(string ext) => ext.TrimStart('.').ToLowerInvariant() switch
    {
        "png" => ImageFormat.Png, "jpg" or "jpeg" or "jfif" => ImageFormat.Jpeg, "bmp" => ImageFormat.Bmp,
        "tif" or "tiff" => ImageFormat.Tiff, "gif" => ImageFormat.Gif, "ico" => ImageFormat.Ico,
        "heic" or "heif" => ImageFormat.Heic, "webp" => ImageFormat.WebP, "avif" => ImageFormat.Avif,
        _ => null,
    };

    /// <summary>Whether WIC has an encoder for this container (HEIF: only with the HEIF Image Extensions).</summary>
    public static bool HasEncoder(Guid container)
    {
        try
        {
            var img = new Imaging();
            IWICBitmapEncoder* enc = null;
            try { enc = img._wic->CreateEncoder(&container, null); return true; }
            catch (COMException) { return false; }
            finally { Gfx.Release(enc); }
        }
        catch (COMException) { return false; }
    }

    public static string ExtOf(ImageFormat f) => f switch
    {
        ImageFormat.Jpeg => "jpg", ImageFormat.Tiff => "tif", _ => f.ToString().ToLowerInvariant(),
    };

    /// <summary>Reads an image file to straight BGRA, applying the JPEG EXIF orientation so it comes out upright.</summary>
    public Pixels LoadUpright(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        var px = Decode(bytes);
        int orientation = Metadata.JpegOrientation(bytes);
        return orientation == 1 ? px : Orient(px, orientation);
    }

    /// <summary>EXIF orientation 2–8 → the upright picture (pure pixel shuffling).</summary>
    public static Pixels Orient(Pixels p, int o)
    {
        bool swap = o >= 5;
        int w = p.Width, h = p.Height, nw = swap ? h : w, nh = swap ? w : h;
        var src = MemoryMarshal.Cast<byte, uint>(p.Data);
        var data = GC.AllocateUninitializedArray<byte>(p.Data.Length);
        var dst = MemoryMarshal.Cast<byte, uint>(data.AsSpan());
        for (int y = 0; y < nh; y++)
        for (int x = 0; x < nw; x++)
        {
            // Map each output pixel back to its source pixel.
            var (sx, sy) = o switch
            {
                2 => (w - 1 - x, y),
                3 => (w - 1 - x, h - 1 - y),
                4 => (x, h - 1 - y),
                5 => (y, x),
                6 => (y, h - 1 - x),
                7 => (w - 1 - y, h - 1 - x),
                8 => (w - 1 - y, x),
                _ => (x, y),
            };
            dst[y * nw + x] = src[sy * w + sx];
        }
        return new Pixels { Width = nw, Height = nh, Data = data };
    }

    public static bool HasAlpha(Pixels p)
    {
        var d = p.Data;
        for (int i = 3; i < d.Length; i += 4) if (d[i] != 255) return true;
        return false;
    }

    /// <summary>High-quality resize (Fant for shrinking: sharp, no aliasing).</summary>
    public Pixels Resize(Pixels p, int w, int h)
    {
        IWICBitmap* bmp = null;
        IWICBitmapScaler* scaler = null;
        try
        {
            Guid fmt = PInvoke.GUID_WICPixelFormat32bppBGRA;
            fixed (byte* d = p.Data) _wic->CreateBitmapFromMemory((uint)p.Width, (uint)p.Height, &fmt, (uint)p.Stride, (uint)p.Data.Length, d, &bmp);
            _wic->CreateBitmapScaler(&scaler);
            var mode = w < p.Width ? WICBitmapInterpolationMode.WICBitmapInterpolationModeFant : WICBitmapInterpolationMode.WICBitmapInterpolationModeHighQualityCubic;
            scaler->Initialize((IWICBitmapSource*)bmp, (uint)w, (uint)h, mode);
            var data = GC.AllocateUninitializedArray<byte>(w * h * 4);
            fixed (byte* d = data) ((IWICBitmapSource*)scaler)->CopyPixels(null, (uint)w * 4, (uint)data.Length, d);
            return new Pixels { Width = w, Height = h, Data = data };
        }
        finally { Gfx.Release(scaler); Gfx.Release(bmp); }
    }

    /// <summary>Encodes to a WIC format (PNG, JPEG, BMP, TIFF, GIF, HEIC) or ICO. JPEG/BMP/HEIC are flattened onto white.</summary>
    public byte[] Encode(Pixels p, ImageFormat f, float jpegQuality = 0.9f)
    {
        switch (f)
        {
            case ImageFormat.Png: return EncodePng(p);
            case ImageFormat.Ico: return EncodeIco(p);
            case ImageFormat.Jpeg: return EncodeWic(PInvoke.GUID_ContainerFormatJpeg, Flatten(p), PInvoke.GUID_WICPixelFormat24bppBGR, jpegQuality);
            case ImageFormat.Bmp: return EncodeWic(PInvoke.GUID_ContainerFormatBmp, Flatten(p), PInvoke.GUID_WICPixelFormat24bppBGR);
            case ImageFormat.Heic: return EncodeWic(PInvoke.GUID_ContainerFormatHeif, Flatten(p), PInvoke.GUID_WICPixelFormat24bppBGR, jpegQuality);
            case ImageFormat.Tiff: return EncodeWic(PInvoke.GUID_ContainerFormatTiff, (p.Data, p.Width, p.Height, p.Stride), PInvoke.GUID_WICPixelFormat32bppBGRA);
            case ImageFormat.Gif: return EncodeGif(p);
            default: throw new NotSupportedException($"{f} needs ffmpeg");
        }
    }

    /// <summary>BGRA → BGR over white (24-bit rows padded to 4 bytes).</summary>
    private static (byte[] Data, int W, int H, int Stride) Flatten(Pixels p)
    {
        int stride = (p.Width * 3 + 3) & ~3;
        var o = new byte[stride * p.Height];
        var s = p.Data;
        for (int y = 0; y < p.Height; y++)
        {
            int si = y * p.Stride, di = y * stride;
            for (int x = 0; x < p.Width; x++, si += 4, di += 3)
            {
                int a = s[si + 3];
                if (a == 255) { o[di] = s[si]; o[di + 1] = s[si + 1]; o[di + 2] = s[si + 2]; continue; }
                o[di] = (byte)((s[si] * a + 255 * (255 - a)) / 255);
                o[di + 1] = (byte)((s[si + 1] * a + 255 * (255 - a)) / 255);
                o[di + 2] = (byte)((s[si + 2] * a + 255 * (255 - a)) / 255);
            }
        }
        return (o, p.Width, p.Height, stride);
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct VarR4
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public float R4;
    }

    private byte[] EncodeWic(Guid container, (byte[] Data, int W, int H, int Stride) img, Guid pixelFormat, float quality = 0)
    {
        IStream* stream = PInvoke.SHCreateMemStream(null, 0);
        IWICBitmapEncoder* enc = null;
        IWICBitmapFrameEncode* frame = null;
        IPropertyBag2* bag = null;
        try
        {
            try { enc = _wic->CreateEncoder(&container, null); }
            catch (COMException) { throw new NotSupportedException("Windows has no encoder for this format (for HEIC, install HEIF + HEVC Video Extensions)."); }
            enc->Initialize(stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache);
            enc->CreateNewFrame(&frame, &bag);
            if (quality > 0 && bag != null)
            {
                fixed (char* name = "ImageQuality")
                {
                    var prop = new PROPBAG2 { pstrName = name };
                    var v = new VarR4 { Vt = 4 /* VT_R4 */, R4 = Math.Clamp(quality, 0.05f, 1f) };
                    try { bag->Write(1, &prop, (Windows.Win32.System.Variant.VARIANT*)&v); } catch (COMException) { }
                }
            }
            frame->Initialize(bag);
            frame->SetSize((uint)img.W, (uint)img.H);
            Guid fmt = pixelFormat;
            frame->SetPixelFormat(&fmt);
            if (fmt != pixelFormat) return EncodeConverted(enc, frame, stream, img, pixelFormat, fmt);
            fixed (byte* d = img.Data) frame->WritePixels((uint)img.H, (uint)img.Stride, (uint)img.Data.Length, d);
            frame->Commit();
            enc->Commit();
            return ReadAll(stream);
        }
        finally { Gfx.Release(bag); Gfx.Release(frame); Gfx.Release(enc); Gfx.Release(stream); }
    }

    /// <summary>The encoder wants another pixel format (e.g. HEIF wants 32bpp): convert through WIC and write the source.</summary>
    private byte[] EncodeConverted(IWICBitmapEncoder* enc, IWICBitmapFrameEncode* frame, IStream* stream, (byte[] Data, int W, int H, int Stride) img, Guid have, Guid want)
    {
        IWICBitmap* bmp = null;
        IWICFormatConverter* conv = null;
        try
        {
            fixed (byte* d = img.Data) _wic->CreateBitmapFromMemory((uint)img.W, (uint)img.H, &have, (uint)img.Stride, (uint)img.Data.Length, d, &bmp);
            _wic->CreateFormatConverter(&conv);
            conv->Initialize((IWICBitmapSource*)bmp, &want, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
            frame->WriteSource((IWICBitmapSource*)conv, null);
            frame->Commit();
            enc->Commit();
            return ReadAll(stream);
        }
        finally { Gfx.Release(conv); Gfx.Release(bmp); }
    }

    private byte[] EncodeGif(Pixels p)
    {
        IStream* stream = PInvoke.SHCreateMemStream(null, 0);
        IWICBitmapEncoder* enc = null;
        IWICBitmapFrameEncode* frame = null;
        IPropertyBag2* bag = null;
        IWICBitmap* bmp = null;
        IWICPalette* pal = null;
        IWICFormatConverter* conv = null;
        try
        {
            Guid bgra = PInvoke.GUID_WICPixelFormat32bppBGRA, indexed = PInvoke.GUID_WICPixelFormat8bppIndexed, gif = PInvoke.GUID_ContainerFormatGif;
            fixed (byte* d = p.Data) _wic->CreateBitmapFromMemory((uint)p.Width, (uint)p.Height, &bgra, (uint)p.Stride, (uint)p.Data.Length, d, &bmp);
            _wic->CreatePalette(&pal);
            bool alpha = HasAlpha(p);
            pal->InitializeFromBitmap((IWICBitmapSource*)bmp, 256, alpha);
            _wic->CreateFormatConverter(&conv);
            conv->Initialize((IWICBitmapSource*)bmp, &indexed, WICBitmapDitherType.WICBitmapDitherTypeErrorDiffusion, pal, alpha ? 50 : 0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
            enc = _wic->CreateEncoder(&gif, null);
            enc->Initialize(stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache);
            enc->CreateNewFrame(&frame, &bag);
            frame->Initialize(bag);
            frame->SetSize((uint)p.Width, (uint)p.Height);
            frame->SetPixelFormat(&indexed);
            frame->SetPalette(pal);
            frame->WriteSource((IWICBitmapSource*)conv, null);
            frame->Commit();
            enc->Commit();
            return ReadAll(stream);
        }
        finally
        {
            Gfx.Release(conv); Gfx.Release(pal); Gfx.Release(bmp);
            Gfx.Release(bag); Gfx.Release(frame); Gfx.Release(enc); Gfx.Release(stream);
        }
    }

    /// <summary>A square .ico (16–256 px, PNG entries). Non-square images are centred on a transparent square.</summary>
    private byte[] EncodeIco(Pixels p)
    {
        int side = Math.Max(p.Width, p.Height);
        var square = p;
        if (p.Width != p.Height)
        {
            var data = new byte[side * side * 4];
            int ox = (side - p.Width) / 2, oy = (side - p.Height) / 2;
            for (int y = 0; y < p.Height; y++)
                p.Data.AsSpan(y * p.Stride, p.Stride).CopyTo(data.AsSpan(((oy + y) * side + ox) * 4));
            square = new Pixels { Width = side, Height = side, Data = data };
        }
        var images = new List<(int, byte[])>();
        foreach (int s in IcoWriter.StandardSizes)
        {
            if (s > side && images.Count > 0) break;
            images.Add((s, EncodePng(s == side ? square : Resize(square, s, s))));
        }
        return IcoWriter.Build(images);
    }
}
