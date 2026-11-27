using Spoke.Core;

namespace Spoke.Core.Tests;

public class SizeEstimateTests
{
    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(1200, 1000)]
    [InlineData(0, 500)]
    public void Small_images_are_encoded_whole(int w, int h) => Assert.Null(SizeEstimate.Sample(w, h));

    [Fact]
    public void A_photo_is_sampled_with_a_full_grid_from_corner_to_corner()
    {
        var p = SizeEstimate.Sample(4032, 3024)!;
        Assert.Equal((16, 16), (p.Cols, p.Rows));
        Assert.Equal(1024 * 1024, p.MosaicPixels);
        Assert.Equal((0, 0), p.Origins[0]);
        var last = p.Origins[^1];
        Assert.InRange(last.X, 4032 - 64 - 15, 4032 - 64);    // reaches the far edge (16-pixel steps)
        Assert.InRange(last.Y, 3024 - 64 - 15, 3024 - 64);
        Assert.All(p.Origins, o => Assert.True(o.X % 16 == 0 && o.Y % 16 == 0, "tiles start on JPEG block edges"));
        Assert.All(p.Origins, o => Assert.True(o.X + p.TileW <= 4032 && o.Y + p.TileH <= 3024));
    }

    [Fact]
    public void A_thin_panorama_uses_its_whole_height()
    {
        var p = SizeEstimate.Sample(30000, 50)!;
        Assert.Equal(50, p.TileH);
        Assert.Equal(1, p.Rows);
        Assert.Equal(16, p.Cols);
    }

    [Fact]
    public void Codec_sampling_uses_bigger_tiles()
    {
        var p = SizeEstimate.Sample(3840, 2160, SizeEstimate.CodecTile, SizeEstimate.CodecGridMax)!;
        Assert.Equal((128, 8, 8), (p.TileW, p.Cols, p.Rows));
    }

    [Fact]
    public void Scaling_keeps_headers_out_of_the_multiplication()
    {
        Assert.Equal(4000, SizeEstimate.Scale(1000, 1_000_000, 4_000_000));
        Assert.Equal(300 + 4 * 700, SizeEstimate.Scale(1000, 1_000_000, 4_000_000, overhead: 300));
        Assert.Equal(1000, SizeEstimate.Scale(1000, 500, 500, overhead: 300));   // the whole image: exact
    }

    [Fact]
    public void Bmp_size_is_exact() => Assert.Equal(54 + 12L * 3, SizeEstimate.Bmp(3, 3));   // 9 bytes a row, padded to 12

    [Fact]
    public void A_batch_scales_by_bytes_on_disk()
    {
        Assert.Equal(400, SizeEstimate.ForAll(100, 1000, 4000));
        Assert.Equal(100, SizeEstimate.ForAll(100, 1000, 1000));
    }

    [Theory]
    [InlineData(1_468_006, "≈ 1.4 MB")]
    [InlineData(58_000, "≈ 57 KB")]
    [InlineData(-1, "?")]
    public void Labels(long bytes, string expected) => Assert.Equal(expected, SizeEstimate.Label(bytes));
}
