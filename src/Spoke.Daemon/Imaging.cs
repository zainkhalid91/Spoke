using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;

namespace Spoke.Daemon;

/// <summary>A decoded image: 32-bit BGRA, top-down, stride = width × 4.</summary>
internal sealed class Pixels
{
    public required int Width, Height;
    public required byte[] Data;
    public int Stride => Width * 4;
}

/// <summary>
/// Windows Imaging Component helpers: PNG encode/decode, BMP (clipboard DIB) decode, thumbnails.
/// One instance per thread (each owns its WIC factory); the clipboard worker and the UI thread each have one.
/// </summary>
internal sealed unsafe class Imaging
{
    private readonly IWICImagingFactory* _wic;

    public Imaging()
    {
        Guid clsid = PInvoke.CLSID_WICImagingFactory, iid = IWICImagingFactory.IID_Guid;
        IWICImagingFactory* f;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&f).ThrowOnFailure();
        _wic = f;
    }

    /// <summary>Decodes PNG/BMP/JPEG/... bytes to BGRA. <paramref name="premultiplied"/> for D2D upload.</summary>
    public Pixels Decode(ReadOnlySpan<byte> bytes, bool premultiplied = false, int maxSide = 0)
    {
        IStream* stream = null;
        IWICBitmapDecoder* decoder = null;
        IWICBitmapFrameDecode* frame = null;
        IWICBitmapScaler* scaler = null;
        IWICFormatConverter* conv = null;
        try
        {
            fixed (byte* p = bytes) stream = PInvoke.SHCreateMemStream(p, (uint)bytes.Length);
            if (stream == null) throw new OutOfMemoryException("SHCreateMemStream");
            decoder = _wic->CreateDecoderFromStream(stream, null, WICDecodeOptions.WICDecodeMetadataCacheOnDemand);
            decoder->GetFrame(0, &frame);
            uint w, h;
            ((IWICBitmapSource*)frame)->GetSize(&w, &h);
            var source = (IWICBitmapSource*)frame;

            if (maxSide > 0 && (w > maxSide || h > maxSide))
            {
                float s = (float)maxSide / Math.Max(w, h);
                uint tw = Math.Max(1, (uint)MathF.Round(w * s)), th = Math.Max(1, (uint)MathF.Round(h * s));
                _wic->CreateBitmapScaler(&scaler);
                scaler->Initialize(source, tw, th, WICBitmapInterpolationMode.WICBitmapInterpolationModeHighQualityCubic);
                source = (IWICBitmapSource*)scaler;
                w = tw; h = th;
            }

            _wic->CreateFormatConverter(&conv);
            Guid fmt = premultiplied ? PInvoke.GUID_WICPixelFormat32bppPBGRA : PInvoke.GUID_WICPixelFormat32bppBGRA;
            conv->Initialize(source, &fmt, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);

            var data = GC.AllocateUninitializedArray<byte>(checked((int)(w * h * 4)));
            fixed (byte* d = data) ((IWICBitmapSource*)conv)->CopyPixels(null, w * 4, (uint)data.Length, d);
            return new Pixels { Width = (int)w, Height = (int)h, Data = data };
        }
        finally
        {
            Gfx.Release(conv); Gfx.Release(scaler); Gfx.Release(frame); Gfx.Release(decoder); Gfx.Release(stream);
        }
    }

    /// <summary>
    /// Encodes straight-alpha BGRA to PNG bytes. Always 32bppBGRA: the PNG encoder has no 32bppBGR input and
    /// silently switches to 24bpp, so callers make opaque images opaque by setting alpha to 255.
    /// </summary>
    public byte[] EncodePng(byte* pixels, int width, int height, int stride)
    {
        IStream* stream = PInvoke.SHCreateMemStream(null, 0);
        IWICBitmapEncoder* enc = null;
        IWICBitmapFrameEncode* frame = null;
        IPropertyBag2* bag = null;
        try
        {
            Guid png = PInvoke.GUID_ContainerFormatPng;
            enc = _wic->CreateEncoder(&png, null);
            enc->Initialize(stream, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache);
            enc->CreateNewFrame(&frame, &bag);
            frame->Initialize(bag);
            frame->SetSize((uint)width, (uint)height);
            Guid fmt = PInvoke.GUID_WICPixelFormat32bppBGRA;
            frame->SetPixelFormat(&fmt);
            if (fmt != PInvoke.GUID_WICPixelFormat32bppBGRA) throw new NotSupportedException("PNG encoder refused 32bppBGRA");
            frame->WritePixels((uint)height, (uint)stride, (uint)(stride * height), pixels);
            frame->Commit();
            enc->Commit();
            return ReadAll(stream);
        }
        finally
        {
            Gfx.Release(bag); Gfx.Release(frame); Gfx.Release(enc); Gfx.Release(stream);
        }
    }

    public byte[] EncodePng(Pixels p)
    {
        fixed (byte* d = p.Data) return EncodePng(d, p.Width, p.Height, p.Stride);
    }

    /// <summary>A PNG no larger than <paramref name="maxSide"/> on its long side.</summary>
    public byte[] Thumbnail(Pixels p, int maxSide)
    {
        if (p.Width <= maxSide && p.Height <= maxSide) return EncodePng(p);
        IWICBitmap* bmp = null;
        IWICBitmapScaler* scaler = null;
        try
        {
            Guid fmt = PInvoke.GUID_WICPixelFormat32bppBGRA;
            fixed (byte* d = p.Data)
                _wic->CreateBitmapFromMemory((uint)p.Width, (uint)p.Height, &fmt, (uint)p.Stride, (uint)p.Data.Length, d, &bmp);
            float s = (float)maxSide / Math.Max(p.Width, p.Height);
            uint tw = Math.Max(1, (uint)MathF.Round(p.Width * s)), th = Math.Max(1, (uint)MathF.Round(p.Height * s));
            _wic->CreateBitmapScaler(&scaler);
            scaler->Initialize((IWICBitmapSource*)bmp, tw, th, WICBitmapInterpolationMode.WICBitmapInterpolationModeHighQualityCubic);
            var data = new byte[tw * th * 4];
            fixed (byte* d = data) ((IWICBitmapSource*)scaler)->CopyPixels(null, tw * 4, (uint)data.Length, d);
            fixed (byte* d = data) return EncodePng(d, (int)tw, (int)th, (int)tw * 4);
        }
        finally
        {
            Gfx.Release(scaler); Gfx.Release(bmp);
        }
    }

    private static byte[] ReadAll(IStream* s)
    {
        STATSTG st;
        s->Stat(&st, 1 /* STATFLAG_NONAME */);
        var bytes = new byte[st.cbSize];
        s->Seek(0, SeekOrigin.Begin, null);
        fixed (byte* b = bytes)
        {
            uint read;
            s->Read(b, (uint)bytes.Length, &read);
        }
        return bytes;
    }

    /// <summary>
    /// Clipboard DIB (CF_DIB / CF_DIBV5 = BITMAPINFO + bits, no file header) → BMP file bytes, so WIC's BMP
    /// decoder can read it, including V5 headers with alpha and BI_BITFIELDS masks.
    /// </summary>
    public static byte[] DibToBmpFile(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40) throw new InvalidDataException("DIB too small");
        uint headerSize = MemoryMarshal.Read<uint>(dib);
        ushort bitCount = MemoryMarshal.Read<ushort>(dib[14..]);
        uint compression = MemoryMarshal.Read<uint>(dib[16..]);
        uint clrUsed = MemoryMarshal.Read<uint>(dib[32..]);
        uint masks = headerSize == 40 && compression == 3 /* BI_BITFIELDS */ ? 12u : 0u;
        uint palette = bitCount <= 8 ? (clrUsed != 0 ? clrUsed : 1u << bitCount) * 4 : clrUsed * 4;
        uint offBits = 14 + headerSize + masks + palette;

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B'; file[1] = (byte)'M';
        MemoryMarshal.Write(file.AsSpan(2), (uint)file.Length);
        MemoryMarshal.Write(file.AsSpan(10), offBits);
        dib.CopyTo(file.AsSpan(14));
        return file;
    }

    /// <summary>BGRA (top-down) → CF_DIB bytes: BITMAPINFOHEADER + bottom-up 32-bit rows.</summary>
    public static byte[] ToDib(Pixels p)
    {
        int rowBytes = p.Stride;
        var dib = new byte[40 + rowBytes * p.Height];
        var h = dib.AsSpan();
        MemoryMarshal.Write(h, 40u);
        MemoryMarshal.Write(h[4..], p.Width);
        MemoryMarshal.Write(h[8..], p.Height);            // positive = bottom-up, the most compatible
        MemoryMarshal.Write(h[12..], (ushort)1);
        MemoryMarshal.Write(h[14..], (ushort)32);
        MemoryMarshal.Write(h[20..], (uint)(rowBytes * p.Height));
        for (int y = 0; y < p.Height; y++)
            p.Data.AsSpan(y * rowBytes, rowBytes).CopyTo(dib.AsSpan(40 + (p.Height - 1 - y) * rowBytes));
        return dib;
    }
}
