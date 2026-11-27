namespace Spoke.Core;

/// <summary>
/// How big a converted image will be, worked out before converting (drop mode shows it on every slice).
/// Small images are simply encoded whole. Big ones are sampled: full-resolution tiles from across the picture
/// are stitched into a mosaic, the mosaic is encoded, and the size scales up by pixel count. Tiles keep the
/// picture's real detail per pixel (a shrunken copy would not), and 16-pixel-aligned tiles keep JPEG blocks whole.
/// </summary>
public static class SizeEstimate
{
    /// <summary>Up to this many pixels the whole image is encoded: the estimate is exact and still quick.</summary>
    public const long WholeUpTo = 1_200_000;
    /// <summary>Small tiles from many places: best for PNG, JPEG, GIF and TIFF (measured on photos and screenshots).</summary>
    public const int Tile = 64;
    public const int GridMax = 16;
    /// <summary>WebP and AVIF predict across big blocks, so tile seams cost them more: fewer, larger tiles.</summary>
    public const int CodecTile = 128;
    public const int CodecGridMax = 8;

    /// <summary>A sampling plan: tile size, how many across and down, and where each tile comes from.</summary>
    public sealed record Plan(int TileW, int TileH, int Cols, int Rows, (int X, int Y)[] Origins)
    {
        public int MosaicW => TileW * Cols;
        public int MosaicH => TileH * Rows;
        public long MosaicPixels => (long)MosaicW * MosaicH;
    }

    /// <summary>Null when the image is small enough to encode whole.</summary>
    public static Plan? Sample(int width, int height, int tile = Tile, int gridMax = GridMax)
    {
        if (width <= 0 || height <= 0 || (long)width * height <= WholeUpTo) return null;
        int tw = Math.Min(tile, width), th = Math.Min(tile, height);
        int cols = Math.Clamp(width / tw, 1, gridMax), rows = Math.Clamp(height / th, 1, gridMax);
        var origins = new (int, int)[cols * rows];
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
            origins[r * cols + c] = (Spread(c, cols, width - tw), Spread(r, rows, height - th));
        return new Plan(tw, th, cols, rows, origins);
    }

    /// <summary>Evenly from 0 to <paramref name="max"/> (both ends included), on 16-pixel steps.</summary>
    private static int Spread(int i, int n, int max)
    {
        if (n <= 1 || max <= 0) return Math.Max(0, max / 2) & ~15;
        int v = (int)Math.Round(i * (double)max / (n - 1));
        return Math.Min(v & ~15, max);
    }

    /// <summary>The sample's encoded size scaled from its pixels to the whole picture's.</summary>
    public static long Scale(long sampleBytes, long samplePixels, long fullPixels, long overhead = 0)
    {
        if (samplePixels <= 0 || samplePixels == fullPixels) return sampleBytes;
        long body = Math.Max(0, sampleBytes - overhead);
        return overhead + (long)Math.Round(body * (double)fullPixels / samplePixels);
    }

    /// <summary>Bytes every file of this format carries whatever the picture (headers), which must not scale.</summary>
    public static long Overhead(string ext) => ext switch
    {
        "avif" => 300, "webp" => 40, "png" => 60, "gif" => 800, _ => 0,
    };

    /// <summary>A 24-bit BMP is exactly this big: headers plus rows padded to 4 bytes.</summary>
    public static long Bmp(int width, int height) => 54 + (long)((width * 3 + 3) & ~3) * height;

    /// <summary>"≈ 1.4 MB" for a slice; "?" when unknown.</summary>
    public static string Label(long bytes) => bytes < 0 ? "?" : "≈ " + OutputNames.Size(bytes);

    /// <summary>
    /// Several files: the ones sampled stand in for the rest, by their share of the bytes on disk
    /// (5 of 20 photos sampled, holding 25% of the bytes → their estimate × 4).
    /// </summary>
    public static long ForAll(long sampledEstimate, long sampledBytesOnDisk, long allBytesOnDisk)
        => sampledBytesOnDisk <= 0 || allBytesOnDisk <= sampledBytesOnDisk ? sampledEstimate
            : (long)Math.Round(sampledEstimate * (double)allBytesOnDisk / sampledBytesOnDisk);
}
